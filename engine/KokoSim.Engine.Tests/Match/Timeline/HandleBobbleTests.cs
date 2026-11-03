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

    private const double GloveReach = 0.47;
    private const double PickupReach = 0.2;

    private static double Dist(double ax, double ay, double bx, double by)
        => System.Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    [Fact]
    public void FumblePopsOutOfTheGloveAndKeepsTheThrowUntouched()
    {
        var play = Grounder(2.13, 4.28);
        var b = HandleBobblePlanner.Plan(play);
        var after = HandleBobblePlanner.ApplyFumble(play, b, recoverSeconds: 0.9, GloveReach, PickupReach);

        Assert.Equal(play.Ball.Count + 1, after.Ball.Count);
        var bobble = (EndpointBallSegment)after.Ball[2];
        Assert.Equal(BallSegmentKind.Roll, bobble.Kind);
        Assert.Equal(2.13, bobble.T0, 6);
        // 拾うのは投げ始めの PickupLeadSeconds 前
        Assert.Equal(4.28 - HandleBobblePlanner.PickupLeadSeconds, bobble.T1, 6);
        // 弾く瞬間の球はグラブの所（野手から GloveReach）に居て、そこから外へこぼれる
        var fp = PlaybackEvaluator.FielderAt(play, FieldPosition.ThirdBase, 2.13).Pos;
        var start = bobble.At(0);
        var rest = bobble.At(bobble.T1 - bobble.T0);
        Assert.Equal(GloveReach, Dist(start.X, start.Y, fp.X, fp.Y), 3);
        Assert.True(Dist(rest.X, rest.Y, fp.X, fp.Y) >= GloveReach + HandleBobblePlanner.PopM - 1e-6, "グラブから外へ跳ねる");
        // 送球はそのまま（判定の時刻を動かさない）
        Assert.Same(play.Ball[2], after.Ball[3]);
    }

    /// <summary>拾う瞬間に球を手へ引き寄せない（監督指摘「残像付きで吸い込まれる」）。</summary>
    [Theory]
    [InlineData(4.40, 0.9)]   // 歩く時間がある
    [InlineData(4.20, 1.6)]   // 弾いた芝居で時間を使い切る
    public void FielderStandsWithinPickupReachOfTheBall(double releaseT, double recover)
    {
        var play = Grounder(2.13, releaseT);
        var after = HandleBobblePlanner.ApplyFumble(play, HandleBobblePlanner.Plan(play), recover, GloveReach, PickupReach);
        var seg = after.Ball[2];
        var rest = seg.At(seg.T1 - seg.T0);
        var at = PlaybackEvaluator.FielderAt(after, FieldPosition.ThirdBase, seg.T1).Pos;
        Assert.Equal(PickupReach, Dist(rest.X, rest.Y, at.X, at.Y), 3);
    }

    [Fact]
    public void FielderWalksFurtherWhenThereIsTime()
    {
        var shortPlay = Grounder(2.13, 4.20);
        var longPlay = Grounder(2.13, 5.20);
        double Walked(PlaybackPlay p)
        {
            var after = HandleBobblePlanner.ApplyFumble(p, HandleBobblePlanner.Plan(p), 0.9, GloveReach, PickupReach);
            var a = PlaybackEvaluator.FielderAt(after, FieldPosition.ThirdBase, 2.13).Pos;
            var z = PlaybackEvaluator.FielderAt(after, FieldPosition.ThirdBase, after.Ball[2].T1).Pos;
            return Dist(a.X, a.Y, z.X, z.Y);
        }
        Assert.True(Walked(longPlay) > Walked(shortPlay) + 0.5);
    }

    [Fact]
    public void NoTimeToWalk_StillTakesOneUnhurriedStep()
    {
        var play = Grounder(2.13, 4.20);
        var after = HandleBobblePlanner.ApplyFumble(play, HandleBobblePlanner.Plan(play), recoverSeconds: 1.6, GloveReach, PickupReach);
        var leg = after.Moves[FieldPosition.ThirdBase].Single();
        Assert.True(leg.T0 >= 2.13 + 0.3 - 1e-9, "弾いた直後は動かない");
        Assert.True(leg.T1 - leg.T0 >= HandleBobblePlanner.MinStepSeconds - 1e-9, "一歩に時間をかける");
        Assert.True(leg.T1 <= after.Ball[2].T1, "拾う前に寄り終わる");
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
