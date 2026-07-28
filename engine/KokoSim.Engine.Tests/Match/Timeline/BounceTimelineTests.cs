using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 打球4分類（Issue #63）の TimelineBuilder / PlaybackModel への波及検証。
/// バウンド打球は Bounce セグメント（頂点付き）で表現され、再生モデルが跳ねる高さを描けること。
/// </summary>
public sealed class BounceTimelineTests
{
    /// <summary>
    /// 内野へ落ちる高いバウンド。<paramref name="through"/> で内野を抜けて外野の回収点(60m)まで届く。
    /// 抜けた側の処理野手は**外野手**（回収役は転がり経路に一番近い野手＝<c>ExtraBaseResolver.Retrieve</c>
    /// が選ぶので、60m地点をショートが処理する出力はリゾルバが作れない, Issue #315）。
    /// </summary>
    private static FieldingPlay Bouncer(bool through = false) => through
        ? FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 2.0, landingZ: 12.0,
            fieldedAtSeconds: 1.6, fieldedX: 3.0, fieldedZ: 60.0,
            hangTimeSeconds: 0.2, apexHeightM: 0.4,
            ballClass: BattedBallClass.Bouncer, throughInfield: true,
            bounceApexHeightM: 1.8, bounceCount: 3)
        : FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.Shortstop, fieldedX: 2.0, fieldedZ: 12.0,
            fieldedAtSeconds: 1.6, throwArriveSeconds: 3.0,
            hangTimeSeconds: 0.2, apexHeightM: 0.4,
            ballClass: BattedBallClass.Bouncer, bounceApexHeightM: 1.8, bounceCount: 3);

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
        Assert.Equal(play.FieldedZ!.Value, bounce.To.Z, 6);
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
