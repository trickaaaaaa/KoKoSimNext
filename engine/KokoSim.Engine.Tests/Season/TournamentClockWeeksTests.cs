using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 大会明けの時計送り（issue #1548）。大会は日で進み週暦は開幕時点で止まっているので、
/// 止まっていた週から数えた「M月W週」が大会の最終日の実月日より前に見えないことを固定する。
/// </summary>
public sealed class TournamentClockWeeksTests
{
    private static readonly TournamentSchedule S = new();
    private static readonly SeasonCalendar Cal = new();

    /// <summary>(月, 月内の週) を週インデックスへ。DateOf の逆引きとして往復が合う。</summary>
    [Theory]
    [InlineData(7, 1, 13)]
    [InlineData(7, 10, 14)]
    [InlineData(7, 16, 15)]
    [InlineData(7, 28, 16)]
    [InlineData(7, 31, 17)]
    [InlineData(8, 6, 18)]
    [InlineData(9, 12, 23)]
    [InlineData(9, 30, 24)]   // 9月は3週配分＝30日でも最後の3週目に丸める
    [InlineData(3, 31, 49)]
    public void WeekOf_PicksTheCalendarWeekOfTheRealDate(int month, int day, int week)
        => Assert.Equal(week, Cal.WeekOf(month, day));

    [Fact]
    public void WeekOf_IsConsistentWithDateOf()
    {
        for (var w = 0; w < Cal.WeeksPerYear; w++)
        {
            var d = Cal.DateOf(w);
            Assert.Equal(w, Cal.WeekOf(d.Month, (d.WeekOfMonth - 1) * 7 + 1));
        }
    }

    /// <summary>報告の症状: 7月16日（開幕6日目）に負けたあと、時計は7月2週でなく7月3週以降になる。</summary>
    [Fact]
    public void SummerLossOnJuly16_ClockIsNotBeforeTheDate()
    {
        var open = Cal.SummerTournamentStartWeek;
        var weeks = TournamentDate.ClockWeeksAfter(S, Cal, TournamentKind.Summer, 6, open);
        Assert.Equal(2, weeks);
        AssertClockNotBeforeFinalDate(TournamentKind.Summer, 6, open, weeks);
    }

    /// <summary>夏の県大会の全ての長さ（初日〜神奈川の決勝7月31日）で、明けの時計が最終日より前にならない。</summary>
    [Fact]
    public void Summer_EveryLastDay_ClockIsNotBeforeTheDate()
    {
        var open = Cal.SummerTournamentStartWeek;
        for (var day = 0; day <= 21; day++)
            AssertClockNotBeforeFinalDate(TournamentKind.Summer, day, open,
                TournamentDate.ClockWeeksAfter(S, Cal, TournamentKind.Summer, day, open));
    }

    /// <summary>神奈川200校（21日・決勝7月31日）は7月5週＝引退週（第17週）まで。第18週の新チーム発足を跨がない。</summary>
    [Fact]
    public void Summer_LongestTournament_StopsAtRetireWeek()
    {
        var open = Cal.SummerTournamentStartWeek;
        var weeks = TournamentDate.ClockWeeksAfter(S, Cal, TournamentKind.Summer, 21, open);
        Assert.Equal(Cal.SummerRetireWeek, open + weeks);
    }

    /// <summary>秋季県大会は開幕週（第23週＝9月2週）が実開幕日（9月12日）と揃っているので従来の週数のまま。</summary>
    [Fact]
    public void Autumn_KeepsTheBaselineWeeks()
    {
        var open = Cal.AutumnTournamentStartWeek;
        for (var day = 0; day <= 21; day++)
        {
            var weeks = TournamentDate.ClockWeeksAfter(S, Cal, TournamentKind.Autumn, day, open);
            Assert.Equal(TournamentDate.BaselineClockWeeks(day), weeks);
            AssertClockNotBeforeFinalDate(TournamentKind.Autumn, day, open, weeks);
        }
    }

    /// <summary>
    /// 甲子園・地区大会・神宮・センバツは前の大会や選考発表週の時計から直接つながる＝時計が実月日より手前にいる。
    /// どの長さでも明けの時計が最終日の週に届く。
    /// </summary>
    [Theory]
    [InlineData(TournamentKind.SummerKoshien, 17, 0)]
    [InlineData(TournamentKind.SummerKoshien, 17, 12)]
    [InlineData(TournamentKind.Regional, 25, 8)]
    [InlineData(TournamentKind.Jingu, 28, 9)]
    [InlineData(TournamentKind.SenbatsuKoshien, 39, 12)]
    public void ChainedTournaments_ReachTheFinalDate(TournamentKind kind, int clockWeek, int finalDay)
    {
        var weeks = TournamentDate.ClockWeeksAfter(S, Cal, kind, finalDay, clockWeek);
        AssertClockNotBeforeFinalDate(kind, finalDay, clockWeek, weeks);
    }

    /// <summary>従来の週数（切り捨て・最低1週）より減ることはない＝時計が日付より先にいても戻さない。</summary>
    [Fact]
    public void NeverFewerThanBaseline()
    {
        var weeks = TournamentDate.ClockWeeksAfter(S, Cal, TournamentKind.Summer, 14, 30);
        Assert.Equal(TournamentDate.BaselineClockWeeks(14), weeks);
    }

    private static void AssertClockNotBeforeFinalDate(TournamentKind kind, int finalDay, int clockWeek, int weeks)
    {
        var (m, d) = TournamentDate.DateOf(S, kind, finalDay);
        Assert.True(clockWeek + weeks >= Cal.WeekOf(m, d),
            $"{kind} 最終日{finalDay}日目({m}/{d})の後の時計が第{clockWeek + weeks}週で、日付の週（第{Cal.WeekOf(m, d)}週）より前");
    }
}
