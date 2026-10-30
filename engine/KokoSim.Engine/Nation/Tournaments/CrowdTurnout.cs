using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>観客の入り（issue #705・表示専用）。3D のスタンドに何層まで観客を出すか。</summary>
public enum CrowdTurnout
{
    /// <summary>まばら（約3割）。</summary>
    Sparse = 1,
    /// <summary>半分（約6割）。</summary>
    Half = 2,
    /// <summary>満員（約9割）。</summary>
    Full = 3,
}

/// <summary>
/// 大会と回戦から観客の入りを決める（issue #705・監督決定「大会と回戦で変える」）。
/// 表示だけ＝判定・帯・決定論には使わない（乱数も使わない）。区切りはこの表1つに集約する。
/// </summary>
public static class CrowdTurnouts
{
    /// <param name="kind">大会の種類。練習試合・大会外は null。</param>
    /// <param name="roundsRemaining">決勝までの残りラウンド数（決勝=1・準決勝=2・準々決勝=3）。不明は null。</param>
    public static CrowdTurnout For(TournamentKind? kind, int? roundsRemaining) => kind switch
    {
        null => CrowdTurnout.Sparse,
        TournamentKind.SummerKoshien or TournamentKind.SenbatsuKoshien => CrowdTurnout.Full,
        TournamentKind.Summer => roundsRemaining switch
        {
            1 => CrowdTurnout.Full,
            2 or 3 => CrowdTurnout.Half,
            _ => CrowdTurnout.Sparse,
        },
        TournamentKind.Autumn => roundsRemaining is 1 or 2 ? CrowdTurnout.Half : CrowdTurnout.Sparse,
        TournamentKind.Regional or TournamentKind.Jingu => CrowdTurnout.Half,
        _ => CrowdTurnout.Sparse,   // 秋の地区予選など
    };
}
