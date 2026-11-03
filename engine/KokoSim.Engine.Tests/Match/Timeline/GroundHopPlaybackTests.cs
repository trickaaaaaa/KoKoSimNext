using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// ゴロ系の打球を、エンジンの実際の弾み列どおりの高さで描く（issue #759・表示専用）。
/// 従来は「バウンド」分類が決め打ちの3山、「ゴロ」分類は高さ0の地這いだった。
/// </summary>
public class GroundHopPlaybackTests
{
    private static FieldingPlay Resolve(double exitMps, double launchDeg, double bearingDeg)
    {
        var field = new FieldGeometry();
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, IrregularBounceProb = 0 };
        return FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = exitMps, LaunchAngleDeg = launchDeg, BearingDeg = bearingDeg },
            field, new Aerodynamics(), BatterAttributes.LeagueAverage, field.StandardAlignment(), coeff,
            new Xoshiro256Random(42));
    }

    private static EndpointBallSegment GroundSegment(FieldingPlay play)
    {
        var field = new FieldGeometry();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        var seg = Assert.Single(tl.Ball, b => b.Kind is BallSegmentKind.Roll or BallSegmentKind.Bounce);
        Assert.NotNull(seg.Hops);
        return EndpointBallSegment.From(seg);
    }

    private static double MaxHeight(EndpointBallSegment seg, double fromU = 0.0)
    {
        var dur = seg.T1 - seg.T0;
        var max = 0.0;
        for (var i = (int)(fromU * 2000); i <= 2000; i++) max = Math.Max(max, seg.At(dur * i / 2000.0).H);
        return max;
    }

    [Fact]
    public void Chopper_IsDrawnAtItsRealBounceApex()
    {
        // 強く叩きつけた打球＝高いバウンド
        var play = Resolve(exitMps: 40.0, launchDeg: -35.0, bearingDeg: -15.0);
        Assert.Equal(BattedBallClass.Bouncer, play.Class);
        Assert.False(play.ThroughInfield);
        Assert.True(play.BounceApexHeightM >= 2.0, $"頂点 {play.BounceApexHeightM:F2}m");

        var seg = GroundSegment(play);
        var landU = play.HangTimeSeconds / play.GroundPathCatchSeconds!.Value;
        // 最初の接地のあとの最高点 ≒ エンジンの最大バウンド頂点（サンプリング誤差 2%）
        Assert.InRange(MaxHeight(seg, landU), play.BounceApexHeightM * 0.98, play.BounceApexHeightM * 1.001);
    }

    [Fact]
    public void Grounder_IsNoLongerFlatOnTheGround_ButStartsAtTheBat()
    {
        var play = Resolve(exitMps: 38.0, launchDeg: -6.0, bearingDeg: -20.0);
        Assert.Equal(BattedBallClass.Grounder, play.Class);
        Assert.False(play.ThroughInfield);
        var seg = GroundSegment(play);

        // 打点の高さから始まる（従来の地這いは 0）
        Assert.Equal(play.LaunchHeightM, seg.At(0).H, 3);
        Assert.True(play.LaunchHeightM > 0.3);
        // 着地後も小さく弾む（ゴロ分類＝頂点 1.2m 未満）
        var landU = play.HangTimeSeconds / play.GroundPathCatchSeconds!.Value;
        var afterLanding = MaxHeight(seg, landU + 1e-3);
        Assert.InRange(afterLanding, 0.01, 1.2);
    }

    [Fact]
    public void HopHeight_ReachesApex_AndTouchesDownAtBothEnds()
    {
        var hops = new[] { new SegmentHop(0.0, 0.2, 1.5, 1.0), new SegmentHop(0.2, 0.6, 3.0) };
        Assert.Equal(1.0, EndpointBallSegment.HopHeight(hops, 0.0), 6);
        Assert.Equal(0.0, EndpointBallSegment.HopHeight(hops, 0.2), 6);
        Assert.Equal(3.0, EndpointBallSegment.HopHeight(hops, 0.4), 6); // 始点0の山は中央が頂点
        Assert.Equal(0.0, EndpointBallSegment.HopHeight(hops, 0.8), 6); // 山の後は転がり
        var peak = 0.0;
        for (var i = 0; i <= 1000; i++) peak = Math.Max(peak, EndpointBallSegment.HopHeight(hops, 0.2 * i / 1000));
        Assert.InRange(peak, 1.499, 1.5001); // 打点1.0mから頂点1.5mを通る
    }

    [Fact]
    public void ThroughInfield_KeepsTheOldProfile()
    {
        // 内野を抜けた打球には弾み列を載せない（区間が外野の回収点まで伸びるため）
        var checkedCount = 0;
        for (var exit = 30.0; exit <= 48.0; exit += 2.0)
        for (var launch = -8.0; launch <= 8.0; launch += 2.0)
        foreach (var bearing in new[] { -44.0, -32.0, -20.0, -12.0 })
        {
            var play = Resolve(exit, launch, bearing);
            if (!play.ThroughInfield) continue;
            Assert.Null(play.GroundHops);
            checkedCount++;
        }
        Assert.True(checkedCount > 0);
    }
}
