using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 打球4分類（Issue #63）の TimelineBuilder / PlaybackModel への波及検証。
/// バウンド打球は Bounce セグメント（頂点付き）で表現され、再生モデルが跳ねる高さを描けること。
/// </summary>
public sealed class BounceTimelineTests
{
    private static FieldingPlay Bouncer(bool through = false) => new()
    {
        Result = through ? BattedBallResult.Single : BattedBallResult.Out,
        LandingX = 2.0, LandingZ = 12.0, HangTimeSeconds = 0.2, ApexHeightM = 0.4,
        RangeM = 12.2, BearingDeg = 6.0, FielderRole = FieldPosition.Shortstop,
        FieldedAtSeconds = 1.6, ThrowArriveSeconds = through ? null : 3.0,
        FieldedX = through ? 3.0 : 2.0, FieldedZ = through ? 60.0 : 12.0,
        BatterToFirstSeconds = 4.2, IsFly = false,
        Class = BattedBallClass.Bouncer, BounceApexHeightM = 1.8, BounceCount = 3,
        ThroughInfield = through,
    };

    [Fact]
    public void BouncerPlay_EmitsBounceSegment_WithApex()
    {
        var field = new FieldGeometry();
        var play = Bouncer();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        // バウンド分類は Roll ではなく Bounce セグメント（頂点＝最大バウンド頂点）。
        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Roll);
        var bounce = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Bounce);
        Assert.Equal(play.BounceApexHeightM, bounce.ApexHeightM, 6);
    }

    [Fact]
    public void GrounderPlay_StillEmitsRoll_NotBounce()
    {
        var field = new FieldGeometry();
        var play = Bouncer() with { Class = BattedBallClass.Grounder, BounceApexHeightM = 0.3 };
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        Assert.Contains(tl.Ball, b => b.Kind == BallSegmentKind.Roll);
        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Bounce);
    }

    [Fact]
    public void ThroughInfieldBouncer_BallTravelsToOutfieldFieldPoint()
    {
        var field = new FieldGeometry();
        var play = Bouncer(through: true);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        // 内野を抜けたバウンドは、内野の着地点で止まらず外野の回収点まで届く（ボールが内野で死なない）。
        var bounce = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Bounce);
        Assert.Equal(play.FieldedZ, bounce.To.Z, 6);
    }

    [Fact]
    public void PlaybackAdapter_BounceSegment_HasBouncingHeight()
    {
        var field = new FieldGeometry();
        var play = Bouncer();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        var pb = PlayTimelineAdapter.ToPlaybackPlay(tl);

        var bounceSeg = pb.Ball.First(s => s is EndpointBallSegment);
        // セグメント途中で高さが 0 を離れて跳ねる（地を這う Roll と区別できる）。
        var maxH = 0.0;
        var dur = bounceSeg.T1 - bounceSeg.T0;
        for (var u = 0.0; u <= 1.0; u += 0.02)
        {
            var h = bounceSeg.At(u * dur).H;
            if (h > maxH) maxH = h;
        }
        Assert.True(maxH > 0.5, $"バウンドの高さが出ていない（max {maxH:F2}m）");
    }
}
