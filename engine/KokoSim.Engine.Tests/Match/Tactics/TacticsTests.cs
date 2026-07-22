using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 采配システム（設計書09）: サイン・守備指示・配球方針・伝令・主将・DH・タイブレーク。
/// プレイヤーとAIが同じ ITacticsBrain を使う構造と、無指示時の従来挙動維持
/// （既存の固定値回帰テストが担保）を前提に、各判断と効果を検証する。
/// </summary>
public sealed class TacticsTests
{
    private static readonly TacticsCoefficients C = new();

    private static Player P(int contact = 50, int power = 50, int bunt = 50, int speed = 50,
        int steal = 50, int arm = 50, int mental = 50, FieldPosition pos = FieldPosition.CenterField)
        => new()
        {
            Position = pos, Contact = contact, Power = power, Bunt = bunt,
            Speed = speed, Steal = steal, ArmStrength = arm, Mental = mental,
        };

    private static Player Pitcher(int control = 50, string name = "P")
        => new() { Name = name, Position = FieldPosition.Pitcher, Pitching = new PitcherAttributes { Control = control } };

    private static TacticsSituation Situation(
        int inning = 5, int outs = 0, int diff = 0,
        Player? first = null, Player? second = null, Player? third = null,
        Player? batter = null, Player? pitcher = null, Player? catcher = null,
        int pressure = 0, bool rattled = false, int offTo = 3, int defTo = 3, bool tieBreak = false)
        => new(inning, 9, outs, diff, first, second, third,
            batter ?? P(), pitcher ?? Pitcher(), catcher ?? P(pos: FieldPosition.Catcher),
            pressure, rattled, offTo, defTo, tieBreak);

    // ===== StandardTacticsBrain: 攻撃サイン =====

    [Fact]
    public void Brain_SacBunt_LateCloseWeakBatter_SometimesCalled()
    {
        var brain = new StandardTacticsBrain(C);
        var s = Situation(inning: 8, outs: 0, first: P(), batter: P(power: 40, bunt: 60));
        var signs = Roll(brain, s, 200);
        Assert.Contains(OffensiveSign.SacrificeBunt, signs);
    }

    [Fact]
    public void Brain_SacBunt_NeverForSluggerOrWithOuts()
    {
        var brain = new StandardTacticsBrain(C);
        var slugger = Situation(inning: 8, outs: 0, first: P(), batter: P(power: 90, bunt: 60));
        Assert.DoesNotContain(OffensiveSign.SacrificeBunt, Roll(brain, slugger, 200));
        var withOuts = Situation(inning: 8, outs: 1, first: P(), batter: P(power: 40, bunt: 60));
        Assert.DoesNotContain(OffensiveSign.SacrificeBunt, Roll(brain, withOuts, 200));
    }

    [Fact]
    public void Brain_TieBreak_BuntsMuchMoreOften()
    {
        var brain = new StandardTacticsBrain(C);
        var normal = Situation(inning: 8, outs: 0, first: P(), batter: P(power: 40, bunt: 60));
        var tb = Situation(inning: 10, outs: 0, first: P(), batter: P(power: 40, bunt: 60), tieBreak: true);
        var normalBunts = Roll(brain, normal, 300).Count(x => x == OffensiveSign.SacrificeBunt);
        var tbBunts = Roll(brain, tb, 300).Count(x => x == OffensiveSign.SacrificeBunt);
        Assert.True(tbBunts > normalBunts, $"タイブレークでバント増のはず: {tbBunts} <= {normalBunts}");
    }

    [Fact]
    public void Brain_Squeeze_OnlyLateCloseWithThirdRunner()
    {
        var brain = new StandardTacticsBrain(C);
        var s = Situation(inning: 9, outs: 1, third: P(), batter: P(bunt: 70));
        Assert.Contains(OffensiveSign.Squeeze, Roll(brain, s, 300));
        var early = Situation(inning: 3, outs: 1, third: P(), batter: P(bunt: 70));
        Assert.DoesNotContain(OffensiveSign.Squeeze, Roll(brain, early, 300));
    }

