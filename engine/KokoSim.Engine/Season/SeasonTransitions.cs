using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>
/// 週に入った直後に処理すべきシーズン遷移をまとめて返す（issue #134 デグレ対策）。
/// 大会開幕（<see cref="TournamentStarting"/>）と新チーム発足＝3年引退（<see cref="NewTeam"/>）を
/// 「同じ1つの入口」で必ず一緒に露出させることで、「大会入りは特定画面でしか判定されず、
/// 引退だけ全画面で発火する」という非対称（＝夏の大会を素通りして引退する不具合）を封じる。
/// </summary>
public readonly record struct SeasonWeekTransition(
    TournamentKind? TournamentStarting,
    NewTeamTransition? NewTeam)
{
    /// <summary>この週に大会が開幕するか。</summary>
    public bool EntersTournament => TournamentStarting.HasValue;

    /// <summary>この週に新チームが発足（3年引退）するか。</summary>
    public bool StartsNewTeam => NewTeam is not null;
}

/// <summary>
/// 週送りの単一遷移フック（設計書03 §2 / 05 §1.1）。<see cref="OnWeekEntered"/> を「週を進めたら必ず通る
/// 1経路」から呼べば、大会入り判定と引退判定が同じ地点で起きる（どちらか一方だけを処理する経路を
/// 作れない）。純ロジック（<see cref="SeasonCalendar.TournamentStartingAt"/> は副作用なし、引退は
/// <see cref="RosterLifecycle"/> に委譲。不変条件#2）。
/// </summary>
public static class SeasonTransitions
{
    /// <summary>
    /// 週 <paramref name="week"/> に入った直後の遷移を返す。大会開幕週なら種別を、新チーム発足週なら
    /// 引退＋暫定主将選出（<see cref="RosterLifecycle.OnWeekEntered"/>）を含める。夏の大会（第15週）と
    /// 引退（第18週）は別週なので、同一週に両方が同時に立つことはない。
    /// </summary>
    public static SeasonWeekTransition OnWeekEntered(
        IReadOnlyList<DevelopingPlayer> roster, int week, SeasonCalendar calendar)
    {
        var tournament = calendar.TournamentStartingAt(week);
        var newTeam = RosterLifecycle.OnWeekEntered(roster, week, calendar);
        return new SeasonWeekTransition(tournament, newTeam);
    }
}
