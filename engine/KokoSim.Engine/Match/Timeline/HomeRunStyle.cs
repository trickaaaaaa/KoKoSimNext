namespace KokoSim.Engine.Match.Timeline;

/// <summary>
/// 本塁打の当たりの種類（issue #704・表示専用）。3D の守備ビューがカメラの見せ方を選ぶのに使う。
/// 判定・帯には一切使わない＝既に決まった弾道（フェンス上の余裕・打球角度）を言葉に分けるだけ。
/// </summary>
public enum HomeRunStyle
{
    /// <summary>どれにも当たらない普通の本塁打。</summary>
    Standard,
    /// <summary>打った瞬間それと分かる大飛球（フェンスの上を大きく越えた）。</summary>
    NoDoubt,
    /// <summary>フェンスぎりぎり（わずかに越えた）。</summary>
    Wall,
    /// <summary>弾丸ライナー（低い角度で突き刺さった）。</summary>
    Liner,
    /// <summary>高々と上がった一発（打球角度が高い）。</summary>
    Towering,
}

/// <summary>本塁打の当たりの分類（issue #704）。しきい値は計測（#704 のコメント）から決めた表示用の値。</summary>
public static class HomeRunStyles
{
    /// <summary>この余裕[m]以上でフェンスを越えたら「打った瞬間」（B同士で約25%＝座席の中段より奥）。</summary>
    public const double NoDoubtClearanceM = 7.0;
    /// <summary>この余裕[m]未満なら「ぎりぎり」（約25%）。</summary>
    public const double WallClearanceM = 1.5;
    /// <summary>打球角度[度]がこれ未満なら「弾丸ライナー」（約12%）。</summary>
    public const double LinerMaxAngleDeg = 22.0;
    /// <summary>打球角度[度]がこれ以上なら「高々と」（約13%）。</summary>
    public const double ToweringMinAngleDeg = 35.0;

    /// <summary>
    /// 分類する。複数に当てはまるときは 打った瞬間 → ぎりぎり → ライナー → 高々 の順
    /// （越え方＝見ている人が最初に感じる違いを優先し、角度はその次）。
    /// </summary>
    /// <param name="clearanceM">フェンスの上を越えたときの余裕[m]（フェンス上での高さ−フェンス高）。</param>
    /// <param name="launchAngleDeg">打球角度[度]。</param>
    public static HomeRunStyle Classify(double clearanceM, double launchAngleDeg)
    {
        if (clearanceM >= NoDoubtClearanceM) return HomeRunStyle.NoDoubt;
        if (clearanceM < WallClearanceM) return HomeRunStyle.Wall;
        if (launchAngleDeg < LinerMaxAngleDeg) return HomeRunStyle.Liner;
        if (launchAngleDeg >= ToweringMinAngleDeg) return HomeRunStyle.Towering;
        return HomeRunStyle.Standard;
    }
}
