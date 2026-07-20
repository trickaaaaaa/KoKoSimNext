using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 年間カレンダー（設計書03 §2）の週→暦変換。4月始まり・50週。
/// 合宿/引退のアンカー（夏合宿=第18週≒8月, 冬合宿=第38週≒1月）に整合することを検証する。
/// </summary>
public sealed class SeasonCalendarTests
{
    private readonly SeasonCalendar _cal = new();

    [Fact]
    public void DateOf_SeasonStart_IsAprilWeek1()
    {
        var (month, wom, yo) = _cal.DateOf(0);
        Assert.Equal(4, month);
        Assert.Equal(1, wom);
        Assert.Equal(0, yo);
    }

    [Theory]
    [InlineData(0, 4, 1, 0)]    // シーズン頭 = 4月1週目
    [InlineData(17, 7, 5, 0)]   // 3年引退 = 7月末
    [InlineData(18, 8, 1, 0)]   // 夏合宿 = 8月頭
    [InlineData(38, 1, 1, 1)]   // 冬合宿 = 翌1月
    [InlineData(49, 3, 4, 1)]   // 最終週 = 翌3月4週目
    public void DateOf_MatchesCalendarAnchors(int week, int month, int weekOfMonth, int yearOffset)
    {
        var d = _cal.DateOf(week);
        Assert.Equal((month, weekOfMonth, yearOffset), (d.Month, d.WeekOfMonth, d.YearOffset));
    }

    [Fact]
    public void DateOf_SummerRetireWeek_IsLateJuly()
    {
        // 3年引退週(第17週≒7月末)は7月であること。
        var (month, _, _) = _cal.DateOf(_cal.SummerRetireWeek);
        Assert.Equal(7, month);
    }

    [Fact]
    public void DateOf_JanToMar_AreNextCalendarYear()
    {
        // 後半の1〜3月は暦年が翌年(YearOffset=1)。
        for (var w = 0; w < _cal.WeeksPerYear; w++)
        {
            var (month, _, yo) = _cal.DateOf(w);
            Assert.Equal(month <= 3 ? 1 : 0, yo);
        }
    }

    [Fact]
    public void DateOf_CoversExactlyFiftyWeeks_NoGaps()
    {
        // 全50週が (月,週目) で連番になり、月ごとの週目が1から始まること。
        var seenWeeks = 0;
        var prevMonth = 0;
        for (var w = 0; w < _cal.WeeksPerYear; w++)
        {
            var (month, wom, _) = _cal.DateOf(w);
            if (month != prevMonth) { Assert.Equal(1, wom); prevMonth = month; }
            seenWeeks++;
        }
        Assert.Equal(50, seenWeeks);
    }
}
