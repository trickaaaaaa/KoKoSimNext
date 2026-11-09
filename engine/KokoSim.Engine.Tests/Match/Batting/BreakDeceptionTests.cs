using KokoSim.Engine.Match.Batting;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>誘発変化量の欺瞞効果への実球速割引（issue #837・設計書21 §3.5）の性質。</summary>
public sealed class BreakDeceptionTests
{
    private static readonly BattingCoefficients Coeff = new();

    [Fact]
    public void AtOrAboveReference_IsFullStrength()
    {
        Assert.Equal(1.0, ContactModel.BreakDeceptionFactor(Coeff.BreakDeceptionRefKmh, Coeff), 12);
        Assert.Equal(1.0, ContactModel.BreakDeceptionFactor(150.0, Coeff), 12);
    }

    [Fact]
    public void BelowReference_FollowsPowerOfVelocityRatio()
    {
        var expected = System.Math.Pow(90.0 / Coeff.BreakDeceptionRefKmh, Coeff.BreakDeceptionExponent);
        Assert.Equal(expected, ContactModel.BreakDeceptionFactor(90.0, Coeff), 12);
    }

    [Fact]
    public void IsMonotoneNonDecreasingAcrossPitchSpeeds()
    {
        // 変化球の緩急込みで実球速が取りうる 70〜155km/h 全域で、遅いほど割引が強い（逆転しない）。
        var prev = ContactModel.BreakDeceptionFactor(70.0, Coeff);
        for (var kmh = 71.0; kmh <= 155.0; kmh += 1.0)
        {
            var f = ContactModel.BreakDeceptionFactor(kmh, Coeff);
            Assert.True(f >= prev, $"{kmh}km/h で割引が逆転（{f} < {prev}）");
            Assert.InRange(f, 0.0, 1.0);
            prev = f;
        }
    }

    [Fact]
    public void ExponentZero_DisablesDiscount()
    {
        var off = Coeff with { BreakDeceptionExponent = 0.0 };
        Assert.Equal(1.0, ContactModel.BreakDeceptionFactor(80.0, off), 12);
    }
}