    /// <summary>
    /// 盗塁の「試みるか」判定は設計書15 Phase D-2d で CallOffense（打席頭一度）から
    /// IPitchTacticsBrain.CallPitchAction（毎球）へ移った。ここでは1球分の判定を200回振って確認する。
    /// </summary>
    [Fact]
    public void Brain_Steal_OnlyWhenSuccessLikely()
    {
        var brain = new StandardTacticsBrain(C);
        var fast = Situation(first: P(speed: 90, steal: 90), catcher: P(arm: 30, pos: FieldPosition.Catcher));
        Assert.True(StealAttemptCount(brain, fast, 200) > 0, "成功見込みが高い盗塁が一度も試みられない");
        var slow = Situation(first: P(speed: 30, steal: 30), catcher: P(arm: 80, pos: FieldPosition.Catcher));
        Assert.Equal(0, StealAttemptCount(brain, slow, 200));
    }

    // ===== StandardTacticsBrain: 盗塁の始動種別（設計書12 §5, G3b／設計書15 Phase D-2d で毎球判定へ） =====

    [Fact]
    public void Brain_StartType_MarginalSteal_SometimesGambles()
    {
        // 成功見込みが際どい盗塁（通常始動では心もとない）＝好ジャンプ・意表のギャンブルに賭ける。
        // 試みが発生するために StealMinSuccess(0.72) 以上、ギャンブル対象になるために
        // GambleStartMaxSuccess(0.82) 未満の帯を狙う（設計書15 Phase D-2d で両判定が1メソッドに統合された）。
        var brain = new StandardTacticsBrain(C);
        var runner = P(speed: 80, steal: 80);
        var catcher = P(arm: 60, pos: FieldPosition.Catcher);
        var est = StealResolver.SuccessProbability(runner, catcher, new BaserunningCoefficients());
        Assert.True(est >= C.StealMinSuccess && est < C.GambleStartMaxSuccess, $"前提: 際どい見込みのはず est={est}");
        var s = Situation(first: runner, catcher: catcher);
        Assert.True(GambleCount(brain, s, 300) > 0, "際どい盗塁でギャンブル始動が一度も出ない");
    }

    [Fact]
    public void Brain_StartType_ComfortableSteal_NeverGambles()
    {
        // 見込みが十分高い盗塁は通常始動で堅実に決める（無防備リスクを負わない）。
        var brain = new StandardTacticsBrain(C);
        var runner = P(speed: 95, steal: 95);
        var catcher = P(arm: 20, pos: FieldPosition.Catcher);
        var est = StealResolver.SuccessProbability(runner, catcher, new BaserunningCoefficients());
        Assert.True(est >= C.GambleStartMaxSuccess, $"前提: 余裕の見込みのはず est={est}");
        var s = Situation(first: runner, catcher: catcher);
        Assert.Equal(0, GambleCount(brain, s, 300));
    }

    [Fact]
    public void Brain_StartType_NoRunner_NeverAttemptsOrGambles()
    {
        // 走者なし＝試みる対象が無いので、始動種別を問う以前にそもそも試みが一度も発生しない。
        var empty = Situation();
        var brain = new StandardTacticsBrain(C);
        Assert.Equal(0, StealAttemptCount(brain, empty, 200));
        Assert.Equal(0, GambleCount(brain, empty, 200));
    }

    // ===== StandardTacticsBrain: 三盗・本盗（issue #67, design-14 未決A） =====

    [Fact]
    public void Brain_StealThird_OnlyWhenSecondOnlyOccupied()
    {
        var brain = new StandardTacticsBrain(C);
        var runner = P(speed: 95, steal: 95);
        var weakCatcher = P(arm: 20, pos: FieldPosition.Catcher);
        var second = Situation(outs: 0, second: runner, catcher: weakCatcher);
        Assert.True(StealTargetCount(brain, second, StealTarget.Third, 400) > 0, "三盗候補で一度も企図されない");

        // 一塁が埋まっている（=二盗の対象で三盗候補ではない）と三盗は出ない。
        var withFirst = Situation(outs: 0, first: P(), second: runner, catcher: weakCatcher);
        Assert.Equal(0, StealTargetCount(brain, withFirst, StealTarget.Third, 400));

        // 三塁も埋まっている（=一・三塁の重盗の塁状況）と三盗は出ない。
        var withThird = Situation(outs: 0, second: runner, third: P(), catcher: weakCatcher);
        Assert.Equal(0, StealTargetCount(brain, withThird, StealTarget.Third, 400));
    }

