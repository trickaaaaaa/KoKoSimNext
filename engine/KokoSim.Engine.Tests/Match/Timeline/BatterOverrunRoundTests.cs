using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 外野への単打で一塁に止まる打者走者が、一塁を回って（オーバーラン）返球が内野へ戻ったら一塁へ歩いて戻る
/// （issue #1765）。表示専用の追加レッグ＝一塁へ着いた時刻・判定は動かさない。
/// 内野ゴロの一塁駆け抜け（#443・<see cref="FirstBaseOverrunTests"/>）とは別の経路。
/// </summary>
public sealed class BatterOverrunRoundTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();
    private static readonly FieldingCoefficients Fielding = new();

    private static FieldingPlay Single(double landingZ, double batterToFirstSeconds = 4.2,
        BattedBallResult result = BattedBallResult.Single)
        => FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: landingZ, fieldedAtSeconds: 3.2,
            result: result, batterToFirstSeconds: batterToFirstSeconds);

    /// <summary>GameEngine と同じ順序: 打球 → 外野からの返球 → 打者走者の一塁回り。</summary>
    private static PlayTimeline Compose(FieldingPlay play, bool appendRound = true, bool withReturnThrow = true)
    {
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        if (withReturnThrow)
        {
            var to = TimelineBuilder.ResolveOutfieldReturnTarget(Array.Empty<RunnerMove>(), play.Result);
            tl = TimelineBuilder.AppendOutfieldReturnThrow(tl, play, Field, to.BaseName, 0.0, Fielding, Baserunning, to.RunnerOut);
        }
        return appendRound ? TimelineBuilder.AppendBatterOverrunRound(tl, play, Field) : tl;
    }

    private static List<RunnerLeg> BatterLegs(PlayTimeline tl) => tl.Runners.Where(r => r.Label == "打").OrderBy(r => r.T0).ToList();

    private static double Len(RunnerLeg l)
        => Math.Sqrt((l.To.X - l.From.X) * (l.To.X - l.From.X) + (l.To.Z - l.From.Z) * (l.To.Z - l.From.Z));

    [Fact]
    public void OutfieldSingle_RoundsFirstThenWalksBackToFirst()
    {
        var tl = Compose(Single(78.0));
        var legs = BatterLegs(tl);
        var first = Field.FirstBase;

        // 主到達 ＋ 弧3本 ＋ 戻り1本
        Assert.Equal(5, legs.Count);
        var primary = legs[0];
        var back = legs[^1];
        Assert.False(primary.WalkBack);
        Assert.True(back.WalkBack);
        Assert.Equal(first.X, back.To.X, 6);
        Assert.Equal(first.Z, back.To.Z, 6);
        Assert.Equal(1, legs.Count(l => l.WalkBack));

        // 弧は一塁から切れ目なくつながり（時刻・位置とも）、一塁から離れていく
        Assert.Equal(first.X, legs[1].From.X, 6);
        Assert.Equal(first.Z, legs[1].From.Z, 6);
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(legs[i - 1].T1, legs[i].T0, 9);
            Assert.Equal(legs[i - 1].To.X, legs[i].From.X, 9);
            Assert.Equal(legs[i - 1].To.Z, legs[i].From.Z, 9);
            Assert.True(legs[i].T1 > legs[i].T0);
        }

        // 戻りは弧の終点から始まり、止まってから（間を置いて）歩き出す
        Assert.Equal(legs[3].To.X, back.From.X, 9);
        Assert.Equal(legs[3].To.Z, back.From.Z, 9);
        Assert.True(back.T0 > legs[3].T1 + 0.3);
        Assert.True(back.T1 <= tl.Duration);
    }

    [Fact]
    public void RoundLegs_DoNotMoveTheFirstBaseArrivalTime()
    {
        var play = Single(78.0);
        var without = BatterLegs(Compose(play, appendRound: false));
        var with = BatterLegs(Compose(play));
        Assert.Single(without);
        Assert.Equal(without[0].T0, with[0].T0, 9);
        Assert.Equal(without[0].T1, with[0].T1, 9);
        Assert.Equal(0.85 + play.BatterToFirstSeconds, with[0].T1, 9);
        Assert.False(with[0].OutAtEnd);
    }

    [Fact]
    public void RoundLegs_BulgeTowardSecondBase_AndDecelerateToAStop()
    {
        var legs = BatterLegs(Compose(Single(78.0)));
        var first = Field.FirstBase;
        var second = Field.SecondBase;
        var end = legs[3].To;

        // 弧の終点は一塁より二塁寄り（一塁の先で内野側へ回り込む）で、4〜8m離れる
        var away = Math.Sqrt((end.X - first.X) * (end.X - first.X) + (end.Z - first.Z) * (end.Z - first.Z));
        Assert.InRange(away, 2.5, 8.0);
        var dFirst = away;
        var dSecond = Math.Sqrt((end.X - second.X) * (end.X - second.X) + (end.Z - second.Z) * (end.Z - second.Z));
        Assert.True(dSecond < Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z)),
            $"二塁へ向かっていない（一塁から{dFirst:F1}m・二塁まで{dSecond:F1}m）");

        // 等減速: 速さは弧の間に単調に落ち、走り込みより遅い
        var approach = Len(legs[0]) / (legs[0].T1 - legs[0].T0);
        var v = legs.Skip(1).Take(3).Select(l => Len(l) / (l.T1 - l.T0)).ToList();
        Assert.True(v[0] < approach);
        Assert.True(v[0] > v[1] && v[1] > v[2], $"減速していない: {string.Join(", ", v.Select(x => x.ToString("F2")))}");

        // 戻りは歩き（走り込みの半分以下）
        var back = legs[^1];
        Assert.True(Len(back) / (back.T1 - back.T0) < 2.0);
    }

    [Fact]
    public void DeeperAndFasterHits_RoundWiderInTheRange4To8m()
    {
        static double Away(FieldingPlay p)
        {
            var legs = BatterLegs(Compose(p));
            var f = Field.FirstBase;
            var e = legs[3].To;
            return Math.Sqrt((e.X - f.X) * (e.X - f.X) + (e.Z - f.Z) * (e.Z - f.Z));
        }
        var shallowSlow = Away(Single(50.0, batterToFirstSeconds: 4.8));
        var deepFast = Away(Single(95.0, batterToFirstSeconds: 3.4));
        Assert.True(deepFast > shallowSlow, $"深く速い当たりの方が大きく回るはず: {deepFast:F2} vs {shallowSlow:F2}");
    }

    [Fact]
    public void WalkBack_StartsWhenTheReturnThrowReachesTheInfield()
    {
        var tl = Compose(Single(78.0));
        var legs = BatterLegs(tl);
        var stop = legs[3].T1;
        var firstThrow = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).OrderBy(b => b.T0).First();
        var back = legs[^1];
        // 返球が内野へ着いた時刻（止まる前なら止まった後・遅すぎれば上限）
        Assert.Equal(Math.Clamp(firstThrow.T1, stop + 0.4, stop + 3.0), back.T0, 9);
    }

    [Fact]
    public void WithoutAReturnThrow_WalksBackAfterAShortPause()
    {
        var tl = Compose(Single(78.0), withReturnThrow: false);
        var legs = BatterLegs(tl);
        Assert.Equal(5, legs.Count);
        Assert.Equal(legs[3].T1 + 1.0, legs[^1].T0, 9);
    }

    [Theory]
    [InlineData(BattedBallResult.Double)]
    [InlineData(BattedBallResult.Triple)]
    [InlineData(BattedBallResult.Error)]
    public void NonSingles_GetNoRoundLegs(BattedBallResult result)
    {
        var tl = Compose(Single(78.0, result: result));
        Assert.DoesNotContain(tl.Runners, l => l.WalkBack);
        Assert.Single(BatterLegs(tl));
    }

    [Fact]
    public void InfieldGrounder_KeepsTheStraightRunThrough_NotTheRound()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: BattedBallResult.Single);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        var after = TimelineBuilder.AppendBatterOverrunRound(tl, play, Field);
        Assert.Same(tl, after);
        Assert.Equal(2, BatterLegs(after).Count);
    }

    [Fact]
    public void BatterWhoContinuesPastFirst_GetsNoRoundLegs()
    {
        // 一塁を回って続きを走る打者（失策の進塁など）の「打」レッグは一塁で終わらない＝足さない
        var play = Single(78.0);
        var tl = Compose(play, appendRound: false);
        var second = Field.SecondBase;
        var legs = new List<RunnerLeg>(tl.Runners)
        {
            new()
            {
                Label = "打", T0 = tl.Runners[0].T1, T1 = tl.Runners[0].T1 + 3.0,
                From = new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z), To = new TimelinePoint(second.X, second.Z),
            },
        };
        var withContinuation = tl with { Runners = legs };
        Assert.Same(withContinuation, TimelineBuilder.AppendBatterOverrunRound(withContinuation, play, Field));
    }

    [Fact]
    public void IsDeterministic()
    {
        var a = BatterLegs(Compose(Single(78.0)));
        var b = BatterLegs(Compose(Single(78.0)));
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].T0, b[i].T0, 9);
            Assert.Equal(a[i].T1, b[i].T1, 9);
            Assert.Equal(a[i].To.X, b[i].To.X, 9);
            Assert.Equal(a[i].To.Z, b[i].To.Z, 9);
        }
    }
}
