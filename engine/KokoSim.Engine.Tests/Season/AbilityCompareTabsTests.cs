using System.Linq;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 選手比較タブ（issue #192）のタブ→能力(AbilityKind)割当を検証する。全 AbilityKind が
/// 過不足なく「野手能力」「投手能力」のどちらか一方に属することを保証する（漏れ・重複ゼロ）。
/// </summary>
public sealed class AbilityCompareTabsTests
{
    [Fact]
    public void PitcherAbilities_MatchesAbilityKindsPitching()
    {
        Assert.Equal(AbilityKinds.Pitching, AbilityCompareTabs.PitcherAbilities);
    }

    [Fact]
    public void FielderAndPitcher_TogetherCoverAllAbilityKinds_WithNoOverlap()
    {
        var fielder = AbilityCompareTabs.FielderAbilities;
        var pitcher = AbilityCompareTabs.PitcherAbilities;

        Assert.Empty(fielder.Intersect(pitcher));
        var union = fielder.Concat(pitcher).OrderBy(k => k).ToArray();
        var all = AbilityKinds.All.OrderBy(k => k).ToArray();
        Assert.Equal(all, union);
    }

    [Fact]
    public void FielderAbilities_HasNoDuplicates()
    {
        Assert.Equal(AbilityCompareTabs.FielderAbilities.Length, AbilityCompareTabs.FielderAbilities.Distinct().Count());
    }

    [Theory]
    [InlineData(CompareTab.Fielder)]
    [InlineData(CompareTab.Pitcher)]
    public void AbilitiesFor_ReturnsMatchingArray(CompareTab tab)
    {
        var expected = tab == CompareTab.Fielder ? AbilityCompareTabs.FielderAbilities : AbilityCompareTabs.PitcherAbilities;
        Assert.Equal(expected, AbilityCompareTabs.AbilitiesFor(tab));
    }

    [Fact]
    public void FielderAbilities_DoesNotContainAnyPitchingKind()
    {
        Assert.All(AbilityCompareTabs.FielderAbilities, k => Assert.False(AbilityKinds.IsPitching(k)));
    }
}