    [Fact]
    public void Brain_StealThird_RespectsOutsAndScoreDiffLimits()
    {
        var brain = new StandardTacticsBrain(C);
        var runner = P(speed: 95, steal: 95);
        var weakCatcher = P(arm: 20, pos: FieldPosition.Catcher);
        var tooManyOuts = Situation(outs: C.StealThirdMaxOuts + 1, second: runner, catcher: weakCatcher);
        Assert.Equal(0, StealTargetCount(brain, tooManyOuts, StealTarget.Third, 400));

        var blowout = Situation(outs: 0, diff: C.StealThirdMaxDiffAbs + 5, second: runner, catcher: weakCatcher);
        Assert.Equal(0, StealTargetCount(brain, blowout, StealTarget.Third, 400));
    }

    [Fact]
    public void Brain_StealHome_OnlyWhenThirdOnlyOccupied_AndAlwaysGambles()
    {
        var brain = new StandardTacticsBrain(C);
        var runner = P(speed: 95, steal: 95);
        var weakCatcher = P(arm: 20, pos: FieldPosition.Catcher);
        var third = Situation(outs: 0, third: runner, catcher: weakCatcher);
        var count = StealTargetCount(brain, third, StealTarget.Home, 600);
        Assert.True(count > 0, "本盗候補で一度も企図されない");
        Assert.Equal(count, GambleTargetCount(brain, third, StealTarget.Home, 600));

        // 一塁・二塁が埋まっていれば本盗の対象塁状況ではない。
        var withFirst = Situation(outs: 0, first: P(), third: runner, catcher: weakCatcher);
        Assert.Equal(0, StealTargetCount(brain, withFirst, StealTarget.Home, 400));
    }

    [Fact]
    public void Brain_StealHome_RespectsOutsAndScoreDiffLimits()
    {
        var brain = new StandardTacticsBrain(C);
        var runner = P(speed: 95, steal: 95);
        var weakCatcher = P(arm: 20, pos: FieldPosition.Catcher);
        var tooManyOuts = Situation(outs: C.StealHomeMaxOuts + 1, third: runner, catcher: weakCatcher);
        Assert.Equal(0, StealTargetCount(brain, tooManyOuts, StealTarget.Home, 400));

        var blowout = Situation(outs: 0, diff: C.StealHomeMaxDiffAbs + 5, third: runner, catcher: weakCatcher);
        Assert.Equal(0, StealTargetCount(brain, blowout, StealTarget.Home, 400));
    }

    private static int StealTargetCount(StandardTacticsBrain brain, TacticsSituation s, StealTarget target, int n)
    {
        var c = 0;
        for (ulong i = 0; i < (ulong)n; i++)
        {
            var d = brain.CallPitchAction(new PitchTacticsSituation(s, 0, 0, 0, null), new Xoshiro256Random(i));
            if (d?.StealAttempt is not null && d.Value.StealTarget == target) c++;
        }
        return c;
    }

    private static int GambleTargetCount(StandardTacticsBrain brain, TacticsSituation s, StealTarget target, int n)
    {
        var c = 0;
        for (ulong i = 0; i < (ulong)n; i++)
        {
            var d = brain.CallPitchAction(new PitchTacticsSituation(s, 0, 0, 0, null), new Xoshiro256Random(i));
            if (d?.StealAttempt == StartType.Gamble && d.Value.StealTarget == target) c++;
        }
        return c;
    }

