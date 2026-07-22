using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
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

    /// <summary>
    /// 学校から決定論的にチームを組む（校ID＋年度シード）。大会展望の表示と実際の対戦相手ラインナップの
    /// **単一ソース**。同じ校・同じ年度なら常に同一チーム＝展望で見た選手が実戦でそのまま出てくる。
    /// 年度を混ぜるのは3年生が抜けて代替わりするため（年が変わればメンバーも変わる）。
    /// 渡す rng に依存しないので、大会の進行状況や観戦の有無に関係なく再現できる。
    /// <paramref name="modernRules"/>/<paramref name="calendarYear"/> は DH使用判断（issue #54）専用の
    /// 任意入力。両方揃ったときだけ検討する（既定 null＝DH不使用＝従来挙動と完全一致）。
    /// </summary>
    public static Team ForSchool(School school, int yearIndex,
        PersonalityCoefficients? personalityCoeff = null, PlayerNameVocab? nameVocab = null,
        RosterCoefficients? rosterCoeff = null, ModernRules? modernRules = null, int? calendarYear = null,
        EnemyAiCoefficients? aiCoeff = null, FormCoefficients? form = null)
        => Create(school.Strength, school.Name, SeedFor(school.Id, yearIndex),
            personalityCoeff, nameVocab, rosterCoeff, modernRules, calendarYear,
            school.ManagerTraits, aiCoeff, form);

    /// <summary>校ID＋年度から生成シードを導出する（決定論・不変条件#2）。</summary>
    public static IRandomSource SeedFor(int schoolId, int yearIndex)
        => new Xoshiro256Random(0x5C40_0000UL ^ (ulong)(long)schoolId ^ ((ulong)(long)yearIndex * 0x9E37_79B9UL));

    public static Team Create(double strength, string name, IRandomSource rng,
        PersonalityCoefficients? personalityCoeff = null, PlayerNameVocab? nameVocab = null,
        RosterCoefficients? rosterCoeff = null, ModernRules? modernRules = null, int? calendarYear = null,
        IReadOnlyList<ManagerTrait>? traits = null, EnemyAiCoefficients? aiCoeff = null,
        FormCoefficients? form = null)
    {
        var pc = personalityCoeff ?? new PersonalityCoefficients();
        var vocab = nameVocab ?? new PlayerNameVocab();
        var rc = rosterCoeff ?? new RosterCoefficients();
        var ac = rc.Archetypes;   // 球質タイプ係数は自校生成と同じ RosterCoefficients に同居
        var archSalt = 0x7B15_0000UL;   // 球質タイプ用の Fork ソルト（主RNG非消費）
        var order = new List<Player>(9);
        // 性格・氏名・投打・学年は Fork（親状態非消費）で付与するため、既存の能力ロール列＝乱数消費列を
        // 1ビットも変えない（裏試合の決定論保存）。
        var salt = 0x50E1_0000UL;
        var nameSalt = 0x4E17_0000UL;      // 氏名用の Fork ソルト（主RNG非消費）
        // 同一チーム内で下の名前が被らないよう、採用済みの名前を持ち回る（苗字の重複はOK＝現実にもある）。
        var usedGiven = new HashSet<string>();
        var profileSalt = 0x6A17_0000UL;   // 投打・学年用の Fork ソルト（主RNG非消費）
        // 背番号は高校野球の慣例に沿って採番: 先発の野手＝守備位置番号(2〜9)、エース＝1、控え野手＝10〜17、控え投手＝18〜。
        foreach (var pos in FieldSlots)
        {
            var pr = Profile(rc, rng, ref profileSalt, starter: true);
            order.Add(PositionPlayer(pos, strength, rng, pc, salt++, GenName(vocab, rng, ref nameSalt, usedGiven))
                with { UniformNumber = PositionNumber(pos), Throws = pr.Throws, Bats = pr.Bats, Grade = pr.Grade });
        }
        var acePr = Profile(rc, rng, ref profileSalt, starter: true);
        order.Add(Pitcher(GenName(vocab, rng, ref nameSalt, usedGiven), strength, rng, pc, salt++, ac, archSalt++)
            with { UniformNumber = 1, Throws = acePr.Throws, Bats = acePr.Bats, Grade = acePr.Grade });

        var bullpen = new List<Player>();
        foreach (var (number, drop) in new[] { (18, 4.0), (19, 2.0) })
        {
            var pr = Profile(rc, rng, ref profileSalt, starter: false);
            bullpen.Add(Pitcher(GenName(vocab, rng, ref nameSalt, usedGiven), strength - drop, rng, pc, salt++, ac, archSalt++)
                with { UniformNumber = number, Throws = pr.Throws, Bats = pr.Bats, Grade = pr.Grade });
        }
        // 背番号20（現代のベンチ入り20人制）。能力ロールは Fork＝主RNG非消費で決定論を保つ。
        var relief20 = rng.Fork(0x9A20_0000UL);
        var pr20 = Profile(rc, rng, ref profileSalt, starter: false);
        bullpen.Add(Pitcher(GenName(vocab, rng, ref nameSalt, usedGiven), strength - 6, relief20, pc, salt++, ac, archSalt++)
            with { UniformNumber = 20, Throws = pr20.Throws, Bats = pr20.Bats, Grade = pr20.Grade });

        // 控え（全員生成・ベンチ入りメンバとして背番号10〜を採番）。能力・氏名とも Fork ストリーム由来＝
        // 主RNG消費を1ビットも変えない。Team.Tactics 未設定の相手校では MaybeOffenseSubs が Bench 前に早期 return するため、
        // 控えを持たせても現行の試合進行・裏試合の決定論には影響しない（采配ブレイン注入時のみ交代候補として機能）。
        var bench = new List<Player>(BenchSlots.Length);
        var benchSalt = 0x8E27_0000UL;
        for (var i = 0; i < BenchSlots.Length; i++)
        {
            var ability = rng.Fork(benchSalt++);   // 控えの能力ロールは Fork（主RNG非消費）
            var pr = Profile(rc, rng, ref profileSalt, starter: false);
            bench.Add(PositionPlayer(BenchSlots[i], strength - 8, ability, pc, salt++, GenName(vocab, rng, ref nameSalt, usedGiven))
                with { UniformNumber = 10 + i, Throws = pr.Throws, Bats = pr.Bats, Grade = pr.Grade });
        }

        // 監督傾向・抜擢型（issue #55）: Compose の前に控えを先発へ抜擢する（オーダー編成に効く傾向）。
        // 調子ロールは主RNGを乱さない Fork ストリーム＝既存の能力生成列を1ビットも変えない（帯・決定論保護）。
        var ai = aiCoeff ?? new EnemyAiCoefficients();
        if (ManagerTraitEffects.HasPromoter(traits))
        {
            ApplyPromoter(order, bench, rng.Fork(0x9C55_0000UL), rc.Lineup, ai, form ?? new FormCoefficients());
        }

        var team = new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Bullpen = bullpen, Bench = bench };
        // 打順編成＋DH使用判断（issue #54, 設計書11 §4）。既に確定した能力ロールだけを材料にする決定論変換
        // （rng は使わない）＝展望（TournamentPreview）と実戦（Shell）が同じここを通り自動で一致する。
        var composed = LineupOrderer.Compose(team, rc.Lineup, modernRules, calendarYear);
        // 監督傾向・継投系（issue #55, 決定4: B-1）: エース酷使/継投早めのチームだけ継投しきい値を差し替える。
        // 傾向なしは null＝GameContext.Fatigue に落ちる（従来と完全一致・帯不変）。
        return composed with { Fatigue = ManagerTraitEffects.FatigueOverride(new FatigueCoefficients(), traits, ai) };
    }

    /// <summary>
    /// 抜擢型（issue #55, 監督傾向 <see cref="ManagerTrait.Promoter"/>）。控え（背番号10〜）に決定論の調子を振り、
    /// 好調（Good以上）の控えを、同じ守備位置の先発より「調子込みの起用価値」が上回るなら先発へ抜擢する
    /// （押し出された正位置選手はベンチへ＝守備固め/代打候補として #40 の交代判断に乗る）。調子ロールは渡された
    /// 隔離ストリーム（校ID＋年度から Fork）で順に引くので、大会展望（ForSchool 経由）と実戦が同一結果になる。
    /// order/bench を破壊的に更新する（Compose の前に呼ぶ）。
    /// </summary>
    private static void ApplyPromoter(
        List<Player> order, List<Player> bench, IRandomSource rng,
        LineupCoefficients lineup, EnemyAiCoefficients ai, FormCoefficients form)
    {
        if (bench.Count == 0) return;
        var sigma = form.StationaryConditionSigma;

        var bestIdx = -1;
        var bestStep = 0;
        var bestScore = double.NegativeInfinity;
        var bestCv = 0.0;
        var bestCond = Condition.Normal;
        for (var i = 0; i < bench.Count; i++)
        {
            // 全控えを1回ずつロール（分岐に関係なく消費数を固定＝決定論）。
            var cv = rng.NextGaussian(0, sigma);
            var cond = FormModel.Quantize(cv);
            var step = FormModel.Step(cond);
            if (step < ai.PromoterMinConditionStep) continue;   // Good未満は抜擢対象外
            var score = LineupOrderer.BattingScore(bench[i], lineup) + ai.PromoterConditionWeight * step;
            if (step > bestStep || (step == bestStep && score > bestScore))
            {
                bestIdx = i;
                bestStep = step;
                bestScore = score;
                bestCv = cv;
                bestCond = cond;
            }
        }
        if (bestIdx < 0) return;   // 今年は好調の控えがいない＝抜擢なし

        var benchPlayer = bench[bestIdx];
        // 同じ守備位置の先発（末尾＝投手枠は除外）を探す。
        var slot = -1;
        for (var i = 0; i < order.Count - 1; i++)
        {
            if (order[i].Position == benchPlayer.Position) { slot = i; break; }
        }
        if (slot < 0) return;

        var starter = order[slot];
        // 抜擢の価値があるとき（調子込みで先発の素の打撃を上回る）だけ入れ替える＝諸刃を無闇に負わない。
        if (bestScore <= LineupOrderer.BattingScore(starter, lineup)) return;

        order[slot] = benchPlayer with { Condition = bestCond, ConditionValue = bestCv };
        bench[bestIdx] = starter;   // 押し出された先発はベンチへ（守備位置は同一）
    }

    /// <summary>
    /// 投打の利き・学年を Fork（親状態非消費）で抽選する。主RNGの消費列を変えないため決定論を保つ。
    /// 投打の分布は ProspectGenerator.Create（設計書01 §1.1c）と同じ条件付き抽選＝自校と同一の傾向にする。
    /// 学年は先発を上級生寄り・控えを下級生寄りに配分する（高校野球の実感に沿う）。
    /// </summary>
    internal static (Handedness Throws, Handedness Bats, int Grade) Profile(
        RosterCoefficients c, IRandomSource rng, ref ulong profileSalt, bool starter)
    {
        var r = rng.Fork(profileSalt++);
        var throws = r.NextDouble() < c.ThrowLeftProb ? Handedness.Left : Handedness.Right;
        Handedness bats;
        if (r.NextDouble() < c.SwitchProb) bats = Handedness.Switch;
        else if (throws == Handedness.Left) bats = r.NextDouble() < c.BatLeftGivenLeftThrow ? Handedness.Left : Handedness.Right;
        else bats = r.NextDouble() < c.BatLeftGivenRightThrow ? Handedness.Left : Handedness.Right;

        var g = r.NextDouble();
        var grade = starter
            ? (g < 0.50 ? 3 : g < 0.85 ? 2 : 1)    // 先発: 3年5割・2年35%・1年15%
            : (g < 0.20 ? 3 : g < 0.55 ? 2 : 1);   // 控え: 3年2割・2年35%・1年45%
        return (throws, bats, grade);
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

    /// <summary>
    /// 氏名を Fork（親状態非消費）で生成する。主RNGの消費列を変えないため決定論を保つ。
    /// 下の名前の重複回避リロールも Fork ストリーム内で完結する。
    /// </summary>
    private static string GenName(PlayerNameVocab vocab, IRandomSource rng, ref ulong nameSalt,
        ISet<string> usedGiven)
        => PlayerNameGenerator.Generate(vocab, rng.Fork(nameSalt++), usedGiven);

    internal static int Ability(double center, IRandomSource rng)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(center, 6)), 10, 99);

    /// <summary>球質タイプのオフセットを載せたレベルを能力域へ丸める。</summary>
    private static int Lv(double level) => (int)MathUtil.Clamp(Math.Round(level), 10, 99);

    /// <summary>球種ランク（PitchRank 近傍の個体差）。</summary>
    private static int Grade(int pitchRank, IRandomSource rng)
        => (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(pitchRank, 8)), 1, 100);

    internal static Player PositionPlayer(FieldPosition pos, double strength, IRandomSource rng,
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

    internal static Player Pitcher(string name, double strength, IRandomSource rng,
        PersonalityCoefficients pc, ulong salt, PitcherArchetypeCoefficients ac, ulong archetypeSalt)
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
        // 球速も他能力と同じく個体差を持つ（旧実装は強さの決定関数で、同じ強さの学校のエースが全員同じ球速＝
        // AI校に技巧派が存在できなかった）。自校（RosterTeamBuilder）と同じ「球速Level→km/h」の形に揃える。
        var velocityLevel = Ability(strength, rng);

        // 球質タイプで配分を振り替える（総合はほぼ不変・Fork＝主RNG非消費）。
        var archetype = PitcherArchetypes.Sample(rng.Fork(archetypeSalt), ac);
        var (dv, dc, ds, dr) = PitcherArchetypes.Offsets(archetype, ac);
        velocityLevel = Lv(velocityLevel + dv);
        control = Lv(control + dc);
        staminaLevel = Lv(staminaLevel + ds);
        pitchRank = Lv(pitchRank + dr);

        var pitching = new PitcherAttributes
        {
            MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velocityLevel),
            Archetype = archetype,
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
