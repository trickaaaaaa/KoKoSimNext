using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 送球1本ごとの逸れの共通モデル（issue #841）。完了条件の物理テスト:
/// 逸れ0ならタッチ秒は従来どおり／逸れが大きいほど単調に伸びる／一定以上で悪送球。
/// </summary>
public sealed class ThrowScatterModelTests
{
    private const double LateralSecPerCm = 0.0015;
    private const double HighSecPerCm = 0.0010;
    private const double ShortHopSecPerCm = 0.0030;
    private const double ReliefSlope = 0.004;
    private const double ReliefMinFactor = 0.60;

    [Fact]
    public void RecoverySeconds_ZeroScatter_IsZero()
    {
        var zero = new ThrowScatterModel.Scatter(0.0, 0.0);
        var seconds = ThrowScatterModel.RecoverySeconds(
            zero, receiverCatching: 50, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);
        Assert.Equal(0.0, seconds);
    }

    [Theory]
    [InlineData(10.0, 0.0)]
    [InlineData(0.0, 10.0)]
    [InlineData(0.0, -10.0)]
    [InlineData(10.0, -10.0)]
    public void RecoverySeconds_LargerScatter_AddsMoreSeconds_Monotonically(double lateralCm, double verticalCm)
    {
        var small = new ThrowScatterModel.Scatter(lateralCm, verticalCm);
        var big = new ThrowScatterModel.Scatter(lateralCm * 3, verticalCm * 3);

        var smallSeconds = ThrowScatterModel.RecoverySeconds(
            small, receiverCatching: 50, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);
        var bigSeconds = ThrowScatterModel.RecoverySeconds(
            big, receiverCatching: 50, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);

        Assert.True(bigSeconds > smallSeconds, $"逸れが3倍なのに遅れが伸びていない（{smallSeconds:F4}→{bigSeconds:F4}）");
    }

    [Fact]
    public void RecoverySeconds_ShortHop_IsWorseThanSameMagnitudeHighThrow()
    {
        // 低い（ショートバウンド側）は高い側より重い（issue #841「捕り直す分」）。
        var high = new ThrowScatterModel.Scatter(0.0, 20.0);
        var low = new ThrowScatterModel.Scatter(0.0, -20.0);
        var highSeconds = ThrowScatterModel.RecoverySeconds(
            high, receiverCatching: 50, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);
        var lowSeconds = ThrowScatterModel.RecoverySeconds(
            low, receiverCatching: 50, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);
        Assert.True(lowSeconds > highSeconds, "ショートバウンド側が高い側より軽い遅れになっている");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(80)]
    public void RecoverySeconds_HigherReceiverCatching_ShrinksDelay(int catching)
    {
        var scatter = new ThrowScatterModel.Scatter(15.0, -8.0);
        var baseline = ThrowScatterModel.RecoverySeconds(
            scatter, receiverCatching: 50, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);
        var shifted = ThrowScatterModel.RecoverySeconds(
            scatter, receiverCatching: catching, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);

        if (catching > 50) Assert.True(shifted < baseline, "受け手の捕球が高いのに遅れが縮んでいない");
        else Assert.True(shifted > baseline, "受け手の捕球が低いのに遅れが伸びていない");
    }

    [Fact]
    public void IsWild_BelowThreshold_False_AboveThreshold_True()
    {
        const double threshold = 70.0;
        Assert.False(ThrowScatterModel.IsWild(new ThrowScatterModel.Scatter(30.0, 20.0), threshold)); // radial≈36
        Assert.True(ThrowScatterModel.IsWild(new ThrowScatterModel.Scatter(60.0, 40.0), threshold));  // radial≈72
    }

    [Fact]
    public void ExpectedRecoverySeconds_MatchesSampleAverage()
    {
        // サンプリングせず解析解で出した期待値が、実際に大量サンプリングした平均と一致する
        // （ResolveVerdict が「期待値からの差分」で平均不変を保証する前提）。
        const double sigma = 22.0;
        var expected = ThrowScatterModel.ExpectedRecoverySeconds(
            sigma, receiverCatching: 50, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);

        var rng = new Xoshiro256Random(99);
        double sum = 0;
        const int trials = 200_000;
        for (var i = 0; i < trials; i++)
        {
            var s = ThrowScatterModel.Sample(sigma, rng);
            sum += ThrowScatterModel.RecoverySeconds(
                s, receiverCatching: 50, LateralSecPerCm, HighSecPerCm, ShortHopSecPerCm, ReliefSlope, ReliefMinFactor);
        }
        var sampledMean = sum / trials;

        Assert.True(System.Math.Abs(sampledMean - expected) < 0.0015,
            $"解析解 {expected:F4} とサンプル平均 {sampledMean:F4} が食い違う");
    }
}
