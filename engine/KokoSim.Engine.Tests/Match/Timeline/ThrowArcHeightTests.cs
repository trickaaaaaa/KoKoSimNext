using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 送球の弧（表示演出・issue #764）。旧式（距離/9）は速さを見ず、遊撃→一塁は肩に関係なく4.2m上がる山なりだった。
/// 弧は飛翔時間だけで決まる重力の放物線＝強肩ほど低く速い。
/// </summary>
public sealed class ThrowArcHeightTests
{
    private const double G = 9.80665;

    private static EndpointBallSegment Throw(double distM, double flightSeconds)
        => EndpointBallSegment.From(new BallSegment
        {
            Kind = BallSegmentKind.Throw, T0 = 0, T1 = flightSeconds,
            From = new TimelinePoint(0, 0), To = new TimelinePoint(distM, 0),
        });

    [Fact]
    public void MatchesGravityOnlyFreeFlight()
    {
        // 同じ高さで離して捕る自由飛行の厳密解: h(t) = h0 + (gT/2)·t − g·t²/2。
        const double T = 1.2;
        var seg = Throw(38.0, T);
        for (var i = 0; i <= 10; i++)
        {
            var t = T * i / 10.0;
            var exact = EndpointBallSegment.ThrowReleaseHeightM + G * T / 2 * t - G * t * t / 2;
            Assert.Equal(exact, seg.At(t).H, 9);
        }
    }

    [Fact]
    public void StrongArm_ThrowsFlatter()
    {
        // 遊撃→一塁 38m。肩100=140km/h／肩20=100km/h（ArmThrowSpeedTests と同じ換算）。
        double Rise(double kmh)
        {
            var t = 38.0 / (kmh / 3.6);
            return Throw(38.0, t).At(t / 2).H - EndpointBallSegment.ThrowReleaseHeightM;
        }
        var strong = Rise(140);
        var weak = Rise(100);
        Assert.InRange(strong, 1.0, 1.4);   // ≈1.2m
        Assert.InRange(weak, 2.1, 2.5);     // ≈2.3m
        Assert.True(strong < weak);
    }
}
