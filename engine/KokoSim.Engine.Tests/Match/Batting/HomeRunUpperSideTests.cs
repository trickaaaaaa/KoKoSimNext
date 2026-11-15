using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>
/// 打球の上側の削り（issue #976）: 初速係数の膝（<see cref="BattingCoefficients.ExitVeloSoftKnee"/>）と
/// 角度の上側の倍率（<see cref="BattingCoefficients.LaunchAngleUpperSigmaFactor"/>）。
/// </summary>
public sealed class HomeRunUpperSideTests
{
    private static readonly PitchPlan Plan = new() { Type = PitchType.Fastball, AimX = 0.0, AimY = 0.5, VelocityKmh = 135.0, Stuff = 0.0 };
    private static readonly PitchTrajectoryFeatures Features = new(0.0, 0.0, 0.45);

    [Fact]
    public void SoftenVeloFactor_IsIdentityBelowTheKnee_AndCompressesAboveIt()
    {
        var c = new BattingCoefficients { ExitVeloSoftKnee = 0.87, ExitVeloSoftSlope = 0.25 };
        Assert.Equal(0.80, c.SoftenVeloFactor(0.80), 12);
        Assert.Equal(0.87, c.SoftenVeloFactor(0.87), 12);
        Assert.Equal(0.87 + 0.13 * 0.25, c.SoftenVeloFactor(1.00), 12);
        // 膝0以下＝無効
        Assert.Equal(1.00, new BattingCoefficients { ExitVeloSoftKnee = 0.0 }.SoftenVeloFactor(1.00), 12);
    }

    /// <summary>膝は満初速に対する比＝パワーの差（km/h）は膝の上でも残る（絶対値の膝だとパワーの価値が潰れた）。</summary>
    [Fact]
    public void Knee_IsRelative_SoPowerStillAddsExitVelocity()
    {
        var c = new BattingCoefficients();
        double MaxExit(int power) => (c.ExitVeloInterceptKmh + power * c.ExitVeloPerPower) * c.SoftenVeloFactor(1.0);
        Assert.True(MaxExit(80) - MaxExit(50) > 10.0, $"P80 {MaxExit(80):F1} vs P50 {MaxExit(50):F1}");
    }

    /// <summary>角度の上側の倍率は、平均より上へ振れた打球だけを低くする。乱数の消費は変えない。</summary>
    [Fact]
    public void UpperSigmaFactor_LowersOnlyUpwardDeviations_AndKeepsTheRandomStream()
    {
        var sym = new BattingCoefficients { LaunchAngleUpperSigmaFactor = 1.0, PopupShare = 0.0 };
        var tight = sym with { LaunchAngleUpperSigmaFactor = 0.5 };
        int lowered = 0, same = 0;
        for (ulong seed = 1; seed <= 2000; seed++)
        {
            var ra = new Xoshiro256Random(seed);
            var rb = new Xoshiro256Random(seed);
            var a = ContactModel.Resolve(BatterAttributes.LeagueAverage, Plan, Features, true, sym, ra);
            var b = ContactModel.Resolve(BatterAttributes.LeagueAverage, Plan, Features, true, tight, rb);
            Assert.Equal(ra.NextUInt64(), rb.NextUInt64());
            if (a.Ball is null) { Assert.Null(b.Ball); continue; }
            Assert.True(b.Ball!.LaunchAngleDeg <= a.Ball.LaunchAngleDeg + 1e-9);
            if (b.Ball.LaunchAngleDeg < a.Ball.LaunchAngleDeg - 1e-9) lowered++;
            else same++;
        }
        Assert.True(lowered > 0 && same > 0, $"上側だけが下がるはず（下がった {lowered} / 同じ {same}）");
    }
}
