namespace KokoSim.Engine.Practice;

/// <summary>
/// 練習試合の係数（設計書03 §週ターン③, 設計書04 §名声の効果）。
/// バランス調整は data/coefficients.yaml の <c>practice_match:</c> で行う（不変条件#4）。
/// </summary>
public sealed record PracticeMatchCoefficients
{
    /// <summary>1試合あたりの費用[万円]（<see cref="Career.Manager.Funds"/> から減算）。</summary>
    public double Cost { get; init; } = 1.0;

    /// <summary>同格・名声0のときの受諾確率。</summary>
    public double BaseAccept { get; init; } = 0.80;

    /// <summary>相手が1ティア格上になるごとに受諾確率へ乗る減点。</summary>
    public double TierGapPenalty { get; init; } = 0.20;

    /// <summary>名声100あたりの受諾確率加点（格上ほど名声が効くのではなく一律加点）。</summary>
    public double FameWeight { get; init; } = 0.40;

    /// <summary>受諾確率の下限。</summary>
    public double MinAccept { get; init; } = 0.02;

    /// <summary>受諾確率の上限。</summary>
    public double MaxAccept { get; init; } = 0.98;
}
