using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 滞空プロファイルの打ち出し高さ（issue duel-view-fly-leaves-bat-flat-then-rises）。
/// 打球の Flight は h(0)=打点から**最初のフレームで上がり始め**、頂点はほぼ ApexHeightM・着地は 0 のまま。
/// LaunchHeightM=0（送球等の従来セグメント）は旧式と同値。表示専用・帯不変。
/// </summary>
public sealed class FlightLaunchHeightTests
{
    private static EndpointBallSegment Flight(double apex, double launch) => EndpointBallSegment.From(new BallSegment
    {
        Kind = BallSegmentKind.Flight, T0 = 0.85, T1 = 0.85 + 4.0,
        From = new TimelinePoint(0, 0), To = new TimelinePoint(0, 90), ApexHeightM = apex, LaunchHeightM = launch,
    });

    [Fact]
    public void FlyBall_StartsAtLaunchHeight_AndRisesImmediately()
    {
        var seg = Flight(apex: 20.0, launch: 1.0);
        var h0 = seg.At(0.0).H;
        Assert.Equal(1.0, h0, 6);
        // 平らな区間が無い＝出だしから単調に上がる（0.09s の平走が「弾んだ」絵の正体だった）
        var prev = h0;
        for (var t = 0.02; t <= 1.0; t += 0.02)
        {
            var h = seg.At(t).H;
            Assert.True(h > prev, $"t={t:F2}: {h:F3} <= {prev:F3}（出だしが平ら／沈む）");
            prev = h;
        }
        // 頂点はほぼ ApexHeightM（1cm 以内）、着地は 0
        Assert.InRange(seg.At(2.0).H, 19.99, 20.02);
        Assert.Equal(0.0, seg.At(4.0).H, 6);
    }

    [Fact]
    public void LaunchZero_IsLegacyProfile()
    {
        var seg = Flight(apex: 20.0, launch: 0.0);
        Assert.Equal(0.0, seg.At(0.0).H, 9);
        Assert.Equal(20.0, seg.At(2.0).H, 9);
        Assert.Equal(20.0 * 4 * 0.25 * 0.75, seg.At(1.0).H, 9);
    }

    [Fact]
    public void ResolvedFlyBall_CarriesContactHeight_IntoFlightSegment()
    {
        // FieldingResolver → FieldingPlay.LaunchHeightM → TimelineBuilder の Flight セグメントまで素通しされる。
        var field = new KokoSim.Engine.Match.Field.FieldGeometry();
        var play = new KokoSim.Engine.Match.Fielding.FieldingPlay
        {
            Result = KokoSim.Engine.Match.Fielding.BattedBallResult.Out,
            LandingX = 0, LandingZ = 80, HangTimeSeconds = 4.0, ApexHeightM = 20.0, LaunchHeightM = 1.0,
            RangeM = 80, BearingDeg = 0, FielderRole = KokoSim.Engine.Match.Field.FieldPosition.CenterField,
            FieldedAtSeconds = 4.0, IsFly = true, BatterToFirstSeconds = 4.2,
        };
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        var flight = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Flight);
        Assert.Equal(1.0, flight.LaunchHeightM, 9);
    }
}
