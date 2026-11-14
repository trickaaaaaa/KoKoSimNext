using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Tests.Saves;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// 特殊能力の見直し第1弾（issue #907・設計書10 §0.1/§2.1〜2.3）。新しいスキルごとに「持っていると狙った向きに
/// 結果が動く」こと、削除・置き換えたスキル名を含む古いセーブが読めること、生成の約束（投手系＝投手寄り・
/// 野手系＝野手寄り・◎と×は同時に付かない）を固定する。
/// </summary>
public sealed class SkillReviewTests
{
    private static readonly SkillCoefficients C = new();
    private static readonly PressureCoefficients P = new();
    private static readonly FieldGeometry Field = new();

    // ===== 打席の集計（打席単位の実測。フルゲームより軽い＝日常ループに置ける） =====

    private sealed record Tally(int Pa, int Hits, int AtBats, int Strikeouts, int OnBase,
        IReadOnlyList<(BattedBall Ball, PlateAppearanceResult Result)> Balls)
    {
        public double Avg => (double)Hits / AtBats;
        public double KRate => (double)Strikeouts / Pa;
        public double Obp => (double)OnBase / Pa;
    }

    private static bool IsHit(PlateAppearanceResult r) => r is PlateAppearanceResult.Single or PlateAppearanceResult.Double
        or PlateAppearanceResult.Triple or PlateAppearanceResult.HomeRun;

