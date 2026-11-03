using KokoSim.Engine.Match.Batting;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>芯を外した当たりの初速カーブ（issue #759）。</summary>
public class MishitVelocityTests
{
    [Fact]
    public void AboveThreshold_IsTheOriginalLine()
    {
        var c = new BattingCoefficients();
        foreach (var q in new[] { c.MishitQualityThreshold, 0.63, 1.05 })
            Assert.Equal(c.MinQualityVeloFactor + (1 - c.MinQualityVeloFactor) * q, ContactModel.VeloFactor(q, c), 9);
    }

    [Fact]
    public void WorstContact_DropsToTheFloor_AndIsContinuousAtThreshold()
    {
        var c = new BattingCoefficients();
        Assert.Equal(c.MishitVeloFactorFloor, ContactModel.VeloFactor(ContactModel.QualityMin, c), 9);
        var q0 = c.MishitQualityThreshold;
        Assert.Equal(ContactModel.VeloFactor(q0, c), ContactModel.VeloFactor(q0 - 1e-9, c), 6);
        // 品質に対して単調
        var prev = 0.0;
        for (var q = ContactModel.QualityMin; q <= 1.05; q += 0.01)
        {
            var f = ContactModel.VeloFactor(q, c);
            Assert.True(f >= prev);
            prev = f;
        }
    }

    [Fact]
    public void WorstContact_IsSlowerThan60Kmh_ForAnAverageHitter()
    {
        var c = new BattingCoefficients();
        var maxKmh = c.ExitVeloInterceptKmh + 50 * c.ExitVeloPerPower;
        Assert.True(maxKmh * ContactModel.VeloFactor(ContactModel.QualityMin, c) < 60.0);
    }

    [Fact]
    public void DisabledThreshold_IsIdentityWithTheLine()
    {
        var c = new BattingCoefficients { MishitQualityThreshold = 0.0 };
        Assert.Equal(c.MinQualityVeloFactor + (1 - c.MinQualityVeloFactor) * 0.05, ContactModel.VeloFactor(0.05, c), 9);
    }
}
