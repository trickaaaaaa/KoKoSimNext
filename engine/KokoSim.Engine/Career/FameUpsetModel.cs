using KokoSim.Engine.Nation;

namespace KokoSim.Engine.Career;

/// <summary>
/// 番狂わせ（アップセット）に連動する名声デルタ（issue #170・設計書04 §1.2）。
/// 「格上に勝つ→名声↑（金星）」「格下に負ける→名声↓（取りこぼし）」を Tier 格差で表現する。
/// 順当な結果（同格・格上に負け・格下に勝ち）は 0＝名声をほとんど動かさない。純関数・決定論。
/// </summary>
public static class FameUpsetModel
{
    /// <summary>
    /// 1試合の名声デルタ。gap = 相手Tier − 自校Tier（正＝相手が格上）。
    /// 金星（格上に勝利）で正、取りこぼし（格下に敗北）で負、その他 0。
    /// </summary>
    public static double MatchDelta(double selfStrength, double opponentStrength, bool won, CareerCoefficients c)
    {
        var gap = (int)Tiers.FromStrength(opponentStrength) - (int)Tiers.FromStrength(selfStrength);
        if (won && gap > 0) return c.FameUpsetWinPerTier * gap;    // 金星：格差ほど大きい
        if (!won && gap < 0) return c.FameUpsetLossPerTier * gap;  // 取りこぼし：gap<0 なので負に働く
        return 0.0;                                                // 順当（同格含む）はほぼ動かさない
    }

    /// <summary>
    /// シーズン内の各試合デルタを年次 Fame 更新へ畳み込む代表値（設計判断: 総和）。
    /// 上昇側だけ FameUpsetSeasonCap で頭打ちにする（「ポンポン上がると面白くない」＝1シーズンの金星ラッシュでも
    /// 急上昇しない）。下降側（取りこぼし）は頭打ちせず、緊張感を残す。
    /// </summary>
    public static double SeasonDelta(IEnumerable<TrackedMatch> matches, double selfStrength, CareerCoefficients c)
    {
        double up = 0.0, down = 0.0;
        foreach (var m in matches)
        {
            var d = MatchDelta(selfStrength, m.OpponentStrength, m.Won, c);
            if (d > 0) up += d; else down += d;
        }
        if (up > c.FameUpsetSeasonCap) up = c.FameUpsetSeasonCap;
        return up + down;
    }
}
