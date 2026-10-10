using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 本塁打の判定は「フェンス上での高さ ≥ フェンス高」（issue homerun-rate-explodes-at-high-power, 2026-08-18）。
///
/// <para>旧判定「着地距離 ≥ フェンス距離 かつ 頂点 ≥ フェンス高」は、柵の 0〜6m 先へ落ちる低い当たり
/// （フェンス上で 4m に届かない）まで本塁打にしていた（実測: 本塁打の 41%）。物理は積分器が
/// フェンス弧の横切りを観測するだけ＝着地・頂点・滞空時間は引数の有無で変わらない。</para>
/// </summary>
public sealed class HomeRunFenceHeightTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldingCoefficients C = new();

    private static BattedBall Ball(double kmh, double angle, double bearing = 0)
        => new() { ExitVelocityMps = kmh / 3.6, LaunchAngleDeg = angle, BearingDeg = bearing };

    private static FieldingPlay Resolve(BattedBall ball, ulong seed = 1)
        => FieldingResolver.ResolveDetailed(ball, Field, Aero, BatterAttributes.LeagueAverage, Field.StandardAlignment(), C, new Xoshiro256Random(seed));

    [Fact]
    public void Integrator_ReportsFenceCrossing_WithoutChangingLanding()
    {
        var b = Ball(160, 28);
        var plain = BallisticIntegrator.IntegrateToGround(b.ContactPoint(), b.InitialVelocity(), b.SpinVector(), Aero);
        var tracked = BallisticIntegrator.IntegrateToGround(b.ContactPoint(), b.InitialVelocity(), b.SpinVector(), Aero, fenceDistanceM: 118.0);
        Assert.False(plain.CrossedFence);
        Assert.True(tracked.CrossedFence);
        Assert.Equal(plain.HangTimeSeconds, tracked.HangTimeSeconds);
        Assert.Equal(plain.LandingPosition.Z, tracked.LandingPosition.Z);
        Assert.Equal(plain.ApexHeightM, tracked.ApexHeightM);
        // 横切り点は弧の上（水平距離＝118）で、着地より前。
        var r = Math.Sqrt(tracked.FenceCrossPosition.X * tracked.FenceCrossPosition.X + tracked.FenceCrossPosition.Z * tracked.FenceCrossPosition.Z);
        Assert.Equal(118.0, r, 2);
        Assert.True(tracked.FenceCrossSeconds < tracked.HangTimeSeconds);
        Assert.True(tracked.FenceCrossPosition.Y > 0);
    }

    [Fact]
    public void Integrator_DoesNotReportCrossing_WhenTheBallLandsShort()
    {
        var b = Ball(140, 28); // 中堅へ 102m ＝ 118m の柵に届かない
        var g = BallisticIntegrator.IntegrateToGround(b.ContactPoint(), b.InitialVelocity(), b.SpinVector(), Aero, fenceDistanceM: 118.0);
        Assert.False(g.CrossedFence);
    }

    [Fact]
    public void BallClearingTheFenceHeight_IsAHomeRun()
    {
        // 165 km/h・30° 中堅方向 → 着地 127m・柵（118m）上で十分な高さ。
        var play = Resolve(Ball(165, 30));
        Assert.Equal(BattedBallResult.HomeRun, play.Result);
        Assert.True(play.RangeM > Field.CenterFenceM);
    }

    [Fact]
    public void LowLinerLandingJustBeyondTheFenceLine_IsNotAHomeRun_ItHitsTheFence()
    {
        // 頂点はフェンス高（4m）を越えるが、柵の位置では 4m に届かず柵の 1〜3m 先へ落ちる低い当たり。
        // 旧判定では本塁打、正しくはフェンス直撃＝着地点が柵の弧上に打ち切られ、本塁打にならない。
        BattedBall? found = null;
        for (var kmh = 150.0; kmh <= 175.0 && found is null; kmh += 0.5)
            for (var la = 12.0; la <= 22.0; la += 0.5)
            {
                var b = Ball(kmh, la);
                var g = BallisticIntegrator.IntegrateToGround(b.ContactPoint(), b.InitialVelocity(), b.SpinVector(), Aero, fenceDistanceM: Field.CenterFenceM);
                var range = Math.Sqrt(g.LandingPosition.X * g.LandingPosition.X + g.LandingPosition.Z * g.LandingPosition.Z);
                if (g.CrossedFence && g.FenceCrossPosition.Y < Field.FenceHeightM && g.ApexHeightM >= Field.FenceHeightM
                    && range >= Field.CenterFenceM + 1.0 && range <= Field.CenterFenceM + 3.0)
                { found = b; break; }
            }
        Assert.NotNull(found);

        var play = Resolve(found!);
        Assert.NotEqual(BattedBallResult.HomeRun, play.Result);
        // 着地＝フェンス弧上（打ち切り）。転がりはフェンス到達（距離0）→ 跳ね返り待ち。
        Assert.Equal(Field.CenterFenceM, play.RangeM, 2);
        Assert.Equal(Field.CenterFenceM, play.LandingZ, 2);
        Assert.True(play.Result is BattedBallResult.Double or BattedBallResult.Triple or BattedBallResult.Single
            or BattedBallResult.Out or BattedBallResult.Error, $"直撃球の結果 {play.Result}");
        if (play.Result is BattedBallResult.Double or BattedBallResult.Triple or BattedBallResult.Single)
            Assert.True(play.ReachedFence, "直撃なのに ReachedFence が立たない");
    }

    [Fact]
    public void FenceHeightMatters_TheSameBallIsAHomeRunOverALowerFence()
    {
        // 柵上の高さが 2.5〜3.9m の球: 4m 柵では直撃、2m 柵では本塁打。
        BattedBall? found = null;
        double crossY = 0;
        for (var kmh = 150.0; kmh <= 175.0 && found is null; kmh += 0.5)
            for (var la = 12.0; la <= 25.0; la += 0.5)
            {
                var b = Ball(kmh, la);
                var g = BallisticIntegrator.IntegrateToGround(b.ContactPoint(), b.InitialVelocity(), b.SpinVector(), Aero, fenceDistanceM: Field.CenterFenceM);
                if (g.CrossedFence && g.FenceCrossPosition.Y is >= 2.5 and < 3.9) { found = b; crossY = g.FenceCrossPosition.Y; break; }
            }
        Assert.NotNull(found);
        Assert.NotEqual(BattedBallResult.HomeRun, Resolve(found!).Result);

        var low = Field with { FenceHeightM = 2.0 };
        var play = FieldingResolver.ResolveDetailed(found!, low, Aero, BatterAttributes.LeagueAverage, low.StandardAlignment(), C, new Xoshiro256Random(1));
        Assert.Equal(BattedBallResult.HomeRun, play.Result);
        Assert.True(crossY >= 2.0);
    }
}
