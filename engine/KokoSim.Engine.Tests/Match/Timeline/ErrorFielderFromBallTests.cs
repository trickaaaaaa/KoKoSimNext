using System;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 3D の「！」の付け先（<see cref="PlaybackEvaluator.ErrorFielderFromBall"/>）。
/// 受け先が塁でない送球を全部「悪送球」とみなすと、お手玉→拾った外野手が中継へ返す1本目
/// （ToBase なし）が後勝ちで「！」を奪っていた。
/// </summary>
public sealed class ErrorFielderFromBallTests
{
    private static PlaybackBallSegment Seg(
        BallSegmentKind kind, double t0, double t1, FieldPosition from, FieldPosition? to = null, string? toBase = null)
        => EndpointBallSegment.From(new BallSegment
        {
            Kind = kind, T0 = t0, T1 = t1,
            From = new TimelinePoint(0, 20), To = new TimelinePoint(0, 40),
            FromRole = from, ToRole = to, ToBase = toBase,
        });

    private static PlaybackPlay PlayOf(params PlaybackBallSegment[] ball) => new()
    {
        Name = "test", Dur = 10, Result = "test", ResAt = 1, Ball = ball,
    };

    [Fact]
    public void Fumble_IsTheFielderWhoSpilledIt()
    {
        var play = PlayOf(Seg(BallSegmentKind.Roll, 2.0, 2.4, FieldPosition.Shortstop));
        Assert.Equal(FieldPosition.Shortstop, PlaybackEvaluator.ErrorFielderFromBall(play, 2.0));
    }

    [Fact]
    public void WildThrow_IsTheThrower_WhenItStartsBeforeTheErrorMoment()
    {
        // 悪送球: 一塁へ届くはずだった時刻（＝エラー確定 3.0）へ向けて 2.2 から投げ始める
        var play = PlayOf(Seg(BallSegmentKind.Throw, 2.2, 3.4, FieldPosition.ThirdBase));
        Assert.Equal(FieldPosition.ThirdBase, PlaybackEvaluator.ErrorFielderFromBall(play, 3.0));
    }

    [Fact]
    public void RelayThrowAfterTheBobble_DoesNotStealTheMark()
    {
        // 遊撃のお手玉（2.0）→ 左翼が拾って中継（二塁）へ返す送球（ToBase なし・4.5 から）
        var play = PlayOf(
            Seg(BallSegmentKind.Roll, 2.0, 2.4, FieldPosition.Shortstop),
            Seg(BallSegmentKind.Throw, 4.5, 5.2, FieldPosition.LeftField, FieldPosition.SecondBase));
        Assert.Equal(FieldPosition.Shortstop, PlaybackEvaluator.ErrorFielderFromBall(play, 2.0));
    }

    [Fact]
    public void RelayThrowOnly_YieldsNobody()
    {
        var play = PlayOf(Seg(BallSegmentKind.Throw, 4.5, 5.2, FieldPosition.LeftField, FieldPosition.SecondBase));
        Assert.Null(PlaybackEvaluator.ErrorFielderFromBall(play, 2.0));
    }

    [Fact]
    public void ThrowToABase_IsNeverAWildThrow()
    {
        var play = PlayOf(Seg(BallSegmentKind.Throw, 2.2, 3.4, FieldPosition.ThirdBase, FieldPosition.FirstBase, "first"));
        Assert.Null(PlaybackEvaluator.ErrorFielderFromBall(play, 3.0));
    }
}
