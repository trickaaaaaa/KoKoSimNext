using System.Linq;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// ファウル打球の再生タイムライン（Issue #313）。物理層を持つファウルだけが対象で、
/// 守備解決（FieldingResolver）を経由せず <see cref="BallisticIntegrator"/> を直接使う。
/// </summary>
public sealed class FoulFlightTimelineTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldingCoefficients Coeff = new();
    private static readonly FieldGeometry Field = new();

    /// <summary>高く上がるファウルフライ（一塁側スタンド方向）。Flight セグメントで放物線を描く。</summary>
    [Fact]
    public void HighFoulPop_ProducesFlightSegmentWithRealPhysics()
    {
        var ball = new BattedBall { ExitVelocityMps = 35.0, LaunchAngleDeg = 65.0, BearingDeg = 60.0 };
        var timeline = TimelineBuilder.BuildFoulFlight(ball, Aero, Coeff, Field, false, false, false);

        Assert.Equal("ファウル", timeline.Result);
        Assert.Equal(2, timeline.Ball.Count);
        Assert.Equal(BallSegmentKind.Pitch, timeline.Ball[0].Kind);

        var flight = timeline.Ball[1];
        Assert.Equal(BallSegmentKind.Flight, flight.Kind);
        Assert.True(flight.ApexHeightM > Coeff.FlyApexThresholdM, "高く上がった打球は頂点が閾値を超える");

        // 直接 BallisticIntegrator を回した結果と一致する（守備解決を経由しない同一物理の使い回し）。
        var ground = BallisticIntegrator.IntegrateToGround(ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), Aero);
        Assert.Equal(ground.LandingPosition.X, flight.To.X, 6);
        Assert.Equal(ground.LandingPosition.Z, flight.To.Z, 6);
        Assert.Equal(ground.ApexHeightM, flight.ApexHeightM, 6);

        Assert.Equal(flight.T1, timeline.ResolvedAt);
        Assert.True(timeline.Duration > timeline.ResolvedAt, "着地後に一呼吸の余韻を持つ");
        Assert.Empty(timeline.Moves);
        Assert.Empty(timeline.Judgements);
    }

    /// <summary>低い打球角の打ち損じファウル。頂点が低いので Roll（地を這う）扱いになる。</summary>
    [Fact]
    public void LowFoulLiner_ProducesRollSegment()
    {
        var ball = new BattedBall { ExitVelocityMps = 20.0, LaunchAngleDeg = 2.0, BearingDeg = -55.0 };
        var timeline = TimelineBuilder.BuildFoulFlight(ball, Aero, Coeff, Field, false, false, false);

        var groundSeg = timeline.Ball[1];
        Assert.Equal(BallSegmentKind.Roll, groundSeg.Kind);
        Assert.True(groundSeg.ApexHeightM < Coeff.FlyApexThresholdM);
    }

    /// <summary>塁上の走者は動かず、その場に静止表示するレッグを持つ（PitchPlaybackFactory.PitchOnly と同じ扱い）。</summary>
    [Fact]
    public void OccupiedBases_GetHeldRunnerLegs()
    {
        var ball = new BattedBall { ExitVelocityMps = 30.0, LaunchAngleDeg = 40.0, BearingDeg = 50.0 };
        var timeline = TimelineBuilder.BuildFoulFlight(ball, Aero, Coeff, Field, true, false, true);

        Assert.Equal(2, timeline.Runners.Count);
        var first = Assert.Single(timeline.Runners, r => r.Label == "走一");
        Assert.Equal(0, first.T0);
        Assert.Equal(timeline.Duration, first.T1);
        Assert.Equal(first.From, first.To); // 静止＝移動なし
        Assert.False(first.OutAtEnd);
        Assert.Contains(timeline.Runners, r => r.Label == "走三");
        Assert.DoesNotContain(timeline.Runners, r => r.Label == "走二");
    }
}
