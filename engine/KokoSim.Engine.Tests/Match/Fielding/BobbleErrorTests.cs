using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// お手玉の失策（2026-11-03 監督報告「あれだけファンブルしたら内野安打じゃなくて普通にエラーでは？」）。
/// もたつきがお手玉級で、もたつきが無ければアウトだったセーフは失策（FieldingErrorKind.Bobble）。
/// </summary>
public class BobbleErrorTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly FieldingCoefficients Coeff = new() { ErrorBaseProb = 0, ThrowErrorBaseProb = 0 };

    private static FieldingPlay Resolve(ulong seed, FieldingCoefficients? coeff = null, int fielding = 50)
        => FieldingResolver.ResolveDetailed(
            // 三遊間の深い所への際どいゴロ（もたつき次第でアウトにもセーフにもなる）
            new BattedBall { ExitVelocityMps = 28.0, LaunchAngleDeg = 4.0, BearingDeg = -18.0 },
            Field, new Aerodynamics(), BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = fielding }),
            coeff ?? Coeff, new Xoshiro256Random(seed));

    private static FieldingPlay FirstBobble()
    {
        for (ulong s = 1; s < 5000; s++)
        {
            var p = Resolve(s * 7919);
            if (p.ErrorKind == FieldingErrorKind.Bobble) return p;
        }
        throw new Xunit.Sdk.XunitException("お手玉の失策が1件も出ない");
    }

    [Fact]
    public void Bobble_IsAnErrorOnTheFielderWhoCaughtIt_AndWouldHaveBeenAnOutWithoutTheJitter()
    {
        var p = FirstBobble();
        Assert.Equal(BattedBallResult.Error, p.Result);
        Assert.Equal(p.FielderRole, p.ErrorFielderRole);
        Assert.Equal(p.FieldedAtSeconds, p.ErrorAtSeconds);
        // セーフ（送球は走者より後に着く）
        Assert.True(p.ThrowArriveSeconds > p.BatterToFirstSeconds);
    }

    [Fact]
    public void Disabled_TurnsEveryBobbleBackIntoAnInfieldHit()
    {
        var off = Coeff with { BobbleErrorMinJitterSeconds = 0.0 };
        for (ulong s = 1; s < 2000; s++)
        {
            var on = Resolve(s * 7919);
            if (on.ErrorKind != FieldingErrorKind.Bobble) continue;
            var p = Resolve(s * 7919, off);
            Assert.Equal(BattedBallResult.Single, p.Result);
            Assert.Equal(on.ThrowArriveSeconds, p.ThrowArriveSeconds); // 秒は1つも動かない＝記録だけが変わる
        }
    }

    [Fact]
    public void Timeline_ThrowsToFirstLate_ShowsErrorAtTheCatch_AndPlaysTheFumble()
    {
        var p = FirstBobble();
        var tl = TimelineBuilder.BuildBattedBall(
            p, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(p));

        Assert.True(tl.BobbleError);
        Assert.False(tl.FumbledCatch);   // 前へこぼす捕球エラーの芝居ではない
        // 送球は受け手のいる一塁へ正しく届く（悪送球でも弾きロールでもない）
        var th = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        Assert.Equal("first", th.ToBase);
        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Roll && b.FromRole == p.FielderRole);
        // 「エラー」は捕った直後（弾いた瞬間）に出る
        var err = Assert.Single(tl.Judgements, j => j.Kind == JudgementKind.Error);
        Assert.Equal(TimelineBuilder.ContactAt + p.FieldedAtSeconds!.Value, err.T, 6);

        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        Assert.Equal(HandleBobbleKind.Fumble, HandleBobblePlanner.Plan(play).Kind);
    }

    // issue #1259: もたつきの頭打ちを守備倍率の前に掛けていたため、守備51以上では
    // しきい値（1.45）に届かず失策が0になり、その分が内野安打に化けていた（能力が逆向きに効く崖）。
    [Theory]
    [InlineData(51)]
    [InlineData(52)]
    [InlineData(55)]
    public void Bobble_StillHappensForFielderAboveFifty(int fielding)
    {
        var n = 0;
        for (ulong s = 1; s <= 20000; s++)
            if (Resolve(s * 7919, fielding: fielding).ErrorKind == FieldingErrorKind.Bobble) n++;
        Assert.True(n > 0, $"守備{fielding}でお手玉の失策が1件も出ない");
    }

    [Fact]
    public void BobbleRate_NeverRisesWithFielding_AcrossTheFiftyBoundary()
    {
        int Count(int fielding)
        {
            var n = 0;
            for (ulong s = 1; s <= 20000; s++)
                if (Resolve(s * 7919, fielding: fielding).ErrorKind == FieldingErrorKind.Bobble) n++;
            return n;
        }
        var counts = new[] { 40, 45, 49, 50, 51, 52, 55, 60 }.Select(Count).ToArray();
        for (var i = 1; i < counts.Length; i++)
            Assert.True(counts[i] <= counts[i - 1], $"お手玉の件数が守備で逆向き: {string.Join(",", counts)}");
        Assert.True(counts[3] > 0 && counts[4] > 0 && counts[5] > 0, $"守備50〜52で0件: {string.Join(",", counts)}");
    }
}