    private static int StealAttemptCount(StandardTacticsBrain brain, TacticsSituation s, int n)
    {
        var c = 0;
        for (ulong i = 0; i < (ulong)n; i++)
        {
            var d = brain.CallPitchAction(new PitchTacticsSituation(s, 0, 0, 0, null), new Xoshiro256Random(i));
            if (d?.StealAttempt is not null) c++;
        }
        return c;
    }

    private static int GambleCount(StandardTacticsBrain brain, TacticsSituation s, int n)
    {
        var g = 0;
        for (ulong i = 0; i < (ulong)n; i++)
        {
            var d = brain.CallPitchAction(new PitchTacticsSituation(s, 0, 0, 0, null), new Xoshiro256Random(i));
            if (d?.StealAttempt == StartType.Gamble) g++;
        }
        return g;
    }

    private static List<OffensiveSign> Roll(StandardTacticsBrain brain, TacticsSituation s, int n)
    {
        var list = new List<OffensiveSign>(n);
        for (ulong i = 0; i < (ulong)n; i++)
        {
            list.Add(brain.CallOffense(s, new Xoshiro256Random(i)));
        }
        return list;
    }

    // ===== StandardTacticsBrain: 守備指示 =====

    [Fact]
    public void Brain_Defense_InfieldIn_WithThirdRunnerLateClose()
    {
        var brain = new StandardTacticsBrain(C);
        var s = Situation(inning: 9, outs: 1, diff: 0, third: P());
        var d = brain.CallDefense(s, new Xoshiro256Random(1));
        Assert.Equal(DefenseDepth.In, d.Infield);
    }

    [Fact]
    public void Brain_Defense_OutfieldDeep_VsSlugger()
    {
        var brain = new StandardTacticsBrain(C);
        var d = brain.CallDefense(Situation(batter: P(power: 85)), new Xoshiro256Random(1));
        Assert.Equal(DefenseDepth.Deep, d.Outfield);
        Assert.Equal(PitchPolicy.KeepLow, d.Policy); // 強打者には低め徹底
    }

    [Fact]
    public void Brain_Defense_GearPushInCrunch_CoastWithBigLead()
    {
        var brain = new StandardTacticsBrain(C);
        var crunch = Situation(inning: 9, diff: 0, second: P());
        Assert.Equal(PitcherGear.Push, brain.CallDefense(crunch, new Xoshiro256Random(1)).Gear);
        var blowout = Situation(inning: 5, diff: -6); // 守備側6点リード
        Assert.Equal(PitcherGear.Coast, brain.CallDefense(blowout, new Xoshiro256Random(1)).Gear);
    }

    // ===== 敬遠（design-14 P1-3） =====

    [Fact]
    public void Brain_IntentionalWalk_Disabled_ByDefault()
    {
        var brain = new StandardTacticsBrain(C); // 既定係数（IntentionalWalkProb=0）
        var s = Situation(inning: 9, diff: 0, second: P(), batter: P(power: 95));
        Assert.False(brain.CallDefense(s, new Xoshiro256Random(1)).IntentionalWalk);
    }

    [Fact]
    public void Brain_IntentionalWalk_NeverWithRunnerOnFirst()
    {
        var forced = C with { IntentionalWalkProb = 1.0 };
        var brain = new StandardTacticsBrain(forced);
        var s = Situation(inning: 9, diff: 0, first: P(), second: P(), batter: P(power: 95));
        Assert.False(brain.CallDefense(s, new Xoshiro256Random(1)).IntentionalWalk);
    }

    [Fact]
    public void Brain_IntentionalWalk_FiresForSluggerWithFirstBaseOpen()
    {
        var forced = C with { IntentionalWalkProb = 1.0 };
        var brain = new StandardTacticsBrain(forced);
        var slugger = Situation(inning: 9, diff: 0, second: P(), batter: P(power: 95));
        Assert.True(brain.CallDefense(slugger, new Xoshiro256Random(1)).IntentionalWalk);

        var average = Situation(inning: 9, diff: 0, second: P(), batter: P(power: 50));
        Assert.False(brain.CallDefense(average, new Xoshiro256Random(1)).IntentionalWalk);
    }

