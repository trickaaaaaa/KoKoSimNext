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
    public void DateOf_SummerTournamentStartWeek_IsEarlyJuly()
    {
        // 夏の地方大会開幕週(第13週)は7月1週目であること（OPEN-QUESTIONS Q23で早期化。現実の夏予選＝7月上旬開幕）。
        var (month, wom, _) = _cal.DateOf(_cal.SummerTournamentStartWeek);
        Assert.Equal((7, 1), (month, wom));
        // 開幕(13)→引退(17)は同月内に中4週の余裕がある（大会モードは1週内で消化）。
        Assert.True(_cal.SummerRetireWeek - _cal.SummerTournamentStartWeek >= 3);
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

    // --- 次の公式戦（issue #582: トップバーのカウントダウン） ---

    [Theory]
    [InlineData(0, TournamentKind.Summer, 13)]   // シーズン頭 → 夏まで13週
    [InlineData(12, TournamentKind.Summer, 1)]   // 夏の前週
    [InlineData(13, TournamentKind.Summer, 0)]   // 夏の開幕週＝開催中
    [InlineData(14, TournamentKind.Autumn, 9)]   // 夏の翌週からは秋を指す（旧実装はここから負＝ずっと開催中だった）
    [InlineData(22, TournamentKind.Autumn, 1)]   // 秋の前週
    [InlineData(23, TournamentKind.Autumn, 0)]   // 秋の開幕週＝開催中
    [InlineData(24, TournamentKind.Summer, 39)]  // 秋の翌週からは翌年の夏へ折り返す
    [InlineData(49, TournamentKind.Summer, 14)]  // 最終週 → 翌年の夏まで14週
    public void NextTournament_SwitchesKindAndWrapsAtYearEnd(int week, TournamentKind kind, int weeksLeft)
    {
        Assert.Equal((kind, weeksLeft), _cal.NextTournament(week));
    }

    [Fact]
    public void NextTournament_NeverGoesNegative_AndCountsDownByOne()
    {
        // 全50週で残り週が非負（「開催中」は開幕週の2回だけ）。
        var live = 0;
        for (var w = 0; w < _cal.WeeksPerYear; w++)
        {
            var (_, left) = _cal.NextTournament(w);
            Assert.True(left >= 0, $"第{w}週の残り週が負: {left}");
            if (left == 0) live++;
        }
        Assert.Equal(2, live);

        // 週が1つ進むと残りはちょうど1つ減る（開幕週で次の大会へ乗り換わる所を除く）。
        for (var w = 0; w < _cal.WeeksPerYear - 1; w++)
        {
            var (kind, left) = _cal.NextTournament(w);
            var (nextKind, nextLeft) = _cal.NextTournament(w + 1);
            if (kind == nextKind) Assert.Equal(left - 1, nextLeft);
        }
    }

    [Fact]
    public void NextTournament_StartWeeks_AgreeWithTournamentStartingAt()
    {
        // 「開催中(残り0)」と「その週に開幕する大会がある」は同じ週でなければならない。
        for (var w = 0; w < _cal.WeeksPerYear; w++)
        {
            var (kind, left) = _cal.NextTournament(w);
            Assert.Equal(left == 0 ? kind : (TournamentKind?)null, _cal.TournamentStartingAt(w));
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
