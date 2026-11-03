using KokoSim.Balance;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>内野安打の型別内訳（issue #759）の集計が閉じていること。</summary>
public class InfieldHitProfileTests
{
    [Fact]
    public void Types_PartitionAllGrounders_AndHits()
    {
        var p = AtBatSimulation.RunInfieldHitProfile(atBats: 4000, seed: 42);

        Assert.True(p.Grounders > 0);
        Assert.Equal(p.Grounders, p.TypeGrounders.Sum());
        Assert.Equal(p.InfieldHits, p.TypeHits.Sum());
        Assert.Equal(p.Grounders, p.ExitAll.Sum());
        Assert.Equal(p.Grounders, p.SpeedAll.Sum());
        Assert.Equal(p.Grounders, p.ApexAll.Sum());
        Assert.Equal(p.InfieldHits, p.ApexHits.Sum());
    }

    [Fact]
    public void SameSeed_GivesSameProfile()
    {
        var a = AtBatSimulation.RunInfieldHitProfile(atBats: 2000, seed: 7);
        var b = AtBatSimulation.RunInfieldHitProfile(atBats: 2000, seed: 7);
        Assert.Equal(a.ToMarkdown(), b.ToMarkdown());
    }
}
