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
/// 失策の連鎖（design-14 P1-6）で同一走者に積まれる連続 move（0→1 と 1→2 等）を、
/// タイムラインでは<b>1人の走者の続きのレッグ</b>として描く契約（issue error-extra-advance-phantom-runner）。
/// 直す前は 2本目の move が「打席前から一塁に居た走者」として扱われ、打った瞬間から一塁に幻の走者が立ち、
/// 失策より前に二塁へ走り出していた。表示専用・判定不変。
/// </summary>
public sealed class ErrorExtraAdvanceLegTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();
    private static readonly FieldingCoefficients Fielding = new();

    /// <summary>無死走者なし・遊撃の悪送球で打者が一塁へ（ThrowArriveSeconds=3.0 が失策の瞬間）。</summary>
    private static FieldingPlay ThrowError() => new()
    {
        Result = BattedBallResult.Error, ErrorKind = FieldingErrorKind.Throw,
        LandingX = -9.0, LandingZ = 30.0, HangTimeSeconds = 0.3, ApexHeightM = 0.4,
        RangeM = 31.3, BearingDeg = -16.0, FielderRole = FieldPosition.Shortstop,
        FieldedAtSeconds = 1.4, ThrowArriveSeconds = 3.0, BatterToFirstSeconds = 4.2, IsFly = false,
    };

    private static TimelinePoint Pt(KokoSim.Engine.Core.Vector3D p) => new(p.X, p.Z);
    private static readonly TimelinePoint First = Pt(Field.FirstBase);
    private static readonly TimelinePoint Second = Pt(Field.SecondBase);
    private static readonly TimelinePoint Third = Pt(Field.ThirdBase);

    private static PlayTimeline ErrorTimeline() => TimelineBuilder.BuildBattedBall(
        ThrowError(), Field.StandardAlignment(), Field, null, runnersOn: false,
        TimelineBuilder.DescribeResult(ThrowError()));

    [Fact]
    public void BatterRunner_ChainedAdvance_IsOneRunner_AndStartsAfterTheError()
    {
        var batter = new Player { Speed = 50 };
        // BaserunningModel が積む順: ApplyRunnerAdvance の 0→1、ApplyErrorExtraAdvance の 1→2（同一参照）。
        var moves = new List<RunnerMove> { new(batter, 0, 1, Out: false), new(batter, 1, 2, Out: false) };
        var tl = TimelineBuilder.AppendRunnerLegs(
            ErrorTimeline(), moves, Field, Baserunning, Fielding, errorAtSeconds: 3.0);

        // 幻の走者（"走1"）が出ない＝走者レッグは全部「打」。
        Assert.All(tl.Runners, l => Assert.Equal("打", l.Label));
        // 一塁での待機レッグ（T0=0・From=To=一塁）が無い。
        Assert.DoesNotContain(tl.Runners, l => l.T0 == 0.0 && l.From == l.To);

        // 一塁駆け抜け（#443）のレッグは続きがあるので外れている（同時刻に2方向へ走らない）。
        Assert.DoesNotContain(tl.Runners, l => l.Label == "打" && l.From == First && l.To != Second);
        // 二塁へのレッグは1本だけで、一塁到達の続き＆失策の瞬間以降に走り出す。
        var toSecond = Assert.Single(tl.Runners, l => l.To == Second);
        var arriveFirst = tl.Runners.Where(l => l.To == First).Max(l => l.T1);
        Assert.True(toSecond.T0 >= arriveFirst - 1e-9, $"二塁レッグ開始 {toSecond.T0:F2} が一塁到達 {arriveFirst:F2} より前");
        Assert.True(toSecond.T0 >= TimelineBuilder.ContactAt + 3.0 - 1e-9, $"二塁レッグ開始 {toSecond.T0:F2} が失策時刻より前");
        Assert.True(toSecond.T1 > toSecond.T0);
    }

    [Fact]
    public void RunnerOnFirst_ChainedAdvance_IsOneContinuousRunner()
    {
        var batter = new Player { Speed = 50 };
        var r1 = new Player { Speed = 55 };
        // 一塁走者あり: 1→2（フォース進塁）＋連鎖の 2→3、打者は 0→1＋1→2。
        var moves = new List<RunnerMove>
        {
            new(r1, 1, 2, Out: false), new(batter, 0, 1, Out: false),
            new(r1, 2, 3, Out: false), new(batter, 1, 2, Out: false),
        };
        var tl = TimelineBuilder.AppendRunnerLegs(
            ErrorTimeline(), moves, Field, Baserunning, Fielding, errorAtSeconds: 3.0);

        var labels = tl.Runners.Select(l => l.Label).Distinct().OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "打", "走1" }, labels);

        // 一塁走者は 一塁(待機)→二塁→三塁 の1本の続き。二塁での待機レッグ（T0=0）は無い。
        var r1Legs = tl.Runners.Where(l => l.Label == "走1").OrderBy(l => l.T0).ToList();
        Assert.Single(r1Legs, l => l.T0 == 0.0);
        Assert.Equal(First, r1Legs[0].From);
        var toThird = Assert.Single(r1Legs, l => l.To == Third);
        var arriveSecond = r1Legs.Single(l => l.To == Second).T1;
        Assert.True(toThird.T0 >= arriveSecond - 1e-9);
        Assert.True(toThird.T0 >= TimelineBuilder.ContactAt + 3.0 - 1e-9);
        // 走者は同一参照
        Assert.All(r1Legs, l => Assert.Same(r1, l.Runner));
    }

    [Fact]
    public void UnrelatedMoves_AreNotMerged()
    {
        // 別人（能力が同じでも参照が違う）の 1→2 と 2→3 は連結しない（#627 と同じ注意）。
        var a = new Player { Speed = 50 };
        var b = new Player { Speed = 50 };
        var moves = new List<RunnerMove> { new(a, 1, 2, Out: false), new(b, 2, 3, Out: false) };
        var tl = TimelineBuilder.AppendRunnerLegs(ErrorTimeline(), moves, Field, Baserunning, Fielding);
        var labels = tl.Runners.Select(l => l.Label).Where(l => l != "打").Distinct().OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "走1", "走2" }, labels);
    }
}
