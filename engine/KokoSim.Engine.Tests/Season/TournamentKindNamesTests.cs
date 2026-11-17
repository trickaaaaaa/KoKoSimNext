using System;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

public class TournamentKindNamesTests
{
    [Theory]
    [InlineData(TournamentKind.Summer, "夏")]
    [InlineData(TournamentKind.Autumn, "秋")]
    [InlineData(TournamentKind.SummerKoshien, "甲子園")]
    [InlineData(TournamentKind.SenbatsuKoshien, "センバツ")]
    [InlineData(TournamentKind.Regional, "地区大会")]
    [InlineData(TournamentKind.Jingu, "明治神宮大会")]
    [InlineData(TournamentKind.AutumnDistrict, "地区予選")]
    public void Of_ReturnsOwnName(TournamentKind kind, string expected)
        => Assert.Equal(expected, TournamentKindNames.Of(kind));

    [Fact]
    public void Of_CoversEveryKind_AndNamesAreDistinct()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var kind in Enum.GetValues<TournamentKind>())
        {
            var name = TournamentKindNames.Of(kind);
            Assert.False(string.IsNullOrEmpty(name));
            Assert.True(seen.Add(name), $"{kind} の呼び名 {name} が他の大会と重複");
        }
    }

    [Theory]
    [InlineData(TournamentKind.Summer, "夏の大会")]
    [InlineData(TournamentKind.Autumn, "秋の大会")]
    [InlineData(TournamentKind.SummerKoshien, "甲子園")]
    [InlineData(TournamentKind.SenbatsuKoshien, "センバツ")]
    [InlineData(TournamentKind.Regional, "地区大会")]
    [InlineData(TournamentKind.Jingu, "明治神宮大会")]
    public void Short_NeverFallsBackToAutumn(TournamentKind kind, string expected)
        => Assert.Equal(expected, TournamentKindNames.Short(kind));

    [Fact]
    public void IsPrefectural_OnlyPrefectureLevelTournaments()
    {
        Assert.True(TournamentKindNames.IsPrefectural(TournamentKind.Summer));
        Assert.True(TournamentKindNames.IsPrefectural(TournamentKind.Autumn));
        Assert.False(TournamentKindNames.IsPrefectural(TournamentKind.SummerKoshien));
        Assert.False(TournamentKindNames.IsPrefectural(TournamentKind.SenbatsuKoshien));
        Assert.False(TournamentKindNames.IsPrefectural(TournamentKind.Regional));
        Assert.False(TournamentKindNames.IsPrefectural(TournamentKind.Jingu));
    }
}