    /// <summary>同じシード列で <paramref name="pas"/> 打席を解く（共通乱数＝スキルの有無だけの差を見る）。</summary>
    private static Tally Run(BatterAttributes batter, PitcherAttributes pitcher, SkillPlayMods mods, int pas = 6000,
        bool runnersOn = false)
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), Skills = mods, RunnersOn = runnersOn };
        var results = new AtBatResult[pas];
        System.Threading.Tasks.Parallel.For(0, pas, i =>
            results[i] = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, new Xoshiro256Random(9000UL + (ulong)i)));
        int hits = 0, ab = 0, k = 0, ob = 0;
        var balls = new List<(BattedBall, PlateAppearanceResult)>();
        foreach (var r in results)
        {
            var res = r.Result;
            if (res is not (PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch)) ab++;
            if (IsHit(res)) hits++;
            if (res == PlateAppearanceResult.Strikeout) k++;
            if (IsHit(res) || res is PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch) ob++;
            var last = r.PitchLog?.LastOrDefault(x => x.Kind == PitchKind.InPlay);
            if (last?.BattedBall is { } b) balls.Add((b, res));
        }
        return new Tally(pas, hits, ab, k, ob, balls);
    }

    private static SkillPlayMods BatterMods(Skill s, bool runnersOn = false)
        => SkillModel.PlayMods(new SkillSet(new[] { s }), SkillSet.Empty, 0, C, runnersOn: runnersOn);

    private static PitcherAttributes FastballOnly => new() { Repertoire = new[] { PitchSlot.FastballOf(50) } };

    private static PitcherAttributes BreakingHeavy => new()
    {
        Repertoire = new[]
        {
            PitchSlot.FastballOf(50),
            new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 60 },
            new PitchSlot { Type = PitchType.Fork, Power = 50, Sharpness = 60 },
        },
    };

    // ===== 速球打ち・変化球打ち =====

    [Fact]
    public void ForPitch_FastballHitter_OnlyOnFastballFamily()
    {
        var mods = BatterMods(Skill.FastballHitter);
        var b = new BatterAttributes { Contact = 50, Discipline = 50 };
        Assert.Equal(56, mods.ForPitch(b, PitchType.Fastball).Contact);
        Assert.Equal(56, mods.ForPitch(b, PitchType.TwoSeam).Contact);
        Assert.Same(b, mods.ForPitch(b, PitchType.Slider));
    }

    [Fact]
    public void ForPitch_BreakingBallHitter_OnlyOnBreakingBalls()
    {
        var mods = BatterMods(Skill.BreakingBallHitter);
        var b = new BatterAttributes { Contact = 50, Discipline = 50 };
        Assert.Same(b, mods.ForPitch(b, PitchType.Fastball));
        var s = mods.ForPitch(b, PitchType.Fork);
        Assert.Equal(55, s.Contact);
        Assert.Equal(56, s.Discipline);
    }

    [Fact]
    public void FastballHitter_StrikesOutLess_AndHitsMore_AgainstFastballs()
    {
        var b = BatterAttributes.LeagueAverage;
        var plain = Run(b, FastballOnly, SkillPlayMods.None);
        var skilled = Run(b, FastballOnly, BatterMods(Skill.FastballHitter));
        Assert.True(skilled.KRate < plain.KRate, $"K% {skilled.KRate:P2} < {plain.KRate:P2}");
        Assert.True(skilled.Avg > plain.Avg, $"打率 {skilled.Avg:F3} > {plain.Avg:F3}");
    }

    [Fact]
    public void BreakingBallHitter_StrikesOutLess_AgainstBreakingBalls()
    {
        var b = BatterAttributes.LeagueAverage;
        var plain = Run(b, BreakingHeavy, SkillPlayMods.None);
        var skilled = Run(b, BreakingHeavy, BatterMods(Skill.BreakingBallHitter));
        Assert.True(skilled.KRate < plain.KRate, $"K% {skilled.KRate:P2} < {plain.KRate:P2}");
        Assert.True(skilled.Obp > plain.Obp, $"出塁率 {skilled.Obp:F3} > {plain.Obp:F3}");
    }

    // ===== 流し打ち =====

    [Fact]
    public void OppositeFieldHitter_HitsHarderToTheOppositeField()
    {
        var b = new BatterAttributes { Contact = 60, Power = 60, Bats = Handedness.Right };
        var mods = BatterMods(Skill.OppositeFieldHitter);
        var plain = Run(b, PitcherAttributes.LeagueAverage, SkillPlayMods.None);
        var skilled = Run(b, PitcherAttributes.LeagueAverage, mods);

        double OppoExit(Tally t) => t.Balls.Where(x => mods.BattedBall.IsOppositeField(x.Ball.BearingDeg, b.Bats))
            .Average(x => x.Ball.ExitVelocityKmh);
        // 引っ張りの縮小（旧 広角打法の方向の質）は左右両端を中堅へ寄せるので、逆方向の「割合」は増えない＝見るのは打球の強さ。
        Assert.True(OppoExit(skilled) > OppoExit(plain) + 3.0,
            $"逆方向の打球初速 {OppoExit(skilled):F1} > {OppoExit(plain):F1} km/h");
        Assert.True(skilled.Avg > plain.Avg, $"打率 {skilled.Avg:F3} > {plain.Avg:F3}");
    }

    [Fact]
    public void OppositeField_IsMirroredForLeftHanders()
    {
        var m = new BattedBallSkillMods(0.06, 10.0, 0, 0, 0);
        Assert.True(m.IsOppositeField(20, Handedness.Right));   // 右打者の右翼＝逆方向
        Assert.False(m.IsOppositeField(-20, Handedness.Right));
        Assert.True(m.IsOppositeField(-20, Handedness.Left));   // 左打者の左翼＝逆方向
        Assert.False(m.IsOppositeField(5, Handedness.Right));   // 中堅寄りは逆方向に数えない
    }

    // ===== つなぎの打撃 =====

    [Fact]
    public void TeamBatting_WithRunners_MoreRightSideGrounders_FewerHits()
    {
        var b = new BatterAttributes { Contact = 55, Power = 55, Bats = Handedness.Right };
        var plain = Run(b, PitcherAttributes.LeagueAverage, SkillPlayMods.None, runnersOn: true);
        var skilled = Run(b, PitcherAttributes.LeagueAverage, BatterMods(Skill.TeamBatting, runnersOn: true), runnersOn: true);

        // 進塁打の形＝右方向（一塁側）のゴロ。
        static double RightGrounders(Tally t)
            => (double)t.Balls.Count(x => x.Ball.BearingDeg > 0 && x.Ball.LaunchAngleDeg < 10) / t.Balls.Count;
        Assert.True(RightGrounders(skilled) > RightGrounders(plain) + 0.015,
            $"右方向のゴロ {RightGrounders(skilled):P1} > {RightGrounders(plain):P1}");
        Assert.True(skilled.Avg < plain.Avg, $"打率 {skilled.Avg:F3} < {plain.Avg:F3}");
    }

    [Fact]
    public void TeamBatting_WithoutRunners_IsIdentity()
    {
        var mods = BatterMods(Skill.TeamBatting, runnersOn: false);
        Assert.True(mods.IsIdentity);
    }

    // ===== チャンス◎×・ピンチ◎×（設計書10 §0.1） =====

    [Fact]
    public void ClutchTrait_LiftsTheLowMentalDropToASmallPlus()
    {
        var none = PressureModel.Multiplier(20, 4, P);
        var clutch = PressureModel.Multiplier(20, 4, P, trait: PressureTrait.Clutch, skills: C);
        Assert.True(none < 1.0);
        Assert.Equal(1.0 + C.ClutchFloorPerPressure * 4, clutch, 9);
        // 精神力が高くて既にプラスなら上に積まない（精神力と二重計上しない）。
        Assert.Equal(PressureModel.Multiplier(90, 4, P),
            PressureModel.Multiplier(90, 4, P, trait: PressureTrait.Clutch, skills: C), 9);
    }

    [Fact]
    public void ChokeTrait_AmplifiesTheDrop_AndDropsEvenHighMental()
    {
        var none = PressureModel.Multiplier(20, 4, P);
        var choke = PressureModel.Multiplier(20, 4, P, trait: PressureTrait.Choke, skills: C);
        Assert.Equal(1.0 + (none - 1.0) * C.ChokeNegativeAmplify, choke, 9);
        Assert.True(PressureModel.Multiplier(90, 4, P, trait: PressureTrait.Choke, skills: C) < 1.0);
        Assert.Equal(1.0, PressureModel.Multiplier(50, 0, P, trait: PressureTrait.Choke, skills: C)); // 圧が無ければ恒等
    }

    [Fact]
    public void PressureTraits_ReadFromSkills_PairsCancel()
    {
        Assert.Equal(PressureTrait.Clutch, SkillModel.BatterPressureTrait(new SkillSet(new[] { Skill.ClutchHitter })));
        Assert.Equal(PressureTrait.Choke, SkillModel.BatterPressureTrait(new SkillSet(new[] { Skill.ChokeHitter })));
        Assert.Equal(PressureTrait.None, SkillModel.BatterPressureTrait(new SkillSet(new[] { Skill.ClutchPitcher })));
        Assert.Equal(PressureTrait.Clutch, SkillModel.PitcherPressureTrait(new SkillSet(new[] { Skill.ClutchPitcher })));
        Assert.Equal(PressureTrait.Choke, SkillModel.PitcherPressureTrait(new SkillSet(new[] { Skill.ChokePitcher })));
        Assert.Equal(PressureTrait.None,
            SkillModel.PitcherPressureTrait(new SkillSet(new[] { Skill.ClutchPitcher, Skill.ChokePitcher })));
    }

    [Fact]
    public void ChanceSkills_MoveTheBattingAverage_UnderPressure()
    {
        // 得点圏・P=4 の精神力20の打者: ◎は低下が消え、×は低下が倍になる＝打率が ◎ > なし > ×。
        var b = new BatterAttributes { Contact = 60, Power = 55 };
        BatterAttributes Under(PressureTrait t)
            => PressureModel.ApplyBatter(b, PressureModel.Multiplier(20, 4, P, trait: t, skills: C));
        var none = Run(Under(PressureTrait.None), PitcherAttributes.LeagueAverage, SkillPlayMods.None);
        var clutch = Run(Under(PressureTrait.Clutch), PitcherAttributes.LeagueAverage, SkillPlayMods.None);
        var choke = Run(Under(PressureTrait.Choke), PitcherAttributes.LeagueAverage, SkillPlayMods.None);
        Assert.True(clutch.Avg > none.Avg && none.Avg > choke.Avg,
            $"打率 ◎{clutch.Avg:F3} > なし{none.Avg:F3} > ×{choke.Avg:F3}");
    }

    [Fact]
    public void PinchSkills_MoveTheOnBaseAgainst_UnderPressure()
    {
        var p = new PitcherAttributes { Control = 55 };
        PitcherAttributes Under(PressureTrait t)
            => PressureModel.ApplyPitcher(p, PressureModel.Multiplier(20, 4, P, trait: t, skills: C));
        var b = BatterAttributes.LeagueAverage;
        var clutch = Run(b, Under(PressureTrait.Clutch), SkillPlayMods.None);
        var choke = Run(b, Under(PressureTrait.Choke), SkillPlayMods.None);
        Assert.True(clutch.Obp < choke.Obp, $"被出塁率 ◎{clutch.Obp:F3} < ×{choke.Obp:F3}");
    }

    // ===== 立ち上がり難・火消し =====

    [Fact]
    public void ShakyStart_HurtsOnlyTheFirstTwoInnings()
    {
        var p = new PitcherAttributes { Control = 60 };
        var skills = new SkillSet(new[] { Skill.ShakyStart });
        Assert.Equal(54, SkillModel.ApplyPitcher(p, skills, 0, C, inning: 1).Control);
        Assert.Equal(54, SkillModel.ApplyPitcher(p, skills, 5, C, inning: 2).Control);
        Assert.Equal(60, SkillModel.ApplyPitcher(p, skills, 9, C, inning: 3).Control);
        Assert.Equal(-C.ShakyStartStuffPenalty, SkillModel.PlayMods(SkillSet.Empty, skills, 0, C, inning: 1).StuffBonus, 9);
        Assert.True(SkillModel.PlayMods(SkillSet.Empty, skills, 9, C, inning: 3).IsIdentity);

        var b = BatterAttributes.LeagueAverage;
        var early = Run(b, SkillModel.ApplyPitcher(p, skills, 0, C, inning: 1),
            SkillModel.PlayMods(SkillSet.Empty, skills, 0, C, inning: 1));
        var later = Run(b, SkillModel.ApplyPitcher(p, skills, 9, C, inning: 3),
            SkillModel.PlayMods(SkillSet.Empty, skills, 9, C, inning: 3));
        Assert.True(early.Obp > later.Obp, $"被出塁率 1回{early.Obp:F3} > 3回{later.Obp:F3}");
    }

    [Fact]
    public void Fireman_BoostsOnlyRightAfterEnteringInRelief()
    {
        var p = new PitcherAttributes { Control = 50 };
        var skills = new SkillSet(new[] { Skill.Fireman });
        Assert.Equal(56, SkillModel.ApplyPitcher(p, skills, 0, C, inning: 7, inRelief: true).Control);
        Assert.Equal(50, SkillModel.ApplyPitcher(p, skills, C.FiremanBatters, C, inning: 7, inRelief: true).Control);
        Assert.Equal(50, SkillModel.ApplyPitcher(p, skills, 0, C, inning: 1, inRelief: false).Control); // 先発には効かない

        var b = BatterAttributes.LeagueAverage;
        var entering = Run(b, SkillModel.ApplyPitcher(p, skills, 0, C, inning: 7, inRelief: true),
            SkillModel.PlayMods(SkillSet.Empty, skills, 0, C, inning: 7, inRelief: true));
        var plain = Run(b, p, SkillPlayMods.None);
        Assert.True(entering.Obp < plain.Obp, $"被出塁率 登板直後{entering.Obp:F3} < 素{plain.Obp:F3}");
    }

    [Fact]
    public void ReliefPick_PrefersFireman_OverASlightlyBetterArm()
    {
        var better = new Player { Name = "好投手", Position = FieldPosition.Pitcher,
            Pitching = new PitcherAttributes { Control = 55 } };
        var fireman = new Player { Name = "火消し", Position = FieldPosition.Pitcher,
            Pitching = new PitcherAttributes { Control = 50 }, Skills = new SkillSet(new[] { Skill.Fireman }) };
        var s = new PitchingChangeSituation(7, 9, 1, 1, 2, false, true, false, 90, 100, 10, true,
            new[] { better, fireman });
        Assert.Same(fireman, AiTacticsBrain.PickReliever(s, C));
        Assert.Same(better, AiTacticsBrain.PickReliever(s, C with { FiremanReliefScoreBonus = 0 }));
    }

    // ===== クイック◎× =====

    [Fact]
    public void QuickDelivery_LowersStealSuccess_SlowDeliveryRaisesIt()
    {
        var runner = new Player { Speed = 70, Steal = 60 };
        var catcher = new Player { Position = FieldPosition.Catcher };
        var br = new BaserunningCoefficients();
        double Rate(Skill? s)
        {
            var offset = SkillModel.QuickDeliveryOffsetSeconds(s is { } k ? new SkillSet(new[] { k }) : SkillSet.Empty, C);
            var safe = 0;
            for (var i = 0; i < 6000; i++)
            {
                if (StealResolver.ResolveVerdict(runner, catcher, br, new Xoshiro256Random(77UL + (ulong)i),
                        pitcherQuickOffsetSeconds: offset).Safe) safe++;
            }
            return safe / 6000.0;
        }
        var quick = Rate(Skill.QuickDelivery);
        var plain = Rate(null);
        var slow = Rate(Skill.SlowDelivery);
        Assert.True(quick < plain - 0.03 && plain < slow - 0.03, $"盗塁成功率 ◎{quick:P1} < なし{plain:P1} < ×{slow:P1}");

        // 采配AIの見込みも同じ秒で読む＝クイック◎の投手には走りにくい。
        var pitcher = new Player { Position = FieldPosition.Pitcher };
        Assert.True(StealResolver.ExpectedSuccessProbability(runner, catcher, pitcher, br,
                        pitcherQuickOffsetSeconds: -C.QuickDeliverySeconds)
                    < StealResolver.ExpectedSuccessProbability(runner, catcher, pitcher, br));
    }

    // ===== セーブ互換 =====

    [Fact]
    public void OldSave_WithRemovedAndRenamedSkills_Loads()
    {
        var state = new SaveState { Roster = SaveTestFixtures.BuildManagerRoster() };
        var json = SaveCodec.Serialize(state);
        Assert.Contains("\"OppositeFieldHitter\"", json);
        Assert.Contains("\"Grinder\"", json);
        // 旧版のセーブを再現: 流し打ち→広角打法の旧名、削除した守備系・存在しない名前を混ぜる。
        var old = json.Replace("\"OppositeFieldHitter\"", "\"SprayHitter\"")
            .Replace("\"Grinder\"", "\"Grinder\",\"DoublePlayArtist\",\"MasterCatcher\",\"NoSuchSkill\",\"3\"")
            .Replace("\"Monster\"", "\"Monster\",\"MasterCatcher\"");

        Assert.True(SaveCodec.TryDeserialize(old, out var loaded, out var err), err);
        var skills = loaded!.Roster[4].Skills;
        Assert.Equal(new[] { Skill.Grinder, Skill.OppositeFieldHitter }.OrderBy(x => x), skills.Visible.OrderBy(x => x));
        Assert.Equal(new[] { Skill.Monster }, skills.Hidden.ToArray());
        // 既定値（目慣れ）に化けていない。
        Assert.False(skills.Has(Skill.SlowStarterBat));
    }

    [Theory]
    [InlineData("SprayHitter", true, Skill.OppositeFieldHitter)]
    [InlineData("OppositeFieldHitter", true, Skill.OppositeFieldHitter)]
    [InlineData("DoublePlayArtist", false, default(Skill))]
    [InlineData("MasterCatcher", false, default(Skill))]
    [InlineData("0", false, default(Skill))]
    [InlineData("", false, default(Skill))]
    public void TryParseSaved_RenamesOrSkips(string name, bool ok, Skill expected)
    {
        Assert.Equal(ok, SkillNames.TryParseSaved(name, out var s));
        if (ok) Assert.Equal(expected, s);
    }

    // ===== 生成 =====

    private static List<SkillSet> Generate(double w, int n = 4000)
    {
        var root = new Xoshiro256Random(907);
        return Enumerable.Range(0, n).Select(i => SkillGenerator.Generate(w, 50, root.Fork((ulong)i), C)).ToList();
    }

    private static readonly Skill[] NewPitching =
        { Skill.ClutchPitcher, Skill.ChokePitcher, Skill.QuickDelivery, Skill.SlowDelivery, Skill.Fireman, Skill.ShakyStart };

    private static readonly Skill[] NewBatting =
        { Skill.ClutchHitter, Skill.ChokeHitter, Skill.FastballHitter, Skill.BreakingBallHitter, Skill.OppositeFieldHitter, Skill.TeamBatting };

    [Fact]
    public void Generation_PitchingSkillsGoToPitchers_BattingSkillsToFielders()
    {
        var pitchers = Generate(1.0);
        var fielders = Generate(0.0);
        foreach (var s in NewPitching)
        {
            Assert.True(pitchers.Count(x => x.Has(s)) > 0, $"{s} が投手寄りに付かない");
            Assert.Equal(0, fielders.Count(x => x.Has(s)));
        }
        foreach (var s in NewBatting)
        {
            Assert.True(fielders.Count(x => x.Has(s)) > 0, $"{s} が野手寄りに付かない");
            Assert.Equal(0, pitchers.Count(x => x.Has(s)));
        }
        // 削除した守備系は誰にも付かない（列挙から消えているので型レベルでも付かない）。
        Assert.DoesNotContain("DoublePlayArtist", Enum.GetNames(typeof(Skill)));
        Assert.DoesNotContain("MasterCatcher", Enum.GetNames(typeof(Skill)));
        Assert.DoesNotContain("SprayHitter", Enum.GetNames(typeof(Skill)));
    }

    [Fact]
    public void Generation_NeverGivesBothSidesOfAPair()
    {
        foreach (var set in Generate(0.5, 20000))
        {
            foreach (var s in Enum.GetValues<Skill>())
            {
                if (SkillNames.OppositeOf(s) is { } o && set.Has(s))
                    Assert.False(set.Has(o), $"{s} と {o} を同時に持っている");
            }
        }
    }
}
