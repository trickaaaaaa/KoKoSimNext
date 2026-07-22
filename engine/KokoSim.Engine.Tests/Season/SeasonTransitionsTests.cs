using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 週送りの単一遷移フック（issue #134 デグレ再現の固定）。大会入り判定と3年引退判定が
/// 「同じ1経路」で必ず一緒に露出することを検証する。以前は大会入りが特定画面（ホーム）限定で、
/// 非Home経路で週を跨ぐと夏の大会を素通りして引退だけ発火していた。
/// </summary>
public sealed class SeasonTransitionsTests
{
    private static System.Collections.Generic.List<DevelopingPlayer> Roster() => new()
    {
        new DevelopingPlayer { Name = "三年", Grade = 3 },
        new DevelopingPlayer { Name = "二年", Grade = 2 },
        new DevelopingPlayer { Name = "一年", Grade = 1 },
    };

    [Fact]
    public void SummerTournamentWeek_EntersTournament_WithoutRetiring()
    {
        var calendar = new SeasonCalendar();
        var roster = Roster();

        var t = SeasonTransitions.OnWeekEntered(roster, calendar.SummerTournamentStartWeek, calendar);

        Assert.True(t.EntersTournament);
        Assert.Equal(TournamentKind.Summer, t.TournamentStarting);
        Assert.False(t.StartsNewTeam);                              // 大会入り週は引退しない
        Assert.Empty(RosterLifecycle.Retired(roster));
    }

    [Fact]
    public void AutumnTournamentWeek_EntersTournament()
    {
        var calendar = new SeasonCalendar();

        var t = SeasonTransitions.OnWeekEntered(Roster(), calendar.AutumnTournamentStartWeek, calendar);

        Assert.Equal(TournamentKind.Autumn, t.TournamentStarting);
    }

    [Fact]
    public void NewTeamWeek_RetiresThirdYears_WithoutTournament()
    {
        var calendar = new SeasonCalendar();

        var t = SeasonTransitions.OnWeekEntered(Roster(), calendar.SummerRetireWeek + 1, calendar);

        Assert.False(t.EntersTournament);
        Assert.True(t.StartsNewTeam);
        Assert.Single(t.NewTeam!.Retired);
    }

    /// <summary>
    /// 「非Homeタブ相当の進週」＝共通フックだけを1週ずつ通す経路を再現。夏の大会入りが引退より手前の週で
    /// 必ず起き（大会を素通りしない）、引退が大会入りより前に発火しないことを固定する（issue #134 症状A/B）。
    /// </summary>
    [Fact]
    public void WeekByWeekAdvance_EntersSummerBeforeRetirement()
    {
        var calendar = new SeasonCalendar();
        var roster = Roster();

        int? tournamentWeek = null;
        int? retireWeek = null;
        for (var week = 0; week <= calendar.SummerRetireWeek + 1; week++)
        {
            var t = SeasonTransitions.OnWeekEntered(roster, week, calendar);
            if (t.EntersTournament && tournamentWeek is null) tournamentWeek = week;
            if (t.StartsNewTeam && retireWeek is null) retireWeek = week;
        }

        Assert.Equal(calendar.SummerTournamentStartWeek, tournamentWeek);   // 大会入りを素通りしない
        Assert.Equal(calendar.SummerRetireWeek + 1, retireWeek);
        Assert.True(tournamentWeek < retireWeek);                            // 引退は大会入りより後
    }
}
