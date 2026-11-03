using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>内野ゴロの「捕ってから投げるまで」の見せ方（表示専用・2026-11-03 監督報告「サードが怠慢に見える」）。</summary>
public sealed class HandleBobbleTests
{
    private static EndpointBallSegment Seg(BallSegmentKind kind, double t0, double t1, double fx, double fz, double tx, double tz,
        FieldPosition? from = null, FieldPosition? to = null)
        => EndpointBallSegment.From(new BallSegment
        {
            Kind = kind, T0 = t0, T1 = t1, From = new TimelinePoint(fx, fz), To = new TimelinePoint(tx, tz),
            FromRole = from, ToRole = to, ToBase = to == FieldPosition.FirstBase ? "first" : null,
        });

    /// <summary>この打席を写した形: 投球 → バウンド（サードが捕る）→ 一塁送球。</summary>
    private static PlaybackPlay Grounder(double catchT, double releaseT, bool fumbled = false) => new()
    {
        Name = "t", Dur = releaseT + 3, Result = "t", ResAt = releaseT + 1, Fumbled = fumbled,
        Ball = new PlaybackBallSegment[]
        {
            Seg(BallSegmentKind.Pitch, 0.4, 0.85, 0, 18.44, 0, 0),
            Seg(BallSegmentKind.Bounce, 0.85, catchT, 0, 0, -13.47, 23.86),
            Seg(BallSegmentKind.Throw, releaseT, releaseT + 1.03, -13.47, 23.86, 19.4, 19.4,
                FieldPosition.ThirdBase, FieldPosition.FirstBase),
        },
    };

    [Theory]
    [InlineData(0.9, HandleBobbleKind.None)]
    [InlineData(1.4, HandleBobbleKind.None)]
    [InlineData(1.41, HandleBobbleKind.Juggle)]
    [InlineData(2.0, HandleBobbleKind.Juggle)]
    [InlineData(2.15, HandleBobbleKind.Fumble)]
    public void KindFollowsHoldLength(double hold, HandleBobbleKind want)
    {
        var b = HandleBobblePlanner.Plan(Grounder(2.13, 2.13 + hold));
        Assert.Equal(want, b.Kind);
        if (want != HandleBobbleKind.None) Assert.Equal(FieldPosition.ThirdBase, b.Fielder);
    }

    [Fact]
    public void RealErrorIsLeftAlone()
        => Assert.Equal(HandleBobbleKind.None, HandleBobblePlanner.Plan(Grounder(2.13, 4.28, fumbled: true)).Kind);

    [Fact]
    public void FumbleDeflectsForwardAndKeepsTheThrowUntouched()
    {
        var play = Grounder(2.13, 4.28);
        var b = HandleBobblePlanner.Plan(play);
        var after = HandleBobblePlanner.ApplyFumble(play, b, recoverSeconds: 0.9);

        Assert.Equal(play.Ball.Count + 1, after.Ball.Count);
        var bobble = (EndpointBallSegment)after.Ball[2];
        Assert.Equal(BallSegmentKind.Roll, bobble.Kind);
        Assert.Equal(2.13, bobble.T0, 6);
        // 拾うのは投げ始めの PickupLeadSeconds 前
        Assert.Equal(4.28 - HandleBobblePlanner.PickupLeadSeconds, bobble.T1, 6);
        var start = bobble.At(0);
        var rest = bobble.At(bobble.T1 - bobble.T0);
        Assert.True(rest.Y > start.Y, "本塁から遠ざかる向きへこぼれる");
        // 送球はそのまま（判定の時刻を動かさない）
        Assert.Same(play.Ball[2], after.Ball[3]);
    }

    [Fact]
    public void FielderWalksToTheBallAfterTheBobbleAndArrivesBeforeThePickup()
    {
        var play = Grounder(2.13, 4.40);
        var after = HandleBobblePlanner.ApplyFumble(play, HandleBobblePlanner.Plan(play), recoverSeconds: 0.9);
        var leg = after.Moves[FieldPosition.ThirdBase].Single();
        Assert.Equal(2.13 + 0.9, leg.T0, 6);
        var pickup = after.Ball[2].T1;
        Assert.True(leg.T1 <= pickup, "拾う前に球の所へ着く");
        var rest = after.Ball[2].At(after.Ball[2].T1 - after.Ball[2].T0);
        var gap = System.Math.Sqrt((rest.X - leg.To.X) * (rest.X - leg.To.X) + (rest.Y - leg.To.Y) * (rest.Y - leg.To.Y));
        Assert.Equal(HandleBobblePlanner.PickupStandOffM, gap, 3);
    }

    [Fact]
    public void NoTimeToWalk_PicksItUpOnTheSpot()
    {
        var play = Grounder(2.13, 4.20);
        var after = HandleBobblePlanner.ApplyFumble(play, HandleBobblePlanner.Plan(play), recoverSeconds: 1.5);
        var seg = after.Ball[2];
        var a = seg.At(0); var z = seg.At(seg.T1 - seg.T0);
        var d = System.Math.Sqrt((z.X - a.X) * (z.X - a.X) + (z.Y - a.Y) * (z.Y - a.Y));
        Assert.Equal(HandleBobblePlanner.MinDeflectM, d, 3);
        Assert.False(after.Moves.ContainsKey(FieldPosition.ThirdBase));
    }

    [Fact]
    public void FlyBallsAreNotTouched()
    {
        var play = Grounder(2.13, 4.28) with
        {
            Ball = new PlaybackBallSegment[]
            {
                Seg(BallSegmentKind.Pitch, 0.4, 0.85, 0, 18.44, 0, 0),
                Seg(BallSegmentKind.Flight, 0.85, 3.0, 0, 0, 0, 60),
                Seg(BallSegmentKind.Roll, 3.0, 5.0, 0, 60, 0, 65),
                Seg(BallSegmentKind.Throw, 8.0, 9.0, 0, 65, 0, 40, FieldPosition.CenterField, FieldPosition.Shortstop),
            },
        };
        Assert.Equal(HandleBobbleKind.None, HandleBobblePlanner.Plan(play).Kind);
    }
}
