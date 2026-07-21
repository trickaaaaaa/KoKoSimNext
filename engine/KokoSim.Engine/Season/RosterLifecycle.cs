using System;
using System.Collections.Generic;
using System.Linq;

namespace KokoSim.Engine.Season;

/// <summary>新チーム発足（引退週フック）の結果。引退した3年と、その場で立てた暫定主将。</summary>
/// <param name="Retired">この処理で新たに引退扱いになった選手（元の順序のまま）。</param>
/// <param name="InterimCaptain">新チームの暫定主将（<see cref="CaptainSelector.SelectAuto"/> 選出）。新チームが空なら null。</param>
public sealed record NewTeamTransition(
    IReadOnlyList<DevelopingPlayer> Retired,
    DevelopingPlayer? InterimCaptain);

/// <summary>
/// 部員の在籍ライフサイクル（設計書03 §2）。夏の第17週で3年生が引退するが、ロスターからは除去せず
/// <see cref="DevelopingPlayer.IsRetired"/> を立てて残す（記録は残しつつ、練習・試合・選手一覧から外す）。
/// 実際の除去は年度替わり（4月）の卒業でまとめて行う。
/// 純ロジック（乱数不要・決定論、不変条件#2）。
/// </summary>
public static class RosterLifecycle
{
    /// <summary>在籍中（＝引退していない）部員だけを抜き出す。順序は元ロスターのまま（決定論）。</summary>
    public static IReadOnlyList<DevelopingPlayer> Active(IReadOnlyList<DevelopingPlayer> roster)
        => roster.Where(p => !p.IsRetired).ToList();

    /// <summary>引退済みの部員だけを抜き出す。順序は元ロスターのまま。</summary>
    public static IReadOnlyList<DevelopingPlayer> Retired(IReadOnlyList<DevelopingPlayer> roster)
        => roster.Where(p => p.IsRetired).ToList();

    /// <summary>
    /// 3年生を引退扱いにする（フラグを立てるだけでロスターからは外さない）。
    /// すでに引退している選手は対象外＝冪等。戻り値＝この呼び出しで新たに引退した選手。
    /// </summary>
    public static IReadOnlyList<DevelopingPlayer> RetireThirdYears(IReadOnlyList<DevelopingPlayer> roster)
    {
        var retiring = roster.Where(p => !p.IsRetired && p.Grade >= 3).ToList();
        foreach (var p in retiring) p.IsRetired = true;
        return retiring;
    }

    /// <summary>
    /// 新チーム発足（引退週フック, 設計書09 §8）。3年生を引退扱いにし、残った新チームの最上級生から
    /// 暫定主将を立てる。以降 <see cref="CaptainSelector.IsDesignationWindow"/> の間だけ
    /// プレイヤーが <see cref="CaptainSelector.Designate"/> で差し替えられる。冪等（2度呼んでも同じ状態）。
    /// </summary>
    public static NewTeamTransition BeginNewTeam(IReadOnlyList<DevelopingPlayer> roster)
    {
        var retired = RetireThirdYears(roster);
        var captain = CaptainSelector.BeginNewTeam(roster);
        return new NewTeamTransition(retired, captain);
    }

    /// <summary>
    /// 週を1つ進めた直後に呼ぶ遷移フック。新チーム発足週（<see cref="CaptainSelector.IsNewTeamStartWeek"/>）
    /// なら引退＋新チーム発足を行い、それ以外の週は null を返す（何もしない）。
    /// 週送りの呼び出し口を1本に集約するための入口。
    /// </summary>
    public static NewTeamTransition? OnWeekEntered(
        IReadOnlyList<DevelopingPlayer> roster, int week, SeasonCalendar calendar)
        => CaptainSelector.IsNewTeamStartWeek(week, calendar) ? BeginNewTeam(roster) : null;
}
