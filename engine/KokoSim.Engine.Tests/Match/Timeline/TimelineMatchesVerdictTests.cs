using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// タイムラインの絵が engine の判定・物理と食い違わない（issue #1214）。表示専用の契約テスト（判定・乱数順には触れない）。
/// </summary>
public sealed class TimelineMatchesVerdictTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();
    private static readonly FieldingCoefficients Fielding = new();

    // --- ① 走者ラベル「走N」の採番は連結後（AppendRunnerLegs）と同じ ---

    [Fact]
    public void ReturnTarget_LabelFollowsMergedChain_SoItNamesAnExistingLeg()
    {
        var play = FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: 62.0, fieldedAtSeconds: 3.2, result: BattedBallResult.Single);
        var batter = new Player { Name = "b", Speed = 55 };
        var r1 = new Player { Name = "r1", Speed = 55 };
        // 打者は失策の連鎖で 0→1→2 と2本の move になる。一塁走者は三塁まで。
        var moves = new List<RunnerMove>
        {
            new(batter, 0, 1, false), new(batter, 1, 2, false), new(r1, 1, 3, false),
        };
        var tl = TimelineBuilder.AppendRunnerLegs(
            TimelineBuilder.BuildBattedBall(play, Field.StandardAlignment(), Field, null, runnersOn: true,
                TimelineBuilder.DescribeResult(play)),
            moves, Field, Baserunning, Fielding);

        var target = TimelineBuilder.ResolveOutfieldReturnTarget(moves, play.Result);

        Assert.Equal("third", target.BaseName);
        Assert.Contains(tl.Runners, l => l.Label == target.AnchorLabel);
        var anchorEnd = tl.Runners.Where(l => l.Label == target.AnchorLabel).Max(l => l.T1);
        Assert.True(anchorEnd > 0.0);
        // アンカーは一塁走者の三塁到達（打者の連鎖ではない）
        var third = Field.ThirdBase;
        var last = tl.Runners.Where(l => l.Label == target.AnchorLabel).OrderBy(l => l.T1).Last();
        Assert.True(Math.Abs(last.To.X - third.X) < 0.5 && Math.Abs(last.To.Z - third.Z) < 0.5, "アンカーが三塁へ走る走者ではない");
    }

    // --- ② 2アウトのゴロで押し出し走者は、打者が一塁でアウトになる瞬間（送球の到達）で止まる ---

    [Fact]
    public void TwoOutGrounder_ForcedRunnerFreezesAtTheOutJudgement_NotAtBatterArrival()
    {
        var play = new FieldingPlay
        {
            Result = BattedBallResult.Out,
            LandingX = -12.0, LandingZ = 30.0, HangTimeSeconds = 0.6, ApexHeightM = 2.0,
            RangeM = 34.0, BearingDeg = -20.0, FielderRole = FieldPosition.Shortstop,
            FieldedAtSeconds = 1.5, BatterToFirstSeconds = 4.3, IsFly = false,
            FielderThrowSpeedMps = 33.0, ThrowArriveSeconds = 3.6,
        };
        var tl = TimelineBuilder.BuildBattedBall(play, Field.StandardAlignment(), Field, null, runnersOn: true,
            TimelineBuilder.DescribeResult(play));
        var outAt = tl.Judgements.Single(j => j.Label == "打" && j.Kind == JudgementKind.Out).T;
        var batterArrive = tl.Runners.First(l => l.Label == "打").T1;
        Assert.True(outAt < batterArrive - 0.3, $"前提: 封殺({outAt})は打者の到達({batterArrive})より十分早い");

        var held = TimelineBuilder.AppendHeldRunners(
            tl, true, false, false, new Player { Speed = 50 }, null, null,
            new List<RunnerMove>(), Field, outsBefore: 2, play, Baserunning);
        var runner = PlayTimelineAdapter.ToPlaybackPlay(held).Runners.Single(x => x.Label == "走一");

        var atOut = PlaybackEvaluator.RunnerAt(runner, outAt)!.Value;
        var later = PlaybackEvaluator.RunnerAt(runner, batterArrive)!.Value;
        Assert.Equal(atOut.X, later.X, 3);
        Assert.Equal(atOut.Y, later.Y, 3);
    }

    // --- ③ お手玉: 球は拾い直した点から投げ始め、後続の野手レッグは捨てない ---

    private static EndpointBallSegment Seg(BallSegmentKind kind, double t0, double t1, double fx, double fz, double tx, double tz,
        FieldPosition? from = null, FieldPosition? to = null)
        => EndpointBallSegment.From(new BallSegment
        {
            Kind = kind, T0 = t0, T1 = t1, From = new TimelinePoint(fx, fz), To = new TimelinePoint(tx, tz),
            FromRole = from, ToRole = to, ToBase = to == FieldPosition.FirstBase ? "first" : null,
        });

    private static PlaybackPlay BobbleGrounder() => new()
    {
        Name = "t", Dur = 9, Result = "t", ResAt = 5.3, BobbleError = true,
        Ball = new PlaybackBallSegment[]
        {
            Seg(BallSegmentKind.Pitch, 0.4, 0.85, 0, 18.44, 0, 0),
            Seg(BallSegmentKind.Bounce, 0.85, 2.13, 0, 0, -13.47, 23.86),
            Seg(BallSegmentKind.Throw, 4.28, 5.31, -13.47, 23.86, 19.4, 19.4, FieldPosition.ThirdBase, FieldPosition.FirstBase),
        },
        Moves = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>
        {
            [FieldPosition.ThirdBase] = new PlaybackMove[]
            {
                new(1.0, 2.0, new PlaybackVec(-13.0, 23.5)),      // 球へ寄る（捕球前）
                new(4.6, 7.0, new PlaybackVec(-17.0, 17.0)),      // 投げたあとの定位置復帰
            },
        },
    };

    [Fact]
    public void Fumble_BallLeavesTheHandWhereItWasPickedUp()
    {
        var play = BobbleGrounder();
        var after = HandleBobblePlanner.ApplyFumble(play, HandleBobblePlanner.Plan(play), 0.9, 0.47, 0.2);
        var release = 4.28;
        var before = PlaybackEvaluator.BallAt(after, release - 1e-4)!.Value;
        var during = PlaybackEvaluator.BallAt(after, release + 1e-4)!.Value;
        var jump = Math.Sqrt(Math.Pow(before.X - during.X, 2) + Math.Pow(before.Y - during.Y, 2));
        Assert.True(jump < 0.05, $"投げ始めに球が {jump:F2}m 跳んでいる");
        // 送球の時刻と終点は動かさない
        var throwSeg = (EndpointBallSegment)after.Ball.Last();
        Assert.Equal(4.28, throwSeg.T0, 6);
        Assert.Equal(5.31, throwSeg.T1, 6);
    }

    [Fact]
    public void Fumble_LaterFielderLegsAreKept()
    {
        var play = BobbleGrounder();
        var after = HandleBobblePlanner.ApplyFumble(play, HandleBobblePlanner.Plan(play), 0.9, 0.47, 0.2);
        var end = PlaybackEvaluator.FielderAt(after, FieldPosition.ThirdBase, 7.5).Pos;
        Assert.Equal(-17.0, end.X, 3);
        Assert.Equal(17.0, end.Y, 3);
    }

    // --- ④ 飛び出しの走者は、捕球が走り終わりと同時でも瞬間移動しない ---

    [Fact]
    public void DoubledOff_EarlyCatch_RunnerNeverTeleports()
    {
        var play = new FieldingPlay
        {
            Result = BattedBallResult.Out, IsFly = true,
            LandingX = 5.0, LandingZ = 40.0, HangTimeSeconds = 0.3, ApexHeightM = 8.0,
            RangeM = 40.3, BearingDeg = 7.0, FielderRole = FieldPosition.CenterField,
            FieldedAtSeconds = 0.3, BatterToFirstSeconds = 4.2,
        };
        var tl = TimelineBuilder.BuildBattedBall(play, Field.StandardAlignment(), Field, null, runnersOn: true,
            TimelineBuilder.DescribeResult(play));
        var move = new RunnerMove(new Player { Speed = 40 }, 1, 1, Out: false)
        {
            DoubledOffThrowArriveSeconds = 1.5, DoubledOffLeadM = 10.0, DoubledOffMaxLeadM = 12.0, CloseCall = true,
        };
        var legs = TimelineBuilder.AppendRunnerLegs(tl, new List<RunnerMove> { move }, Field, Baserunning, Fielding,
            flyCaughtAtSeconds: 0.3).Runners.Where(l => l.Label == "走1").OrderBy(l => l.T0).ToList();

        Assert.NotEmpty(legs);
        for (var i = 1; i < legs.Count; i++) Assert.Equal(legs[i - 1].To, legs[i].From);
    }

    // --- ⑤ 内野安打の結果テロップは一塁到達で出る（駆け抜けを待たない） ---

    [Fact]
    public void ResultShow_InfieldHit_UsesArrivalAtFirst_NotTheRunThrough()
    {
        var home = new PlaybackVec(0, 0);
        var first = new PlaybackVec(19.4, 19.4);
        var past = new PlaybackVec(21.9, 21.9);
        var play = new PlaybackPlay
        {
            Name = "t", Dur = 30, Result = "内野安打", ResAt = 4.3, Ball = Array.Empty<PlaybackBallSegment>(),
            Runners = new[]
            {
                new PlaybackRunner
                {
                    Label = "打",
                    Segs = new[] { new PlaybackRun(0.55, 4.3, home, first), new PlaybackRun(4.3, 4.9, first, past) },
                },
            },
            Judgements = Array.Empty<PlaybackJudgement>(),
        };
        Assert.Equal(4.3, PlaybackDisplaySync.ResultShowTime(play), 6);
    }
}
