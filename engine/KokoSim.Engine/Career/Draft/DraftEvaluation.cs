namespace KokoSim.Engine.Career.Draft;

/// <summary>予想指名順位バンド（注目度→予想, 設計書20 §3.1）。序列を持つ（大きいほど上位指名）。</summary>
public enum DraftRankBand
{
    /// <summary>圏外（候補でない）。</summary>
    None = 0,
    /// <summary>下位・育成。</summary>
    LowerRound = 1,
    /// <summary>中位（4〜6位）。</summary>
    MiddleRound = 2,
    /// <summary>上位（2〜3位）。</summary>
    UpperRound = 3,
    /// <summary>1位候補。</summary>
    FirstRound = 4,
}

/// <summary>バンドの表示補助（文言・代表順位）。engine 側は純データ、文言整形は UI で行ってもよい。</summary>
public static class DraftRankBands
{
    /// <summary>候補か（圏外でない）。</summary>
    public static bool IsCandidate(this DraftRankBand band) => band != DraftRankBand.None;

    /// <summary>バンドの代表指名順位（指名確定時のround）。圏外は0。</summary>
    public static int RepresentativeRound(this DraftRankBand band) => band switch
    {
        DraftRankBand.FirstRound => 1,
        DraftRankBand.UpperRound => 2,
        DraftRankBand.MiddleRound => 4,
        DraftRankBand.LowerRound => 6,
        _ => 0,
    };

    /// <summary>予想の短い日本語文言（フィード/画面のデフォルト）。</summary>
    public static string Label(this DraftRankBand band) => band switch
    {
        DraftRankBand.FirstRound => "1位指名が有力",
        DraftRankBand.UpperRound => "上位指名圏",
        DraftRankBand.MiddleRound => "中位指名圏",
        DraftRankBand.LowerRound => "下位／育成で名前が挙がる",
        _ => "圏外",
    };
}

/// <summary>
/// 1選手のドラフト評価スナップショット（設計書20 §2-3）。ある週時点の注目度と予想バンド。
/// Shell はこれを前回スナップショットと突き合わせて通知フィード（候補入り／注目度変化）を作る。
/// </summary>
/// <param name="PlayerId">育成選手ID（<see cref="Season.DevelopingPlayer.Id"/>）。</param>
/// <param name="Name">表示名（フィード文言用）。</param>
/// <param name="Grade">学年（1〜3）。</param>
/// <param name="IsPitcher">投手評価か（役割別加重の分岐）。</param>
/// <param name="Notability">注目度スコア（0〜100）。</param>
/// <param name="Band">予想指名順位バンド。</param>
public sealed record DraftEvaluation(
    int PlayerId,
    string Name,
    int Grade,
    bool IsPitcher,
    double Notability,
    DraftRankBand Band);

/// <summary>
/// 指名確定の1件（10月最終週・3年のみ, 設計書20 §3.2）。指名漏れも <see cref="Nominated"/>=false で残す。
/// </summary>
/// <param name="PlayerId">育成選手ID。</param>
/// <param name="Name">表示名。</param>
/// <param name="Notability">確定時の注目度。</param>
/// <param name="Band">予想バンド（指名時のround導出元）。</param>
/// <param name="Nominated">指名されたか。</param>
/// <param name="Round">指名順位（指名時のみ1以上、指名漏れは0）。</param>
public sealed record DraftPick(
    int PlayerId,
    string Name,
    double Notability,
    DraftRankBand Band,
    bool Nominated,
    int Round);

/// <summary>10月最終週のドラフト結果（3年候補の指名確定一覧, 設計書20 §3.2）。</summary>
/// <param name="Year">季シーズン年（1始まり）。カレンダー接続用。</param>
/// <param name="Picks">3年候補ごとの確定結果（元ロスター順）。指名者は <see cref="DraftPick.Nominated"/>=true。</param>
public sealed record DraftResult(int Year, IReadOnlyList<DraftPick> Picks)
{
    /// <summary>実際に指名された選手だけ。</summary>
    public IReadOnlyList<DraftPick> Nominated =>
        Picks.Where(p => p.Nominated).OrderBy(p => p.Round).ToList();
}
