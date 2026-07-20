using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation;

/// <summary>
/// チーム力(0〜100)から試合可能な Team を構成する。集計モデルとフルエンジン(GameEngine)の
/// キャリブレーション、および注目試合の中解像度処理に使う（設計書05 §1.4）。
/// </summary>
public static class StrengthTeamFactory
{
    private static readonly FieldPosition[] FieldSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    // 控えの布陣（代打/代走/守備固め用）。守備固めが全ポジション成立するよう各守備位置に1人ずつ。
    private static readonly FieldPosition[] BenchSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    public static Team Create(double strength, string name, IRandomSource rng,
        PersonalityCoefficients? personalityCoeff = null, PlayerNameVocab? nameVocab = null)
    {
        var pc = personalityCoeff ?? new PersonalityCoefficients();
        var vocab = nameVocab ?? new PlayerNameVocab();
        var order = new List<Player>(9);
        // 性格・氏名は Fork（親状態非消費）で付与するため、既存の能力ロール列＝乱数消費列を1ビットも変えない（裏試合の決定論保存）。
        var salt = 0x50E1_0000UL;
        var nameSalt = 0x4E17_0000UL;   // 氏名用の Fork ソルト（主RNG非消費）
        // 背番号は高校野球の慣例に沿って採番: 先発の野手＝守備位置番号(2〜9)、エース＝1、控え野手＝10〜17、控え投手＝18〜。
        foreach (var pos in FieldSlots)
        {
            order.Add(PositionPlayer(pos, strength, rng, pc, salt++, GenName(vocab, rng, ref nameSalt))
                with { UniformNumber = PositionNumber(pos) });
        }
        order.Add(Pitcher(GenName(vocab, rng, ref nameSalt), strength, rng, pc, salt++)
            with { UniformNumber = 1 });

        var bullpen = new List<Player>
        {
            Pitcher(GenName(vocab, rng, ref nameSalt), strength - 4, rng, pc, salt++) with { UniformNumber = 18 },
            Pitcher(GenName(vocab, rng, ref nameSalt), strength - 2, rng, pc, salt++) with { UniformNumber = 19 },
        };
        // 背番号20（現代のベンチ入り20人制）。能力ロールは Fork＝主RNG非消費で決定論を保つ。
        var relief20 = rng.Fork(0x9A20_0000UL);
        bullpen.Add(Pitcher(GenName(vocab, rng, ref nameSalt), strength - 6, relief20, pc, salt++)
            with { UniformNumber = 20 });

        // 控え（全員生成・ベンチ入りメンバとして背番号10〜を採番）。能力・氏名とも Fork ストリーム由来＝
        // 主RNG消費を1ビットも変えない。Team.Tactics 未設定の相手校では MaybeOffenseSubs が Bench 前に早期 return するため、
        // 控えを持たせても現行の試合進行・裏試合の決定論には影響しない（采配ブレイン注入時のみ交代候補として機能）。
        var bench = new List<Player>(BenchSlots.Length);
        var benchSalt = 0x8E27_0000UL;
        for (var i = 0; i < BenchSlots.Length; i++)
        {
            var ability = rng.Fork(benchSalt++);   // 控えの能力ロールは Fork（主RNG非消費）
            bench.Add(PositionPlayer(BenchSlots[i], strength - 8, ability, pc, salt++, GenName(vocab, rng, ref nameSalt))
                with { UniformNumber = 10 + i });
        }

        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Bullpen = bullpen, Bench = bench };
    }

    /// <summary>守備位置→先発背番号（投1・捕2・一3・二4・三5・遊6・左7・中8・右9）。</summary>
    private static int PositionNumber(FieldPosition pos) => pos switch
    {
        FieldPosition.Pitcher => 1,
        FieldPosition.Catcher => 2,
        FieldPosition.FirstBase => 3,
        FieldPosition.SecondBase => 4,
        FieldPosition.ThirdBase => 5,
        FieldPosition.Shortstop => 6,
        FieldPosition.LeftField => 7,
        FieldPosition.CenterField => 8,
        FieldPosition.RightField => 9,
        _ => 0,
    };

    /// <summary>氏名を Fork（親状態非消費）で生成する。主RNGの消費列を変えないため決定論を保つ。</summary>
    private static string GenName(PlayerNameVocab vocab, IRandomSource rng, ref ulong nameSalt)
        => PlayerNameGenerator.Generate(vocab, rng.Fork(nameSalt++));

    private static int Ability(double center, IRandomSource rng)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(center, 6)), 10, 99);

    /// <summary>球種ランク（PitchRank 近傍の個体差）。</summary>
    private static int Grade(int pitchRank, IRandomSource rng)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(pitchRank, 8)), 1, 100);

    private static Player PositionPlayer(FieldPosition pos, double strength, IRandomSource rng,
        PersonalityCoefficients pc, ulong salt, string playerName)
    {
        // Ability() の呼び出し順を従来どおり保ち、乱数消費列を不変にする（決定論の後方互換, 2A）。
        var contact = Ability(strength, rng);
        var power = Ability(strength, rng);
        var launch = Ability(50, rng);
        var discipline = Ability(strength, rng);
        var speed = Ability(strength, rng);
        var arm = Ability(strength, rng);
        var fielding = Ability(strength, rng);
        var catching = Ability(strength, rng);
        // 性格（Fork＝親状態非消費）: 上の能力ロール列に影響しない。
        var personality = pc.Sample(rng.Fork(salt));
        var pp = pc.Profile(personality);
        return new Player
        {
            Name = playerName,
            Position = pos,
            Contact = contact,
            Power = power,
            LaunchTendency = launch,
            Discipline = discipline,
            Speed = speed,
            ArmStrength = arm,
            // 新パラメータは既存の抽選値から派生（新規RNG非消費）。正式生成は 2B。
            ThrowAccuracy = arm,
            Fielding = fielding,
            Catching = catching,
            Bunt = (int)MathUtil.Clamp(strength, 10, 99),
            Steal = speed,
            Baserunning = speed,
            Personality = personality,
            BuntSuccessBonus = pp.BuntSuccessBonus,
            ChanceHitFactor = pp.ChanceHitFactor,
        };
    }

    private static Player Pitcher(string name, double strength, IRandomSource rng,
        PersonalityCoefficients pc, ulong salt)
    {
        var contact = Ability(strength - 20, rng);
        var power = Ability(strength - 20, rng);
        var launch = Ability(50, rng);
        var discipline = Ability(strength, rng);
        var speed = Ability(strength, rng);
        var arm = Ability(strength, rng);
        var fielding = Ability(strength, rng);
        var catching = Ability(strength, rng);
        var control = Ability(strength, rng);
        var staminaLevel = Ability(strength, rng);
        var pitchRank = Ability(strength, rng);
        var pitching = new PitcherAttributes
        {
            MaxVelocityKmh = MathUtil.Clamp(120 + strength * 0.45, 118, 155),
            Control = control,
            StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staminaLevel),
            PitchRank = pitchRank,
            // 集計用の簡易レパートリー: ストレート＋変化球2種（ランクは PitchRank 近傍）。
            Repertoire = new[]
            {
                PitchSlot.FastballOf(pitchRank),
                new PitchSlot { Type = PitchType.Slider, Power = Grade(pitchRank, rng), Sharpness = Grade(pitchRank, rng) },
                new PitchSlot { Type = PitchType.Fork, Power = Grade(pitchRank, rng), Sharpness = Grade(pitchRank, rng) },
            },
        };
        // 性格（Fork＝親状態非消費）: 上の能力ロール列に影響しない。
        var personality = pc.Sample(rng.Fork(salt));
        var pp = pc.Profile(personality);
        return new Player
        {
            Name = name,
            Position = FieldPosition.Pitcher,
            Contact = contact,
            Power = power,
            LaunchTendency = launch,
            Discipline = discipline,
            Speed = speed,
            ArmStrength = arm,
            ThrowAccuracy = arm,
            Fielding = fielding,
            Catching = catching,
            Bunt = (int)MathUtil.Clamp(strength, 10, 99),
            Steal = speed,
            Baserunning = speed,
            Pitching = pitching,
            Personality = personality,
            BuntSuccessBonus = pp.BuntSuccessBonus,
            ChanceHitFactor = pp.ChanceHitFactor,
        };
    }
}
