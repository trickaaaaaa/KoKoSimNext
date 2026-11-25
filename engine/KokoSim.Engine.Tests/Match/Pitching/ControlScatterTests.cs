using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Pitching;

/// <summary>
/// コントロール由来の投球散布（設計書02 §1.2 の σ = 30 − C×0.22 [cm]、issue #1589 でControl&lt;50側に
/// 3次の上乗せを足して非対称化）の検証。
/// </summary>
public sealed class ControlScatterTests
{
    [Theory]
    [InlineData(50, 0.19)]   // 30 − 50×0.22 = 19cm（差0なので3次項も0）
    [InlineData(100, 0.08)]  // 30 − 100×0.22 = 8cm（Control≥50は3次項の対象外）
    public void ControlSigma_MatchesDesignFormula_AtAndAboveFifty(int control, double expectedMeters)
    {
        var coeff = new PitchingCoefficients();
        Assert.Equal(expectedMeters, coeff.ControlSigmaMeters(control), 6);
    }

    [Fact]
    public void ControlSigma_BelowFifty_AddsCubicTerm_IssueUseCase1589()
    {
        // issue #1589: Control<50側だけ (50−Control)^3×cubic_below の3次項が線形部分の上に乗る
        // （制球Gの投手の実行散布を広げ、与四球率を上げる狙い。母集団の大半を占めるControl40台は
        // 差が小さく3次項がほぼ効かない＝線形・2次より極端な低制球だけ選んで広がる度合いが強い）。
        var coeff = new PitchingCoefficients();
        var sigma50 = coeff.ControlSigmaMeters(50);
        var sigma20 = coeff.ControlSigmaMeters(20);
        Assert.True(sigma20 > sigma50, $"Control20の散布がControl50以下: {sigma20:F4} <= {sigma50:F4}");
        var expectedCm = (coeff.ControlSigmaInterceptCm - coeff.ControlSigmaSlopeAbovePerPoint * 20)
            + coeff.ControlSigmaBelowCubicCmPerPointCubed * 30 * 30 * 30;
        Assert.Equal(Math.Max(expectedCm, coeff.ControlSigmaMinCm) / 100.0, sigma20, 6);
    }

    [Fact]
    public void ControlSigma_IsSmooth_AtTheControlFiftyPivot()
    {
        // 50の直前・直後で急に跳ねない（3次項とその導関数がControl=50で0になるので、線形部分へ
        // 切れ目なく滑らかに繋がる）こと。
        var coeff = new PitchingCoefficients();
        var just49 = coeff.ControlSigmaMeters(49);
        var at50 = coeff.ControlSigmaMeters(50);
        var just51 = coeff.ControlSigmaMeters(51);
        Assert.InRange(just49, at50 - 0.01, at50 + 0.01);
        Assert.InRange(just51, at50 - 0.01, at50 + 0.01);
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
