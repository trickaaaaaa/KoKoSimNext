using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>issue #864: 成長イベントの球速の伸びは内部値でなく km/h で見せる。</summary>
public sealed class GrowthGainDisplayTests
{
    private static int Kmh(int level) => (int)System.Math.Round(PitcherAttributes.VelocityKmhFromLevel(level));

    private static DevelopingPlayer PlayerWithVelocity(int level)
    {
        var p = new DevelopingPlayer();
        p.SetLevel(AbilityKind.Velocity, level);
        return p;
    }

    [Theory]
    [InlineData(44, 4)]   // 下の区分（0.47 km/h per Lv）: 127→129
    [InlineData(64, 4)]   // 上の区分（0.36 km/h per Lv）: 136→137
    [InlineData(52, 4)]   // Lv50 をまたぐ: 131→133
    [InlineData(99, 4)]
    public void VelocityGain_IsTheDifferenceOfDisplayedKmh(int levelAfter, int amount)
    {
        Assert.Equal(Kmh(levelAfter) - Kmh(levelAfter - amount), GrowthGainDisplay.VelocityGainKmh(levelAfter, amount));
    }

    [Fact]
    public void VelocityGain_KnownValues()
    {
        Assert.Equal(2, GrowthGainDisplay.VelocityGainKmh(44, 4));
        Assert.Equal(1, GrowthGainDisplay.VelocityGainKmh(64, 4));
        Assert.Equal(2, GrowthGainDisplay.VelocityGainKmh(52, 4));
    }

    [Fact]
    public void Amount_Velocity_IsShownInKmh()
    {
        var p = PlayerWithVelocity(44);
        Assert.Equal("＋2km/h", GrowthGainDisplay.Amount(p, new AbilityGain(AbilityKind.Velocity, 4)));
    }

    [Fact]
    public void Amount_VelocityRoundingToZero_IsEmpty()
    {
        var p = PlayerWithVelocity(50);
        Assert.Equal(0, GrowthGainDisplay.VelocityGainKmh(50, 1));
        Assert.Equal("", GrowthGainDisplay.Amount(p, new AbilityGain(AbilityKind.Velocity, 1)));
    }

    [Fact]
    public void Amount_OtherAbilities_KeepTheInternalValue()
    {
        var p = PlayerWithVelocity(44);
        Assert.Equal("＋4", GrowthGainDisplay.Amount(p, new AbilityGain(AbilityKind.Contact, 4)));
        Assert.Equal("＋3", GrowthGainDisplay.Amount(p, new AbilityGain(AbilityKind.Control, 3)));
    }
}
