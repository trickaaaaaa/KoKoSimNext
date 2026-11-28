using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Replay;

/// <summary>
/// どの試合の再生データを残すか・いつまで残すか（issue #1580・オーナー判断 2026-11-28）。
/// 範囲は「自校の全試合＋自県の上位ティア校の試合＋甲子園の全試合」。保持は OPEN-QUESTIONS Q15 未決3＝
/// 自校が関わった試合は全履歴・他校同士は直近2年。
/// </summary>
public static class MatchReplayPolicy
{
    /// <summary>
    /// 「自県の上位ティア校」とみなす最低のティア（これ以上＝チーム力70以上）。生成直後の全国で県あたり平均3.7校
    /// （A以上だと平均1.0校で、47県中19県は該当校なし＝「同県の強豪」が成り立たない）。
    /// </summary>
    public const Tier PrefectureTopMinTier = Tier.B;

    /// <summary>他校同士の試合を残す年数（今年度を含む。2＝今年度と前年度）。</summary>
    public const int OtherSchoolRetentionYears = 2;

    /// <summary>
    /// 裏試合（自校が出ない試合）を残す理由。<paramref name="kind"/> は大会の種別。
    /// 自校の試合はここを通らない（自校の試合は呼び出し側が <see cref="NotableReason.OwnSchool"/> で残す）。
    /// </summary>
    public static NotableReason Evaluate(
        TournamentKind? kind, School away, School home, int managerSchoolId, int managerPrefectureId)
    {
        var reasons = NotableReason.None;
        if (away.Id == managerSchoolId || home.Id == managerSchoolId) reasons |= NotableReason.OwnSchool;
        if (kind is TournamentKind.SummerKoshien or TournamentKind.SenbatsuKoshien) reasons |= NotableReason.Koshien;
        if (IsPrefectureTop(away, managerPrefectureId) || IsPrefectureTop(home, managerPrefectureId))
            reasons |= NotableReason.PrefectureTop;
        return reasons;
    }

    private static bool IsPrefectureTop(School s, int managerPrefectureId)
        => s.PrefectureId == managerPrefectureId && s.Tier >= PrefectureTopMinTier;

    /// <summary>
    /// 今の年度（<paramref name="currentYear"/>＝YearIndex）でまだ残すか。自校が関わった試合は常に残し、
    /// 他校同士は直近 <see cref="OtherSchoolRetentionYears"/> 年だけ。
    /// </summary>
    public static bool IsRetained(MatchReplayRecord record, int currentYear)
        => record.IsOwn || record.Year > currentYear - OtherSchoolRetentionYears;
}
