using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// 球速 Lv→km/h の変換定数（<see cref="PitcherAttributes"/> の const が正典・YAML には載せない）を固定する（issue #1213）。
/// 切片・3区間の傾き・膝の位置は、1点ずつ既知の km/h と突き合わせて縛る。
/// </summary>
public sealed class PitcherVelocityMappingTests
{
    [Theory]
    [InlineData(0, 108.5)]      // 切片
    [InlineData(50, 132.0)]     // 切片 + 50×0.47
    [InlineData(90, 146.4)]     // 132.0 + 40×0.36
    [InlineData(100, 158.0)]    // 146.4 + 10×1.16
    public void VelocityKmh_MatchesKnownKneePoints(double level, double expectedKmh)
        => Assert.Equal(expectedKmh, PitcherAttributes.VelocityKmhFromPhysicalLevel(level), 9);

    [Fact]
    public void VelocityKmh_SlopesAreFixedPerSegment()
    {
        Assert.Equal(0.47, PitcherAttributes.VelocityKmhFromPhysicalLevel(21) - PitcherAttributes.VelocityKmhFromPhysicalLevel(20), 9);
        Assert.Equal(PitcherAttributes.VelocitySlopeAboveKneeKmhPerLevel,
            PitcherAttributes.VelocityKmhFromPhysicalLevel(71) - PitcherAttributes.VelocityKmhFromPhysicalLevel(70), 9);
        Assert.Equal(PitcherAttributes.VelocitySlopeAboveTopKneeKmhPerLevel,
            PitcherAttributes.VelocityKmhFromPhysicalLevel(96) - PitcherAttributes.VelocityKmhFromPhysicalLevel(95), 9);
        Assert.Equal(0.36, PitcherAttributes.VelocitySlopeAboveKneeKmhPerLevel, 9);
        Assert.Equal(1.16, PitcherAttributes.VelocitySlopeAboveTopKneeKmhPerLevel, 9);
        Assert.Equal(50, PitcherAttributes.VelocityKneeLevel);
        Assert.Equal(90, PitcherAttributes.VelocityTopKneeLevel);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(73)]
    [InlineData(90)]
    [InlineData(100)]
    public void VelocityKmh_RoundTripsThroughInverse(int level)
        => Assert.Equal(level,
            PitcherAttributes.PhysicalLevelFromVelocityKmhExact(PitcherAttributes.VelocityKmhFromPhysicalLevel(level)), 6);
}
