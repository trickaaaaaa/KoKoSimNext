using KokoSim.Engine.Match.Batting;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>投打差の soft-cap（issue matchup-gap-saturation-k-rate）の性質。</summary>
public sealed class WhiffMatchupCapTests
{
    [Fact]
    public void SaturateMatchup_IsLinearNearZero_AndSaturatesAtCap()
    {
        const double cap = 1.5;
        Assert.Equal(0.0, ContactModel.SaturateMatchup(0.0, cap), 12);
        // 互角近傍（|d| ≪ cap）は線形とほぼ一致（1%以内）。
        Assert.InRange(ContactModel.SaturateMatchup(0.15, cap) / 0.15, 0.99, 1.0);
        // 大差は cap で頭打ち・奇関数。
        Assert.InRange(ContactModel.SaturateMatchup(5.0, cap), 0.99 * cap, cap);
        Assert.Equal(-ContactModel.SaturateMatchup(2.4, cap), ContactModel.SaturateMatchup(-2.4, cap), 12);
        // 単調増加。
        Assert.True(ContactModel.SaturateMatchup(1.0, cap) < ContactModel.SaturateMatchup(2.0, cap));
    }

    [Fact]
    public void SaturateMatchup_CapZeroOrNegative_IsIdentity()
    {
        Assert.Equal(2.4, ContactModel.SaturateMatchup(2.4, 0.0), 12);
        Assert.Equal(-2.4, ContactModel.SaturateMatchup(-2.4, -1.0), 12);
    }
}
