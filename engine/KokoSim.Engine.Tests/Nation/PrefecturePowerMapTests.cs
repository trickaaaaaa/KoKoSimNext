using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

public sealed class PrefecturePowerMapTests
{
    private static School S(int id, int pref, double strength)
        => new() { Id = id, Name = "校" + id, PrefectureId = pref, Strength = strength };

    [Fact]
    public void StrongestByPrefecture_PicksTheStrongestPerPrefecture_TiesToTheSmallerId()
    {
        var best = PrefecturePowerMap.StrongestByPrefecture(new[]
        {
            S(1, 0, 60), S(2, 0, 72), S(3, 0, 72), S(4, 1, 40), S(5, 1, 35),
        });
        Assert.Equal(2, best.Count);
        Assert.Equal(2, best[0].Id);
        Assert.Equal(4, best[1].Id);
    }

    [Theory]
    [InlineData(Tier.S, Tier.S)]
    [InlineData(Tier.A, Tier.A)]
    [InlineData(Tier.B, Tier.B)]
    [InlineData(Tier.C, Tier.C)]
    [InlineData(Tier.D, Tier.D)]
    [InlineData(Tier.E, Tier.D)]
    [InlineData(Tier.G, Tier.D)]
    public void StepOf_FoldsEverythingBelowDIntoD(Tier tier, Tier expected)
        => Assert.Equal(expected, PrefecturePowerMap.StepOf(tier));
}
