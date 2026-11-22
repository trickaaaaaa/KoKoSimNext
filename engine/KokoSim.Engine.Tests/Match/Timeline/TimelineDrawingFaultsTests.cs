using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// タイムライン（絵）の不具合の続き（issue #1242）。バント・牽制・盗塁・ファウルフライ・自己フォースの絵が、
/// 判定・物理・人の脚力とずれないことを固定する。どれも結果は変えない（表示専用）。
/// </summary>
public sealed class TimelineDrawingFaultsTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();
    private const double Tol = 1e-3;

    private static Player Runner(string name, int speed = 50) => new() { Name = name, Speed = speed, Baserunning = 50 };

    private static double FielderCap()
        => new FielderAttributes().SprintSpeedMps / (1.0 - PlaybackEvaluator.MoveRampFraction);

    /// <summary>絵が要求する最大の脚（野手＝台形のピーク・走者＝区間の平均）を返す。LegSpeedTests と同じ測り方。</summary>
    private static (double Fielder, string FielderWhere, double Runner, string RunnerWhere) Peaks(PlayTimeline tl)
    {
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var fv = 0.0; var fw = "";
        foreach (var role in PlaybackEvaluator.DefaultPositions.Keys)
        {
            if (!play.Moves.TryGetValue(role, out var segs)) continue;
            foreach (var m in segs)
            {
                if (m.T1 - m.T0 <= 1e-6) continue;
                const double dt = 1.0 / 120.0;
                for (var t = m.T0; t <= m.T1; t += dt)
                {
                    var p0 = PlaybackEvaluator.FielderAt(play, role, t).Pos;
                    var p1 = PlaybackEvaluator.FielderAt(play, role, t + dt).Pos;
                    var v = Dist(p0, p1) / dt;
                    if (v > fv) { fv = v; fw = string.Format(CultureInfo.InvariantCulture, "{0} {1:F2}→{2:F2}", role, m.T0, m.T1); }
                }
            }
        }
        var rv = 0.0; var rw = "";
        foreach (var runner in play.Runners)
            foreach (var s in runner.Segs)
            {
                var dur = s.T1 - s.T0;
                var d = Dist(s.From, s.To);
                if (dur <= 1e-6 || d < 0.01) continue;
                var v = d / dur;
                if (v > rv) { rv = v; rw = string.Format(CultureInfo.InvariantCulture, "{0} {1:F2}m/{2:F2}s", runner.Label, d, dur); }
            }
        return (fv, fw, rv, rw);
    }

    private static double Dist(PlaybackVec a, PlaybackVec b)
        => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

    private static double RunnerCap(Player p)
        => p.ToFielder(C.AbilityEffect).SprintSpeedMps
           * Math.Max(C.RunnerTopSpeedFactor, new FieldingCoefficients().RunningTopSpeedFactor);

    private static void AssertHumanLegs(PlayTimeline tl, string what, params Player[] runners)
        => AssertHumanLegs(tl, what, checkRunners: true, runners);

    private static void AssertHumanLegs(PlayTimeline tl, string what, bool checkRunners, params Player[] runners)
    {
        var p = Peaks(tl);
        Assert.True(p.Fielder <= FielderCap() * 1.03 + Tol, $"{what}: 野手が {p.Fielder:F2} m/s（上限 {FielderCap():F2}）: {p.FielderWhere}");
        // 塁間の進塁レッグは走者の時計（RunnerClock）どおりの秒で、上限（スプリント×最高速係数）に対し0.5%ほど余る
        // 既存の丸めがある。ここで見たいのは秒速15mのような桁違いの速さ＝2%の余裕を持たせる。
        var cap = !checkRunners || runners.Length == 0 ? double.MaxValue : runners.Max(RunnerCap) * 1.03;
        Assert.True(p.Runner <= cap + Tol, $"{what}: 走者が {p.Runner:F2} m/s（上限 {cap:F2}）: {p.RunnerWhere}");
    }

    private static void AssertNoOverlappingMoves(PlayTimeline tl, string what)
    {
        foreach (var g in tl.Moves.GroupBy(m => m.Role))
        {
            var segs = g.OrderBy(m => m.T0).ToList();
            for (var k = 1; k < segs.Count; k++)
                Assert.True(segs[k].T0 >= segs[k - 1].T1 - 1e-9,
                    $"{what}: {g.Key} が同時刻に2つの行き先を持つ（{segs[k - 1].Task} {segs[k - 1].T0:F2}→{segs[k - 1].T1:F2} と {segs[k].Task} {segs[k].T0:F2}→{segs[k].T1:F2}）");
        }
    }

    // ───────── ① バントの処理者と塁カバーが同一人物 ─────────

    private static BuntResolver.BuntFielding Fielding(FieldPosition role, double x, double z, double fieldedAt = 2.2)
        => new(role, new HomePlaySituation(new Vector3D(x, 0, z), fieldedAt, C.BuntFielderThrowSpeedMps),
            LaunchSpeedMps: 4.0, LaunchBearingDeg: 0.0);

    public static IEnumerable<object[]> BuntCases()
    {
        var fielders = new (FieldPosition Role, double X, double Z)[]
        {
            (FieldPosition.Pitcher, -2.0, 9.0),
            (FieldPosition.Catcher, 0.5, 2.0),
            (FieldPosition.FirstBase, 9.0, 12.0),
            (FieldPosition.SecondBase, 6.0, 14.0),
            (FieldPosition.ThirdBase, -9.0, 12.0),
            (FieldPosition.Shortstop, -6.0, 14.0),
        };
        var results = new[]
        {
            BuntResult.SacrificeSuccess, BuntResult.InfieldHit,
            BuntResult.ForceOutAtSecond, BuntResult.ForceOutAtThird, BuntResult.ForceOutAtHome,
        };
        foreach (var r in results)
            foreach (var f in fielders)
                yield return new object[] { r, f.Role, f.X, f.Z };
    }

    // 二遊間は標準位置が深い（本塁から約40m）ので、バントを処理するのに脚力どおりの時間がかかる
    private static double FieldedAtOf(FieldPosition role)
        => role is FieldPosition.SecondBase or FieldPosition.Shortstop ? 4.0 : 2.2;

    private static PlayTimeline BuntTimeline(BuntResult result, FieldPosition role, double x, double z, double? fieldedAt = null)
        => TimelineBuilder.BuildBunt(
            new Player { Name = "打者", Speed = 60, Bunt = 50 }, Field.StandardAlignment(), result,
            Runner("R1"), Runner("R2"), Runner("R3"), null, null, null, C, Field,
            fielding: Fielding(role, x, z, fieldedAt ?? FieldedAtOf(role)));

    [Theory]
    [MemberData(nameof(BuntCases))]
    public void Bunt_NeverThrowsToTheFielderWhoFielded(BuntResult result, FieldPosition role, double x, double z)
    {
        var tl = BuntTimeline(result, role, x, z);
        var what = $"{result}/{role}";

        foreach (var seg in tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw))
        {
            Assert.True(seg.FromRole != seg.ToRole, $"{what}: {seg.FromRole} が自分へ送球している");
            Assert.True(Dist(seg.From, seg.To) > 0.5, $"{what}: 始点と終点が同じ送球（From=To）");
        }
        AssertNoOverlappingMoves(tl, what);
    }

    [Theory]
    [MemberData(nameof(BuntCases))]
    public void Bunt_LegsStayWithinHumanSpeed(BuntResult result, FieldPosition role, double x, double z)
    {
        var tl = BuntTimeline(result, role, x, z);
        // 打者・進塁走者の脚は既存の走者の時計（判定の秒）どおり＝ここで見るのは今回触った野手の脚（カバー・運び）
        AssertHumanLegs(tl, $"{result}/{role}", checkRunners: false);
    }

    // ───────── ② バント封殺の判定時刻 ─────────

    [Theory]
    [InlineData(BuntResult.ForceOutAtSecond, "走1")]
    [InlineData(BuntResult.ForceOutAtThird, "走2")]
    [InlineData(BuntResult.ForceOutAtHome, "走3")]
    public void BuntForceOut_IsCalledWhenTheThrowArrives_NotWhenTheBatterReachesFirst(BuntResult result, string label)
    {
        var tl = BuntTimeline(result, FieldPosition.Pitcher, -2.0, 9.0, fieldedAt: 1.0);

        var throwSeg = tl.Ball.Single(b => b.Kind is BallSegmentKind.Throw or BallSegmentKind.Carry && b.ToBase != "first");
        var batterArrive = tl.Runners.Single(r => r.Label == "打").T1;
        var call = tl.Judgements.Single(j => j.Label == label && j.Kind == JudgementKind.Out);
        Assert.Equal(throwSeg.T1, call.T, 6);
        Assert.True(call.T < batterArrive - 0.3,
            $"{result}: 封殺の判定（{call.T:F2}s）が打者の一塁到達（{batterArrive:F2}s）まで遅れている");
        Assert.True(tl.ResolvedAt >= throwSeg.T1 - 1e-9);
        var caption = tl.Captions.Last(cap => cap.Text.StartsWith("封殺", StringComparison.Ordinal));
        Assert.Equal(throwSeg.T1, caption.T, 6);
        // 封殺された走者は送球より後に塁へ着く（先に着く絵ならセーフに見える）
        var runner = tl.Runners.Single(r => r.Label == label);
        Assert.True(runner.T1 >= throwSeg.T1, $"{result}: 封殺された走者が送球より先に塁へ着いている");
        Assert.True(tl.Duration >= batterArrive, "尺が打者の一塁到達を覆っていない");
    }

    // ───────── ③ バントの小フライ併殺 ─────────

    [Fact]
    public void BuntPopDoublePlay_FirstRunnerLeavesThenReturns()
    {
        var r1 = Runner("R1");
        var tl = TimelineBuilder.BuildBunt(
            new Player { Name = "打者", Speed = 60, Bunt = 50 }, Field.StandardAlignment(), BuntResult.PopOutDoubledOff,
            r1, null, null, null, null, null, C, Field);

        var legs = tl.Runners.Where(r => r.Label == "走1").OrderBy(r => r.T0).ToList();
        var firstPt = new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z);
        Assert.True(legs.Count >= 2, "一塁走者のレッグが捕球前（出る）と捕球後（戻る）に分かれていない");
        foreach (var leg in legs)
            Assert.True(Dist(leg.From, leg.To) > 0.5, $"レッグが From=To（{leg.T0:F2}→{leg.T1:F2}）");
        Assert.True(Dist(legs[0].From, firstPt) < 0.5, "最初のレッグが一塁から出ていない");
        Assert.True(Dist(legs[^1].To, firstPt) < 0.5, "最後のレッグが一塁へ戻っていない");
        Assert.True(legs[^1].OutAtEnd);
        for (var k = 1; k < legs.Count; k++)
            Assert.True(Dist(legs[k - 1].To, legs[k].From) < 1e-6 && legs[k].T0 >= legs[k - 1].T1 - 1e-9, "レッグが繋がっていない");
        AssertHumanLegs(tl, "小フライ併殺", r1);
    }

    // ───────── ④ 牽制・盗塁・暴投の脚力 ─────────

    [Theory]
    [InlineData("first", true, 50)]
    [InlineData("first", false, 90)]
    [InlineData("second", true, 50)]
    [InlineData("second", false, 20)]
    [InlineData("second", false, 90)]
    [InlineData("third", true, 80)]
    [InlineData("third", false, 90)]
    public void Pickoff_LegsStayWithinHumanSpeed(string baseName, bool pickedOff, int runnerSpeed)
    {
        var runner = new Player { Speed = runnerSpeed, Steal = 60 };
        var pitcher = new Player { Position = FieldPosition.Pitcher, ArmStrength = 80, Mental = 50 };

        var tl = TimelineBuilder.BuildPickoff(runner, pitcher, baseName, pickedOff, C, Field);

        AssertHumanLegs(tl, $"牽制 {baseName} pickedOff={pickedOff}", runner);
        var throwSeg = tl.Ball.Single(b => b.Kind == BallSegmentKind.Throw);
        var leg = tl.Runners.Single();
        Assert.True(pickedOff ? leg.T1 >= throwSeg.T1 : leg.T1 <= throwSeg.T1, "判定と到達の前後が合わない");
        // 受け手は球より先に塁へ入る
        var cover = tl.Moves.Single(m => m.Task == FielderTask.CoverBase);
        Assert.True(cover.T1 <= throwSeg.T1 + 1e-9, "受け手が球より後に塁へ着いている");
    }

    [Theory]
    [InlineData(StealTarget.Second, StealResult.Safe)]
    [InlineData(StealTarget.Second, StealResult.CaughtStealing)]
    [InlineData(StealTarget.Third, StealResult.Safe)]
    [InlineData(StealTarget.Third, StealResult.CaughtStealing)]
    public void Steal_LegsStayWithinHumanSpeed(StealTarget target, StealResult result)
    {
        var runner = new Player { Speed = 70, Steal = 70, Baserunning = 70 };
        var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 70 };

        var tl = TimelineBuilder.BuildSteal(runner, catcher, result, C, Field, target);

        AssertHumanLegs(tl, $"盗塁 {target} {result}", runner);
    }

    [Fact]
    public void BatteryMiss_LegsStayWithinHumanSpeed()
    {
        var r1 = Runner("R1"); var r2 = Runner("R2"); var r3 = Runner("R3");
        var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 60 };

        var tl = TimelineBuilder.BuildBatteryMiss(r1, r2, r3, catcher, C, Field);

        AssertHumanLegs(tl, "暴投・捕逸", r1, r2, r3);
    }

    // ───────── ⑤ ファウルフライを先着して捕らない ─────────

    [Fact]
    public void ChasedFoulFly_FielderNeverArrivesBeforeTheBallLands()
    {
        var aero = new Aerodynamics();
        var ball = new BattedBall { ExitVelocityMps = 18.0, LaunchAngleDeg = 75.0, BearingDeg = 70.0 };
        var ground = BallisticIntegrator.IntegrateToGround(ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), aero);
        // 素の到達は落下より0.4秒先（判定の捕球予算は落下より短いので落球）
        var chase = new FoulChase(FieldPosition.FirstBase, 19.0, 24.0,
            ReachSeconds: ground.HangTimeSeconds - 0.4, HangSeconds: ground.HangTimeSeconds);

        var t = TimelineBuilder.BuildFoulFlight(ball, aero, new FieldingCoefficients(), Field, false, false, false, chase);

        Assert.Equal(FoulViewKind.ChasedFly, t.FoulView);
        var landAt = t.Ball.Single(b => b.Kind == BallSegmentKind.Flight).T1;
        var move = Assert.Single(t.Moves);
        Assert.True(move.T1 > landAt, $"野手（{move.T1:F2}s）が落下（{landAt:F2}s）より先に着いて捕らない");
        Assert.True(t.CatchMarginSeconds < 0, $"余裕が負でない: {t.CatchMarginSeconds}");
        Assert.Equal(landAt - move.T1, t.CatchMarginSeconds!.Value, 6);
    }

    // ───────── ⑥ 本封→一封で打者レッグを縮める ─────────

    [Fact]
    public void HomeForceRelay_DoesNotShortenTheBatterLeg()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.SecondBase);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        tl = TimelineBuilder.AppendBackHomeThrows(tl, play, Field, homeArriveAnchor: 3.5, runnerOut: true);
        // 鈍足の打者（一塁到達が遅い）＝転送が先に着いて打者アウトになる
        const double slowArrive = 7.0;
        tl = tl with { Runners = tl.Runners.Select(r => r.Label == "打" && r.OutAtEnd ? r with { T1 = slowArrive } : r).ToList() };
        var batterLeg = tl.Runners.First(r => r.Label == "打");

        var after = TimelineBuilder.AppendHomeForceRelay(tl, Field, slowArrive, batterOut: true);

        var relay = after.Ball.Single(b => b.Kind == BallSegmentKind.Throw && b.ToBase == "first");
        Assert.True(relay.T1 < batterLeg.T1, "前提: 転送が打者より先に着く（打者アウト）");
        var batterAfter = after.Runners.First(r => r.Label == "打");
        Assert.Equal(batterLeg.T0, batterAfter.T0, 9);
        Assert.Equal(batterLeg.T1, batterAfter.T1, 9);   // 物理より速く一塁に着いて止まらない（走り切る絵のまま）
        var call = after.Judgements.Single(j => j.Label == "打" && j.Kind == JudgementKind.Out);
        Assert.Equal(relay.T1, call.T, 6);
    }

    // ───────── ⑦ 自己フォースで球だけが滑る ─────────

    [Fact]
    public void SelfForceCarry_CarrierBodyTravelsWithTheBall()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.ThirdBase, fieldedX: -17.0, fieldedZ: 17.0);
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        var batterArrive = baseTl.Runners.First(r => r.Label == "打").T1;

        var tl = TimelineBuilder.AppendSelfForceDoublePlayThrows(baseTl, play, Field, batterArrive, targetBase: 3);

        AssertCarrierMoves(tl, FieldPosition.ThirdBase);
        AssertNoOverlappingMoves(tl, "三塁自己フォース");
    }

    [Fact]
    public void FirstBaseSelfForceTag_CarrierBodyTravelsWithTheBall()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.FirstBase, fieldedX: 21.0, fieldedZ: 16.0);
        var baseTl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        var batterArrive = baseTl.Runners.First(r => r.Label == "打").T1;

        var tl = TimelineBuilder.AppendFirstBaseSelfForceTagThrows(baseTl, play, Field, batterArrive);

        AssertCarrierMoves(tl, FieldPosition.FirstBase);
        AssertNoOverlappingMoves(tl, "一塁自己フォース");
    }

    private static void AssertCarrierMoves(PlayTimeline tl, FieldPosition carrier)
    {
        var carry = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Carry);
        var leg = Assert.Single(tl.Moves, m => m.Role == carrier && m.T0 >= carry.T0 - 1e-6 && m.T1 <= carry.T1 + 1e-6);
        Assert.Equal(carry.T0, leg.T0, 6);
        Assert.Equal(carry.T1, leg.T1, 6);
        Assert.True(Dist(leg.To, carry.To) <= 4.2 + 1e-6, "運び手が球の行き先（塁）から守備範囲より離れた所で止まっている");
        var p = Peaks(tl);
        Assert.True(p.Fielder <= FielderCap() + Tol, $"運び手が {p.Fielder:F2} m/s（上限 {FielderCap():F2}）: {p.FielderWhere}");
    }

    // ───────── ⑩ 中継送球の逸れの行き先 ─────────

    [Fact]
    public void RelayScatter_OfFirstBaseSelfForceTag_GoesToSecondBase()
    {
        var s = new ThrowScatterModel.Scatter(10.0, 5.0);
        var move = new RunnerMove(new Player(), 1, 2, Out: true) { FirstBaseSelfForceTag = true, RelayThrowScatter = s };
        var list = ThrowScatterAssigner.FromBattedBall(null, new[] { move });
        var judged = Assert.Single(list);
        Assert.Equal(2, judged.Base);
        Assert.Equal(s, judged.Scatter);

        // それ以外の転送（併殺のピボット・本封→一封など）は一塁行き
        var other = new RunnerMove(new Player(), 1, 2, Out: true) { RelayThrowScatter = s };
        Assert.Equal(1, Assert.Single(ThrowScatterAssigner.FromBattedBall(null, new[] { other })).Base);
    }
}
