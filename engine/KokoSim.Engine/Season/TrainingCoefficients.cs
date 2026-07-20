namespace KokoSim.Engine.Season;

/// <summary>育成式の係数（設計書02 §5.1, YAML駆動）。</summary>
public sealed record TrainingCoefficients
{
    /// <summary>主効果の練習基礎値[exp/週]。</summary>
    public double BaseMainExp { get; init; } = 85.0;
    /// <summary>副効果の倍率。</summary>
    public double SubFactor { get; init; } = 0.4;

    /// <summary>施設係数。</summary>
    public double FacilityCoef { get; init; } = 1.0;
    /// <summary>指導力（分野別, Phase 5で監督メタに置換）。暫定固定値。</summary>
    public double CoachingLevel { get; init; } = 20.0;
    /// <summary>指導力1あたりの効率係数（設計書02: 1 + 指導力×0.005）。</summary>
    public double CoachingSlope { get; init; } = 0.005;

    /// <summary>レベルアップ必要exp = LevelUpBase × LevelUpGrowth^v（設計書02: 100 × 1.05^v）。</summary>
    public double LevelUpBase { get; init; } = 100.0;
    public double LevelUpGrowth { get; init; } = 1.05;

    /// <summary>合宿の経験値倍率（設計書04 §4: 夏×2.0 / 冬×2.5）。</summary>
    public double SummerCampMult { get; init; } = 2.0;
    public double WinterCampMult { get; init; } = 2.5;

    /// <summary>
    /// 大会モード中の練習効果倍率（&lt;1.0で低下）。大会期間は試合中心で練習量が落ちることを表す。
    /// 呼び出し側が合宿倍率と同様に common へ乗算する（大会モード時のみ適用, 通常週は1.0）。
    /// </summary>
    public double TournamentPracticeMult { get; init; } = 0.5;

    /// <summary>
    /// 基準週の練習時間[分]（設計書03 §3.1）。1メニューにこの分を配分すると BaseMainExp と一致する
    /// （時間配分育成の後方互換の基準）。成長・疲労は「配分分 ÷ この値」で線形スケールする。
    /// </summary>
    public int ReferenceWeekMinutes { get; init; } = 300;

    /// <summary>
    /// 施設0（デフォルト校）の週あたり練習時間[分]。照明・寮などの施設導入でこれを増やす（設計書03 §4）。
    /// 現状は基準週と同値。将来 School/施設Lv から算出して SeasonContext.BudgetMinutes に注入する。
    /// </summary>
    public int DefaultBudgetMinutes { get; init; } = 300;

    public double RequiredExp(int level) => LevelUpBase * Math.Pow(LevelUpGrowth, level);
}
