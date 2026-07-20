using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Pitching;

/// <summary>
/// コントロール由来の投球散布（設計書02 §1.2 の σ = 30 − C×0.22 [cm]）の検証。
/// </summary>
public sealed class ControlScatterTests
{
    [Theory]
    [InlineData(50, 0.19)]   // 30 − 50×0.22 = 19cm
    [InlineData(100, 0.08)]  // 30 − 100×0.22 = 8cm
    [InlineData(0, 0.30)]    // 切片そのまま 30cm
    public void ControlSigma_MatchesDesignFormula(int control, double expectedMeters)
    {
        var coeff = new PitchingCoefficients();
        Assert.Equal(expectedMeters, coeff.ControlSigmaMeters(control), 6);
    }

    [Fact]
    public void ControlSigma_ClampsToMinimum()
    {
        var coeff = new PitchingCoefficients(); // min 2cm
        // C=140 なら式上は 30 − 30.8 < 0 → 下限 2cm に張り付く
        Assert.Equal(0.02, coeff.ControlSigmaMeters(140), 6);
    }

    [Fact]
    public void HigherControl_ProducesTighterScatter()
    {
        var coeff = new PitchingCoefficients();
        Assert.True(coeff.ControlSigmaMeters(90) < coeff.ControlSigmaMeters(40));
    }

    [Fact]
    public void Scatter_EmpiricalStdDevMatchesSigma()
    {
        var coeff = new PitchingCoefficients();
        var rng = new Xoshiro256Random(2024);
        const int control = 50; // σ = 0.19m
        var expectedSigma = coeff.ControlSigmaMeters(control);

        const int n = 200_000;
        double sumX = 0, sumSqX = 0, sumY = 0, sumSqY = 0;
        for (var i = 0; i < n; i++)
        {
            var loc = ControlScatter.Sample(0.0, 1.0, control, coeff, rng);
            var dx = loc.X - 0.0;
            var dy = loc.Y - 1.0;
            sumX += dx; sumSqX += dx * dx;
            sumY += dy; sumSqY += dy * dy;
        }

        var stdX = Math.Sqrt(sumSqX / n - Math.Pow(sumX / n, 2));
        var stdY = Math.Sqrt(sumSqY / n - Math.Pow(sumY / n, 2));

        Assert.InRange(stdX, expectedSigma * 0.97, expectedSigma * 1.03);
        Assert.InRange(stdY, expectedSigma * 0.97, expectedSigma * 1.03);
        // 平均は狙い位置に一致（偏りなし）
        Assert.InRange(sumX / n, -0.01, 0.01);
        Assert.InRange(sumY / n + 1.0 - 1.0, -0.01, 0.01);
    }

    [Fact]
    public void Scatter_SameSeedIsReproducible()
    {
        var coeff = new PitchingCoefficients();
        var a = new Xoshiro256Random(7);
        var b = new Xoshiro256Random(7);

        for (var i = 0; i < 100; i++)
        {
            var la = ControlScatter.Sample(0.1, 1.2, 60, coeff, a);
            var lb = ControlScatter.Sample(0.1, 1.2, 60, coeff, b);
            Assert.Equal(la.X, lb.X);
            Assert.Equal(la.Y, lb.Y);
        }
    }
}
