using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 学校ごとの通算戦績集計器（issue #84）。勝敗加算・春夏分離・「初出場／N年ぶりM回目」の文言生成の
/// 境界を検証する。乱数を消費しない集計のみなので決定論テストは不要（値を突き合わせれば十分）。
/// </summary>
public sealed class SchoolRecordBookTests
{
    [Fact]
    public void RecordOfficialMatch_AccumulatesWinsAndLosses()
    {
        var book = new SchoolRecordBook();
        book.RecordOfficialMatch(winnerId: 1, loserId: 2);
        book.RecordOfficialMatch(winnerId: 1, loserId: 3);
        book.RecordOfficialMatch(winnerId: 2, loserId: 1);

        Assert.Equal(2, book.For(1).OfficialWins);
        Assert.Equal(1, book.For(1).OfficialLosses);
        Assert.Equal(1, book.For(2).OfficialWins);
        Assert.Equal(1, book.For(2).OfficialLosses);
        Assert.Equal(0, book.For(3).OfficialWins);
        Assert.Equal(1, book.For(3).OfficialLosses);
    }

    [Fact]
    public void For_UnknownSchool_ReturnsZeroRecord()
    {
        var book = new SchoolRecordBook();
        var r = book.For(999);
        Assert.Equal(0, r.OfficialWins);
        Assert.Equal(0, r.OfficialLosses);
        Assert.Equal(0, r.TotalAppearances);
        Assert.Null(r.LastSummerYear);
        Assert.Null(r.LastSpringYear);
        Assert.Equal(BestResult.None, r.BestResult);
    }

    [Fact]
    public void SummerAndSpringAppearances_AreTrackedSeparately()
    {
        var book = new SchoolRecordBook();
        book.RecordKoshienAppearance(1, KoshienKind.Summer, 2028);
        book.RecordKoshienAppearance(1, KoshienKind.Spring, 2029);

        var r = book.For(1);
        Assert.Equal(1, r.SummerAppearances);
        Assert.Equal(1, r.SpringAppearances);
        Assert.Equal(2, r.TotalAppearances);
        Assert.Equal(2028, r.LastSummerYear);
        Assert.Equal(2029, r.LastSpringYear);
    }

    [Fact]
    public void RecordKoshienAppearance_SetsBestResultToAppearance_WhenNoneYet()
    {
        var book = new SchoolRecordBook();
        book.RecordKoshienAppearance(1, KoshienKind.Summer, 2028);
        Assert.Equal(BestResult.Appearance, book.For(1).BestResult);
    }

    [Fact]
    public void UpdateBestResult_OnlyImproves_NeverDowngrades()
    {
        var book = new SchoolRecordBook();
        book.UpdateBestResult(1, BestResult.RunnerUp);
        Assert.Equal(BestResult.RunnerUp, book.For(1).BestResult);

        book.UpdateBestResult(1, BestResult.RoundOf4); // 劣る成績では上書きしない
        Assert.Equal(BestResult.RunnerUp, book.For(1).BestResult);

        book.UpdateBestResult(1, BestResult.Champion);
        Assert.Equal(BestResult.Champion, book.For(1).BestResult);
    }

    [Theory]
    [InlineData(0, null, 2028, "初出場")]
    [InlineData(1, 2027, 2028, "2年連続2回目")]
    [InlineData(4, 2025, 2028, "3年ぶり5回目")]
    [InlineData(2, 2028, 2028, "0年ぶり3回目")] // 通常発生しないが式は素直に計算する
    public void AppearanceLabel_For_ProducesExpectedText(
        int priorAppearances, int? priorLastYear, int currentYear, string expected)
    {
        Assert.Equal(expected, AppearanceLabel.For(priorAppearances, priorLastYear, currentYear));
    }

    [Fact]
    public void RecordKoshienAppearance_CachesLabelOnRecord()
    {
        var book = new SchoolRecordBook();
        book.RecordKoshienAppearance(1, KoshienKind.Summer, 2028); // 初出場
        Assert.Equal("初出場", book.For(1).SummerAppearanceLabel);

        book.RecordKoshienAppearance(1, KoshienKind.Summer, 2029); // 2年連続2回目
        Assert.Equal("2年連続2回目", book.For(1).SummerAppearanceLabel);

        book.RecordKoshienAppearance(1, KoshienKind.Summer, 2033); // 4年ぶり3回目
        Assert.Equal("4年ぶり3回目", book.For(1).SummerAppearanceLabel);
    }
}