    // ===== 陣形→初期守備位置（効果は幾何から出る） =====

    [Fact]
    public void Alignment_DefaultTactics_ReturnsSameInstance()
    {
        var fielders = new FieldGeometry().StandardAlignment();
        Assert.Same(fielders, AlignmentTactics.Adjust(fielders, DefensiveTactics.Default, C));
    }

    [Fact]
    public void Alignment_InfieldIn_MovesInfieldersCloser_OutfieldUntouched()
    {
        var field = new FieldGeometry();
        var normal = field.StandardAlignment();
        var adjusted = AlignmentTactics.Adjust(normal, new DefensiveTactics { Infield = DefenseDepth.In }, C);
        var ss0 = normal.First(f => f.Position == FieldPosition.Shortstop).Location;
        var ss1 = adjusted.First(f => f.Position == FieldPosition.Shortstop).Location;
        Assert.True(ss1.Z < ss0.Z);
        var cf0 = normal.First(f => f.Position == FieldPosition.CenterField).Location;
        var cf1 = adjusted.First(f => f.Position == FieldPosition.CenterField).Location;
        Assert.Equal(cf0.Z, cf1.Z, 9);
    }

    [Fact]
    public void Alignment_BuntShift_ChargesCorners()
    {
        var field = new FieldGeometry();
        var normal = field.StandardAlignment();
        var adjusted = AlignmentTactics.Adjust(normal, new DefensiveTactics { BuntShift = true }, C);
        foreach (var pos in new[] { FieldPosition.FirstBase, FieldPosition.ThirdBase })
        {
            var before = normal.First(f => f.Position == pos).Location;
            var after = adjusted.First(f => f.Position == pos).Location;
            Assert.True(after.Z < before.Z * 0.7, $"{pos} がチャージしていない");
        }
    }

    // ===== 配球方針→自動配球の重み =====

    [Fact]
    public void Directive_FastballHeavy_RaisesFastballShare()
    {
        var pitcher = new PitcherAttributes
        {
            Repertoire = new[] { PitchSlot.FastballOf(50), new PitchSlot { Type = PitchType.Curve, Power = 50, Sharpness = 50 } },
        };
        var auto = CountFastballs(pitcher, null);
        var heavy = CountFastballs(pitcher, C.DirectiveFor(PitchPolicy.FastballHeavy, Handedness.Right));
        Assert.True(heavy > auto + 200, $"直球中心で増えていない: {heavy} vs {auto}");
    }

