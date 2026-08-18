using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 飛び出し（DoubledOff）の絵（issue fly-out-runner-dp-table-and-doubled-off-depiction）。
/// 一塁走者は二塁方向へ飛び出し→捕球で反転→一塁へ戻る（Returning）。憤死は戻し送球の到達で Out 判定、
/// 帰塁セーフも「飛び出して戻った」レッグを持つ。戻し送球は捕球点→一塁の Throw セグメント。
/// </summary>
public sealed class DoubledOffDepictionTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();
    private static readonly FieldingCoefficients Fielding = new();

    private static FieldingPlay ShallowFlyOut() => new()
    {
        Result = BattedBallResult.Out, IsFly = true,
        LandingX = 5.0, LandingZ = 40.0, HangTimeSeconds = 2.2, ApexHeightM = 8.0,
        RangeM = 40.3, BearingDeg = 7.0, FielderRole = FieldPosition.CenterField,
        FieldedAtSeconds = 2.2, BatterToFirstSeconds = 4.2,
    };

    private static PlayTimeline FlyTimeline() => TimelineBuilder.BuildBattedBall(
        ShallowFlyOut(), Field.StandardAlignment(), Field, null, runnersOn: true,
        TimelineBuilder.DescribeResult(ShallowFlyOut()));

    private static TimelinePoint First => new(Field.FirstBase.X, Field.FirstBase.Z);

    private static PlayTimeline Build(bool doubledOff, out RunnerMove move)
    {
        var r1 = new Player { Speed = 40 };
        move = new RunnerMove(r1, 1, 1, Out: doubledOff)
        {
            DoubledOffThrowArriveSeconds = 2.2 + 1.2, DoubledOffLeadM = 10.0, DoubledOffMaxLeadM = 12.0, CloseCall = !doubledOff,
        };
        var moves = new List<RunnerMove> { move };
        var tl = TimelineBuilder.AppendRunnerLegs(FlyTimeline(), moves, Field, Baserunning, Fielding, flyCaughtAtSeconds: 2.2);
        return TimelineBuilder.AppendDoubledOffThrow(tl, ShallowFlyOut(), moves, Field, Baserunning);
    }

    [Fact]
    public void DoubledOff_RunnerBreaksTowardSecond_ReversesAtCatch_AndIsOutReturning()
    {
        var tl = Build(doubledOff: true, out var move);
        var legs = tl.Runners.Where(l => l.Label == "走1").OrderBy(l => l.T0).ToList();
        Assert.NotEmpty(legs);

        // 二塁方向へ出るレッグ（To が一塁より二塁寄り）があり、最大リード（12m＋二次リード）まで出る
        var toward = legs.OrderByDescending(l => Dist(l.To, First)).First();
        Assert.True(Dist(toward.To, First) > Dist(toward.From, First));
        Assert.Equal(12.0 + Baserunning.HomeLeadDistanceM, Dist(toward.To, First), 3);
        Assert.True(toward.T1 <= TimelineBuilder.ContactAt + 1.0 + 1e-9, "コミット上限より後まで飛び出し続けている");
        // 折り返し点から捕球まで塁の方へ戻り（10m の点）、捕球＋判断遅延で反転する
        var retreat = legs.First(l => l.T0 >= toward.T1 - 1e-9 && l.To != l.From);
        Assert.Equal(10.0 + Baserunning.HomeLeadDistanceM, Dist(retreat.To, First), 3);
        Assert.Equal(TimelineBuilder.ContactAt + 2.2, retreat.T1, 6);

        // 反転は捕球＋判断遅延から。帰塁レッグは Returning・終端で Out
        var back = legs.Last();
        Assert.True(back.Returning);
        Assert.True(back.OutAtEnd);
        Assert.Equal(First, back.To);
        Assert.Equal(TimelineBuilder.ContactAt + 2.2 + Baserunning.DoubledOffReverseSeconds, back.T0, 6);
        // 戻りの尺は判定と同じ「リード÷脚力」
        var speed = move.Runner.ToFielder(Baserunning.AbilityEffect).SprintSpeedMps;
        Assert.Equal(back.T0 + 10.0 / speed, back.T1, 6);
        // 全レッグが連続（同一走者が瞬間移動しない）
        for (var i = 1; i < legs.Count; i++) Assert.Equal(legs[i - 1].To, legs[i].From);

        // Out 判定は戻し送球の到達（判定の守備所要）で確定
        var j = Assert.Single(tl.Judgements, j => j.Label == "走1");
        Assert.Equal(JudgementKind.Out, j.Kind);
        Assert.Equal(TimelineBuilder.ContactAt + 2.2 + 1.2, j.T, 6);

        // 戻し送球: 捕球点→一塁・投げ手は捕った中堅手
        var thr = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw && b.ToBase == "first");
        Assert.Equal(FieldPosition.CenterField, thr.FromRole);
        Assert.Equal(First, thr.To);
        Assert.True(thr.T0 >= TimelineBuilder.ContactAt + 2.2);
        Assert.Equal(TimelineBuilder.ContactAt + 2.2 + 1.2 - Baserunning.DoubledOffTagSeconds, thr.T1, 6);
    }

    [Fact]
    public void SafeReturn_IsDepictedToo_WithoutAnOut()
    {
        var tl = Build(doubledOff: false, out _);
        var legs = tl.Runners.Where(l => l.Label == "走1").ToList();
        Assert.NotEmpty(legs);
        Assert.DoesNotContain(legs, l => l.OutAtEnd);
        Assert.Contains(legs, l => l.Returning && l.To == First);
        // 際どい帰塁はセーフ判定を出す
        var j = Assert.Single(tl.Judgements, j => j.Label == "走1");
        Assert.Equal(JudgementKind.Safe, j.Kind);
        Assert.True(j.CloseCall);
    }

    [Fact]
    public void NoThrow_WhenARunnerScoresOnTheSamePlay()
    {
        var r1 = new Player { Speed = 40 };
        var r3 = new Player { Speed = 60 };
        var moves = new List<RunnerMove>
        {
            new(r3, 3, 4, Out: false) { TagUpCatchAtSeconds = 2.2 },
            new(r1, 1, 1, Out: false) { DoubledOffThrowArriveSeconds = 4.1, DoubledOffLeadM = 10.0 },
        };
        var tl = TimelineBuilder.AppendRunnerLegs(FlyTimeline(), moves, Field, Baserunning, Fielding, flyCaughtAtSeconds: 2.2);
        var before = tl.Ball.Count;
        tl = TimelineBuilder.AppendDoubledOffThrow(tl, ShallowFlyOut(), moves, Field, Baserunning);
        Assert.Equal(before, tl.Ball.Count);
    }

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
