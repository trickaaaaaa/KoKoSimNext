using System;
using System.Collections.Generic;
using System.Linq;

namespace KokoSim.Engine.Season;

/// <summary>
/// 主将の選定と年度更新（設計書09 §8）。統率力 = 統率傾向(Leadership) × 精神力(Mental) / 100 で選ぶ。
/// 主将は基本「最上級生」から選ぶ（現実の高校野球）。3年生引退で主将が抜けたら自動で選び直す。
/// プレイヤーの手動指名（<see cref="Designate"/>）は在籍する限り尊重し、<see cref="EnsureCaptain"/> は上書きしない。
/// 純ロジック（乱数不要・決定論）。
/// </summary>
public static class CaptainSelector
{
    /// <summary>統率力（0〜約99）。主将適性の指標。</summary>
    public static double LeadershipPower(DevelopingPlayer p) => p.Leadership * p.Mental / 100.0;

    /// <summary>現在の主将（不在なら null）。</summary>
    public static DevelopingPlayer? Current(IReadOnlyList<DevelopingPlayer> roster)
        => roster.FirstOrDefault(p => p.IsCaptain);

    /// <summary>
    /// 主将を自動選定する（最上級生の中で統率力最大）。既存の IsCaptain は一旦すべて外して付け替える。
    /// タイブレークは統率力→統率傾向→名前で決定論。空ロスターは null。
    /// </summary>
    public static DevelopingPlayer? SelectAuto(IReadOnlyList<DevelopingPlayer> roster)
    {
        foreach (var p in roster) p.IsCaptain = false;
        // 引退済み（夏で引退した3年）は候補外。ロスターに残っていても主将にはしない（RosterLifecycle）。
        var eligible = roster.Where(p => !p.IsRetired).ToList();
        if (eligible.Count == 0) return null;

        var topGrade = eligible.Max(p => p.Grade);
        var chosen = eligible
            .Where(p => p.Grade == topGrade)
            .OrderByDescending(LeadershipPower)
            .ThenByDescending(p => p.Leadership)
            .ThenBy(p => p.Name, System.StringComparer.Ordinal)
            .First();
        chosen.IsCaptain = true;
        return chosen;
    }

    /// <summary>
    /// ロスターに有効な主将が居ることを保証する。現主将が在籍していればそのまま（手動指名も尊重）、
    /// 居なければ自動選定する（3年生引退→選び直しの入口）。年度更新後に呼ぶ。
    /// </summary>
    public static DevelopingPlayer? EnsureCaptain(IReadOnlyList<DevelopingPlayer> roster)
    {
        // 引退済みの主将は在籍扱いしない（引退週フックを通さず週が飛んだ場合の保険）。
        foreach (var p in roster) if (p.IsRetired) p.IsCaptain = false;

        // IsCaptain が複数付いていたら不整合。1名以下に正規化してから判定。
        var flagged = roster.Where(p => p.IsCaptain).ToList();
        if (flagged.Count == 1) return flagged[0];
        if (flagged.Count > 1)
        {
            foreach (var p in roster) p.IsCaptain = false;
        }
        return SelectAuto(roster);
    }

    /// <summary>プレイヤーによる手動指名（設計書09 §8）。他の主将フラグを外して指定選手に付ける。</summary>
    public static void Designate(IReadOnlyList<DevelopingPlayer> roster, DevelopingPlayer captain)
    {
        foreach (var p in roster) p.IsCaptain = false;
        captain.IsCaptain = true;
    }

    // ===== 新チーム発足と指名ウィンドウ（設計書09 §8: 3年生引退→新チームで主将を選び直す） =====

    /// <summary>
    /// 新チームが発足する週か。3年生は <see cref="SeasonCalendar.SummerRetireWeek"/> まで在籍扱い
    /// （<see cref="SeasonCalendar.CanTrain"/> と整合）なので、その翌週が新チームの初週になる。
    /// </summary>
    public static bool IsNewTeamStartWeek(int week, SeasonCalendar calendar)
        => week == calendar.SummerRetireWeek + 1;

    /// <summary>
    /// 主将の指名を受け付ける期間か（設計書09 §8）。夏の3年引退で新チームが発足した週から、
    /// 新チーム初の公式戦（秋季県大会 <see cref="SeasonCalendar.AutumnTournamentStartWeek"/>）の前週まで。
    /// この期間外はプレイヤーの指名不可（主将が不在になった場合は <see cref="EnsureCaptain"/> が自動補充する）。
    /// 乱数不要・純関数。
    /// </summary>
    public static bool IsDesignationWindow(int week, SeasonCalendar calendar)
        => week > calendar.SummerRetireWeek && week < calendar.AutumnTournamentStartWeek;

    /// <summary>新チームの構成員（3年生は夏で引退済み＝除外）。順序は元ロスターのまま（決定論）。</summary>
    public static IReadOnlyList<DevelopingPlayer> NewTeamMembers(IReadOnlyList<DevelopingPlayer> roster)
        => roster.Where(p => !p.IsRetired && p.Grade < 3).ToList();

    /// <summary>
    /// 指名候補（新チームの最上級生＝新3年）。統率力の生値は伏せたまま UI で選ばせるため、
    /// ここでは並べ替えず元ロスター順のまま返す。新チームが空なら空リスト。
    /// </summary>
    public static IReadOnlyList<DevelopingPlayer> DesignationCandidates(IReadOnlyList<DevelopingPlayer> roster)
    {
        var newTeam = NewTeamMembers(roster);
        if (newTeam.Count == 0) return Array.Empty<DevelopingPlayer>();
        var topGrade = newTeam.Max(p => p.Grade);
        return newTeam.Where(p => p.Grade == topGrade).ToList();
    }

    /// <summary>
    /// 新チーム発足処理（引退週フック）。引退した3年を含め全員の主将フラグを外し、
    /// 新チームの最上級生から <see cref="SelectAuto"/> で暫定主将を立てる（戻り値＝暫定主将）。
    /// この後 <see cref="IsDesignationWindow"/> の間だけプレイヤーが <see cref="Designate"/> で差し替えられる。
    /// </summary>
    public static DevelopingPlayer? BeginNewTeam(IReadOnlyList<DevelopingPlayer> roster)
    {
        foreach (var p in roster) p.IsCaptain = false;
        return SelectAuto(NewTeamMembers(roster));
    }

    /// <summary>
    /// その週にその選手を主将へ指名できるか。指名期間内であること＋新チームの最上級生であることが条件。
    /// </summary>
    public static bool CanDesignate(
        IReadOnlyList<DevelopingPlayer> roster, DevelopingPlayer candidate, int week, SeasonCalendar calendar)
        => IsDesignationWindow(week, calendar) && DesignationCandidates(roster).Contains(candidate);
}