    private static int CountFastballs(PitcherAttributes pitcher, PitchDirective? d)
    {
        var rng = new Xoshiro256Random(7);
        var zone = new StrikeZone();
        var n = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (PitchSelection.Select(pitcher, zone, new(), new(), rng, PitcherGear.Normal, d).Type == PitchType.Fastball) n++;
        }
        return n;
    }

    [Fact]
    public void Directive_KeepLow_LowersAim_InsideShiftsByHandedness()
    {
        var pitcher = PitcherAttributes.LeagueAverage;
        var zone = new StrikeZone();
        double MeanY(PitchDirective? d)
        {
            var rng = new Xoshiro256Random(11);
            var sum = 0.0;
            for (var i = 0; i < 1500; i++) sum += PitchSelection.Select(pitcher, zone, new(), new(), rng, PitcherGear.Normal, d).AimY;
            return sum / 1500;
        }
        Assert.True(MeanY(C.DirectiveFor(PitchPolicy.KeepLow, Handedness.Right)) < MeanY(null) - 0.10);

        double MeanX(PitchDirective? d)
        {
            var rng = new Xoshiro256Random(13);
            var sum = 0.0;
            for (var i = 0; i < 1500; i++) sum += PitchSelection.Select(pitcher, zone, new(), new(), rng, PitcherGear.Normal, d).AimX;
            return sum / 1500;
        }
        Assert.True(MeanX(C.DirectiveFor(PitchPolicy.InsideAttack, Handedness.Right)) < -0.05); // 右打者の内角=三塁側
        Assert.True(MeanX(C.DirectiveFor(PitchPolicy.InsideAttack, Handedness.Left)) > 0.05);
    }

    // ===== 「待て」サイン: 初球は必ず見送り =====

    [Fact]
    public void TakeSign_FirstPitchNeverSwung_SoAtLeastTwoPitches()
    {
        var batter = new BatterAttributes();
        var pitcher = PitcherAttributes.LeagueAverage;
        var take = new AtBatContext { TakeFirstPitch = true };
        var minPitches = int.MaxValue;
        for (ulong s = 0; s < 300; s++)
        {
            var r = AtBatResolver.ResolveDetailed(batter, pitcher, take, new Xoshiro256Random(s));
            minPitches = System.Math.Min(minPitches, r.Pitches);
        }
        Assert.True(minPitches >= 2, $"初球見送りなら最少2球のはず: {minPitches}");
    }

    // ===== 伝令・動揺・主将（TeamState） =====

    private static Team TeamOf(string name, Player? captain = null, IReadOnlyList<Player>? bullpen = null)
    {
        var order = new List<Player>
        {
            P(pos: FieldPosition.Catcher), P(pos: FieldPosition.FirstBase), P(pos: FieldPosition.SecondBase),
            P(pos: FieldPosition.ThirdBase), P(pos: FieldPosition.Shortstop), P(pos: FieldPosition.LeftField),
            captain ?? P(pos: FieldPosition.CenterField), P(pos: FieldPosition.RightField),
            Pitcher(name: name + "P"),
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = bullpen ?? System.Array.Empty<Player>(), Captain = captain,
        };
    }

    [Fact]
    public void Timeouts_ThreePerGame_ExtraInningGrantsMore()
    {
        var st = new TeamState(TeamOf("A"));
        Assert.True(st.TryUseDefenseTimeout());
        Assert.True(st.TryUseDefenseTimeout());
        Assert.True(st.TryUseDefenseTimeout());
        Assert.False(st.TryUseDefenseTimeout()); // 3回で尽きる
        Assert.True(st.TryUseOffenseTimeout());  // 攻守は別勘定
        st.GrantExtraInningTimeouts();
        Assert.True(st.TryUseDefenseTimeout());  // 延長で1回追加
    }

    [Fact]
    public void Rattled_ConsecutiveBaserunners_SetsAndClears()
    {
        var st = new TeamState(TeamOf("A"));
        st.NotePitchingResult(PlateAppearanceResult.Single, 3);
        st.NotePitchingResult(PlateAppearanceResult.Walk, 3);
        Assert.False(st.PitcherRattled);
        st.NotePitchingResult(PlateAppearanceResult.Single, 3);
        Assert.True(st.PitcherRattled);
        st.ClearRattled(); // 伝令で解除
        Assert.False(st.PitcherRattled);
        // アウトで連続カウントはリセットされる。
        st.NotePitchingResult(PlateAppearanceResult.Single, 3);
        st.NotePitchingResult(PlateAppearanceResult.InPlayOut, 3);
        st.NotePitchingResult(PlateAppearanceResult.Single, 3);
        st.NotePitchingResult(PlateAppearanceResult.Single, 3);
        Assert.False(st.PitcherRattled);
    }

    [Fact]
    public void Captain_OnFieldFullEffect_BenchReduced_NoneZero()
    {
        var captain = P(mental: 80);
        captain = captain with { Leadership = 80 }; // 統率力 = 80×80/100 = 64
        var onField = new TeamState(TeamOf("A", captain: captain));
        var expected = 64.0 * C.CaptainMitigationPerPower;
        Assert.Equal(expected, onField.CaptainMitigation(C), 9);

        // ベンチ（ブルペン在籍）は大きく減衰。
        var benchTeam = TeamOf("B", bullpen: new[] { captain with { Pitching = PitcherAttributes.LeagueAverage } });
        var benchCaptain = benchTeam.Bullpen[0];
        var bench = new TeamState(benchTeam with { Captain = benchCaptain });
        Assert.Equal(expected * C.CaptainBenchFactor, bench.CaptainMitigation(C), 9);

        Assert.Equal(0.0, new TeamState(TeamOf("C")).CaptainMitigation(C));
    }

    [Fact]
    public void Multiplier_RattledAmplifiesNegativeOnly()
    {
        var pc = new PressureCoefficients();
        var calm = PressureModel.Multiplier(20, 8, pc);
        var rattled = PressureModel.Multiplier(20, 8, pc, negativeAmplify: C.RattledNegativeAmplify);
        Assert.True(rattled < calm);
        Assert.Equal(PressureModel.Multiplier(100, 8, pc),
                     PressureModel.Multiplier(100, 8, pc, negativeAmplify: C.RattledNegativeAmplify), 9);
    }

    // ===== 試合統合: 采配Brainつきでも決定論、サインが試合を変える =====

    [Fact]
    public void Game_WithBrains_IsDeterministic()
    {
        Team Build(string n) => TeamOf(n) with { Tactics = new StandardTacticsBrain(C) };
        var ctx = new GameContext();
        for (ulong s = 0; s < 5; s++)
        {
            var a = GameEngine.Play(Build("A"), Build("H"), ctx, new Xoshiro256Random(s));
            var b = GameEngine.Play(Build("A"), Build("H"), ctx, new Xoshiro256Random(s));
            Assert.Equal(a.AwayRuns, b.AwayRuns);
            Assert.Equal(a.HomeRuns, b.HomeRuns);
            Assert.Equal(a.TotalPitches, b.TotalPitches);
            Assert.Equal(a.Log.Count, b.Log.Count);
        }
    }

    [Fact]
    public void Game_BrainsChangeOutcomes()
    {
        var ctx = new GameContext();
        var differs = false;
        for (ulong s = 0; s < 20 && !differs; s++)
        {
            var plain = GameEngine.Play(TeamOf("A"), TeamOf("H"), ctx, new Xoshiro256Random(s));
            var brainy = GameEngine.Play(
                TeamOf("A") with { Tactics = new StandardTacticsBrain(C) },
                TeamOf("H") with { Tactics = new StandardTacticsBrain(C) },
                ctx, new Xoshiro256Random(s));
            differs = plain.TotalPitches != brainy.TotalPitches || plain.TotalRuns != brainy.TotalRuns;
        }
        Assert.True(differs, "采配が一切試合に影響していない");
    }

    /// <summary>敬遠テスト専用: 最初の CallDefense 呼び出しのみ IntentionalWalk を強制する（design-14 P1-3）。
    /// 毎打席強制すると outs が一切進まず半イニングが終わらないため、1回きりに限定して安全に検証する。</summary>
    private sealed class ForcedIntentionalWalkBrain : ITacticsBrain
    {
        private readonly StandardTacticsBrain _inner = new(new TacticsCoefficients());
        private bool _used;

        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => _inner.CallOffense(s, rng);

        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng)
        {
            var d = _inner.CallDefense(s, rng);
            if (!_used) { _used = true; d = d with { IntentionalWalk = true }; }
            return d;
        }

        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
    }

    [Fact]
    public void IntentionalWalk_WhenCalled_ProducesAWalkForTheLeadoffBatter()
    {
        // 最初の CallDefense（先攻1番打者の初打席）だけを強制するので、その打席は必ず四球になる。
        // 通算成績には以降の通常打席も混ざるため、Walks>=1（打数には含めない）だけを厳密に検証する。
        var away = TeamOf("A");
        var home = TeamOf("H") with { Tactics = new ForcedIntentionalWalkBrain() };
        var r = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(5));
        var leadoff = r.AwayBatting[0];
        Assert.True(leadoff.Walks >= 1, "強制敬遠が四球として記録されていない");
    }

    // ===== DH（設計書09 §6） =====

    private static Team DhTeam(string name)
    {
        var order = new List<Player>
        {
            P(pos: FieldPosition.Catcher), P(pos: FieldPosition.FirstBase), P(pos: FieldPosition.SecondBase),
            P(pos: FieldPosition.ThirdBase), P(pos: FieldPosition.Shortstop), P(pos: FieldPosition.LeftField),
            P(pos: FieldPosition.CenterField), P(pos: FieldPosition.RightField),
            P(contact: 70, power: 80) with { Name = name + "DH" }, // 守備に就かない打撃専門
        };
        return new Team
        {
            Name = name, BattingOrder = order, DhSlot = 8,
            StartingPitcher = Pitcher(name: name + "SP"),
            Bullpen = new[] { Pitcher(name: name + "RP") },
        };
    }

    [Fact]
    public void Dh_PitcherNeverBats_DhBats_AlignmentStillNine()
    {
        var away = DhTeam("A");
        var r = GameEngine.Play(away, TeamOf("H"), new GameContext(), new Xoshiro256Random(3));
        // 打撃成績にDHが居て、投手は打席に立たない。
        Assert.Contains(r.AwayBatting, l => l.Name == "ADH" && l.PlateAppearances > 0);
        Assert.DoesNotContain(r.AwayBatting, l => l.Name is "ASP" or "ARP");
        // 投手成績は先発から記録される。
        Assert.Equal("ASP", r.AwayPitching[0].Name);
        // 守備は9人（DH除外＋投手追加）。
        Assert.Equal(9, new TeamState(away).DefensiveAlignment(new FieldGeometry()).Count);
    }

    [Fact]
    public void Dh_RequiresStartingPitcher()
        => Assert.Throws<System.ArgumentException>(() => new TeamState(DhTeam("A") with { StartingPitcher = null }));

    // ===== タイブレーク（設計書09 §7） =====

    [Fact]
    public void TieBreak_ExtraInnings_ScoreMuchMore()
    {
        // 無死一・二塁スタートの直接効果＝延長イニングの得点率が跳ね上がることを検証する。
        double ExtraRunRate(GameContext ctx)
        {
            var runs = 0;
            var halves = 0;
            for (ulong s = 0; s < 80; s++)
            {
                var r = GameEngine.Play(TeamOf("A"), TeamOf("H"), ctx, new Xoshiro256Random(s));
                foreach (var line in new[] { r.AwayLineScore, r.HomeLineScore })
                {
                    for (var i = 9; i < line.Count; i++)
                    {
                        runs += line[i];
                        halves++;
                    }
                }
            }
            Assert.True(halves > 0, "延長にもつれる試合が1つもない（テスト前提が崩れている）");
            return (double)runs / halves;
        }

        var plainRate = ExtraRunRate(new GameContext { MaxInnings = 12 });
        var tbRate = ExtraRunRate(new GameContext { MaxInnings = 12, TieBreakEnabled = true, TieBreakStartInning = 10 });
        Assert.True(tbRate > plainRate * 1.5, $"タイブレークで延長の得点率が上がるはず: {tbRate:F2} vs {plainRate:F2}");
    }

    // ===== YAML駆動（不変条件#4） =====

    [Fact]
    public void CoefficientsYaml_LoadsPressureAndTactics()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var b = KokoSim.Config.CoefficientsLoader.LoadFromFile(path);
        Assert.Equal(new PressureCoefficients().MultiplierSlope, b.Pressure.MultiplierSlope, 9);
        Assert.Equal(new TacticsCoefficients().SacBuntProb, b.Tactics.SacBuntProb, 9);
        Assert.Equal(new TacticsCoefficients().TimeoutDurationPa, b.Tactics.TimeoutDurationPa);
        Assert.Equal(new TacticsCoefficients().CaptainMitigationPerPower, b.Tactics.CaptainMitigationPerPower, 9);
    }

    [Fact]
    public void TieBreak_PreviousBatterMapping()
    {
        var team = TeamOf("A");
        var st = new TeamState(team);
        st.NextBatter(); // 1番消費 → 次は2番
        // 次打者=2番(index1)。前打者=1番、前々打者=9番(投手スロット=現投手)。
        Assert.Equal(team.BattingOrder[0], st.PreviousBatter(1));
        Assert.Equal(st.CurrentPitcher, st.PreviousBatter(2));
    }
}
