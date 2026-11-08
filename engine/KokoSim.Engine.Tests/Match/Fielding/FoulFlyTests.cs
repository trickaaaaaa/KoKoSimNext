using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// ファウルフライのアウト（issue #830）。ファウルグラウンド（バックネット・ライン外の帯）に落ちる飛球へ、
/// 捕手を含む野手が届けばアウト。届かない・スタンドに入る・低い打球はただのファウル。
/// </summary>
public sealed class FoulFlyTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly FieldingCoefficients Coeff = new();

    private static Vector3D At(double bearingDeg, double r)
    {
        var b = bearingDeg * Math.PI / 180.0;
        return new Vector3D(r * Math.Sin(b), 0, r * Math.Cos(b));
    }

    [Theory]
    [InlineData(0.0, 30.0, false)]      // フェア
    [InlineData(44.0, 60.0, false)]     // フェア（ライン際）
    [InlineData(180.0, 10.0, true)]     // 本塁の真後ろ・ネットの手前
    [InlineData(180.0, 20.0, false)]    // ネットの向こう
    [InlineData(60.0, 30.0, true)]      // 一塁側・ラインから 7.8m
    [InlineData(80.0, 30.0, false)]     // 一塁側・ラインから 17m＝スタンド
    [InlineData(-55.0, 80.0, true)]     // 三塁側・ポール手前・ラインから 13.9m…の手前（境界の内側か外側かは幅次第）
    public void InFoulTerritory(double bearingDeg, double r, bool want)
    {
        var p = At(bearingDeg, r);
        if (bearingDeg == -55.0)
        {
            // ラインからの距離 = r·sin(10°) = 13.9m > 13m なのでスタンド
            Assert.False(Field.InFoulTerritory(p));
            return;
        }
        Assert.Equal(want, Field.InFoulTerritory(p));
    }

    private static FieldingPlay? Resolve(double launchDeg, double kmh, double bearingDeg)
        => FieldingResolver.ResolveFoulFly(
            new BattedBall { ExitVelocityMps = kmh / 3.6, LaunchAngleDeg = launchDeg, BearingDeg = bearingDeg },
            Field, new Aerodynamics(), Field.StandardAlignment(), Coeff, runnerTime: 4.3);

    /// <summary>本塁の後ろに高く上がった飛球は捕手が捕る。</summary>
    [Fact]
    public void PopBehindHome_CatcherCatchesIt()
    {
        var p = Resolve(82.0, 80.0, 175.0);
        Assert.NotNull(p);
        Assert.Equal(BattedBallResult.Out, p!.Result);
        Assert.Equal(FieldPosition.Catcher, p.FielderRole);
        Assert.True(p.FoulFly && p.IsFly);
        Assert.True(Field.InFoulTerritory(new Vector3D(p.LandingX, 0, p.LandingZ)));
        Assert.Equal(p.HangTimeSeconds, p.FieldedAtSeconds);
    }

    /// <summary>一塁側・三塁側のライン外へ上がった飛球は、それぞれ一塁手・三塁手が捕る。</summary>
    [Theory]
    [InlineData(58.0, FieldPosition.FirstBase)]
    [InlineData(-58.0, FieldPosition.ThirdBase)]
    public void PopOutsideTheLine_CornerInfielderCatchesIt(double bearingDeg, FieldPosition want)
    {
        var p = Resolve(62.0, 105.0, bearingDeg);
        Assert.NotNull(p);
        Assert.Equal(want, p!.FielderRole);
    }

    /// <summary>スタンドへ入る打球・低いライナー・誰も届かない打球はアウトにしない。</summary>
    [Theory]
    [InlineData(35.0, 150.0, 70.0)]    // スタンドへ飛び込む
    [InlineData(5.0, 120.0, 60.0)]     // 低いライナー（飛球ではない）
    [InlineData(40.0, 150.0, 180.0)]   // バックネットを越える
    public void NotCatchable_StaysFoul(double launchDeg, double kmh, double bearingDeg)
        => Assert.Null(Resolve(launchDeg, kmh, bearingDeg));

    /// <summary>前に飛んだ（方位角 ±45° 超）打球の経路でも、捕れればアウト。割合0なら必ずファウル（従来どおり）。</summary>
    [Fact]
    public void GeometricFoul_IsCaughtWhenReachable_AndOffWhenShareIsZero()
    {
        var ball = new BattedBall { ExitVelocityMps = 100 / 3.6, LaunchAngleDeg = 65.0, BearingDeg = 55.0 };
        var on = FieldingResolver.ResolveDetailed(ball, Field, new Aerodynamics(), BatterAttributes.LeagueAverage,
            Field.StandardAlignment(), Coeff, new Xoshiro256Random(1));
        Assert.Equal(BattedBallResult.Out, on.Result);
        Assert.True(on.FoulFly);

        var off = FieldingResolver.ResolveDetailed(ball, Field, new Aerodynamics(), BatterAttributes.LeagueAverage,
            Field.StandardAlignment(), Coeff with { FoulFlyShare = 0.0 }, new Xoshiro256Random(1));
        Assert.Equal(BattedBallResult.Foul, off.Result);
    }

    /// <summary>打ち上がったファウルの打球は同じ乱数なら同じ（決定論）。方位角は必ずファウル側。</summary>
    [Fact]
    public void FoulFlyBall_IsDeterministicAndFoul()
    {
        int made = 0;
        for (ulong seed = 1; seed <= 300; seed++)
        {
            var a = FieldingResolver.FoulFlyBall(Coeff, new Xoshiro256Random(seed));
            var b = FieldingResolver.FoulFlyBall(Coeff, new Xoshiro256Random(seed));
            Assert.Equal(a, b);
            if (a is null) continue;
            made++;
            Assert.True(Math.Abs(a.BearingDeg) > 45.0);
        }
        Assert.InRange(made, 40, 110);   // 割合 0.24 前後
    }

    /// <summary>ファウルフライのアウトの絵: 捕った野手が落下点へ行き、捕球の瞬間にアウトが出る。</summary>
    [Fact]
    public void Timeline_ShowsCatchInFoulTerritory()
    {
        var p = Resolve(82.0, 80.0, 175.0)!;
        var tl = TimelineBuilder.BuildBattedBall(p, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(p));
        var outCall = Assert.Single(tl.Judgements, j => j.Label == "打");
        Assert.Equal(JudgementKind.Out, outCall.Kind);
        Assert.Equal(TimelineBuilder.ContactAt + p.HangTimeSeconds, outCall.T, 3);
        var move = tl.Moves.Where(m => m.Role == FieldPosition.Catcher && m.Task == FielderTask.FieldBall).ToList();
        Assert.NotEmpty(move);
        Assert.True(Math.Abs(move[^1].To.X - p.LandingX) < 0.5 && Math.Abs(move[^1].To.Z - p.LandingZ) < 0.5,
            "捕手が落下点へ行っていない");
    }
}
