namespace KokoSim.Engine.Season;

/// <summary>
/// 施設レベル1段の効果（Issue #115・設計書03 §4 / 04）。練習効率係数と週練習時間を持つ。
/// <see cref="TrainingCoefficients.FacilityTiers"/> にレベル順（index=施設レベル）で並べる。
/// レベル0（施設なし）は現状値 (1.0 / 300分) と一致させ、従来のシムを1ビットも動かさない。
/// </summary>
public sealed record FacilityTier
{
    /// <summary>練習効率係数（common に乗る施設係数, 設計書02 §5.1）。</summary>
    public double Coef { get; init; } = 1.0;
    /// <summary>週あたり練習時間[分]（照明・室内練習場・寮などで増える, 設計書03 §4）。</summary>
    public int BudgetMinutes { get; init; } = 300;
}
