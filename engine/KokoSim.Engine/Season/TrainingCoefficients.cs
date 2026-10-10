using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>育成式の係数（設計書02 §5.1, YAML駆動）。</summary>
public sealed record TrainingCoefficients
{
    /// <summary>主効果の練習基礎値[exp/週]。2026-08-15 に 85→130（案D。中核能力の3年間の伸び +4〜5 → +7）。</summary>
    public double BaseMainExp { get; init; } = 130.0;
    /// <summary>副効果の倍率。</summary>
    public double SubFactor { get; init; } = 0.4;

    /// <summary>施設係数（施設レベル0＝現状の基準値）。<see cref="FacilityTiers"/> 未指定時の既定施設係数。</summary>
    public double FacilityCoef { get; init; } = 1.0;
    /// <summary>
    /// 基準指導力（Issue #115）。監督メタ（<see cref="CoachingProfile"/>）を注入しない場合に全分野へ適用する既定値、
    /// かつ分野別指導力の「中立点」＝この指導力なら従来のシムと1ビット一致する（不変条件#2）。
    /// 監督指導力を注入すると各能力分野が Manager.Coaching.* で駆動される。
    /// </summary>
    public double CoachingLevel { get; init; } = 20.0;
    /// <summary>指導力1あたりの効率係数（設計書02: 1 + 指導力×0.005）。</summary>
    public double CoachingSlope { get; init; } = 0.005;

    /// <summary>
    /// 個別指導3枠のスロット数上限（Issue #126・設計書06 §3.3）。エンジン側では強制しない
    /// （UI側の <c>TrainingPlanState.ToggleNominate</c> がこの値を上限として使う）。
    /// </summary>
    public int IndividualCoachingSlots { get; init; } = 3;

    /// <summary>
    /// 個別指導3枠の追加倍率スケール（Issue #126・OPEN-QUESTIONS Q7(a)）。指名選手の主効果expにだけ、
    /// 分野別指導力由来のcoachingFactor比（Issue #115・<see cref="DevelopmentModel"/> の中立点比）を
    /// もう一段乗せる: 追加倍率 = 1 + (coachingFactor比 − 1) × このスケール。
    /// 既定1.0＝#115の写像をそのまま流用。coaching=null または指名なしなら追加倍率は1.0（従来一致）。
    /// </summary>
    public double IndividualCoachingBonusScale { get; init; } = 1.0;

    /// <summary>
    /// 施設レベル→(係数, 週練習時間) 対応表（Issue #115・設計書03 §4）。index=施設レベル。
    /// 既定は data/coefficients.yaml training.facility_tiers と一致（Lv0-4, issue #402）。Lv0＝現状値(1.0/300)で
    /// SeasonContext.FacilityLevel=0（既定）のとき従来と1ビット一致する。多軸施設は <see cref="FacilityCatalog"/> が担い、
    /// この単一軸表は <c>SeasonEngine.FacilityLevel &gt; 0</c> かつ多軸未注入のときのフォールバックとして効く。
    /// </summary>
    public IReadOnlyList<FacilityTier> FacilityTiers { get; init; } = DefaultFacilityTiers();

    /// <summary>data/coefficients.yaml training.facility_tiers と一致する既定表（Lv0-4, issue #402）。</summary>
    private static IReadOnlyList<FacilityTier> DefaultFacilityTiers() => new FacilityTier[]
    {
        new() { Coef = 1.00, BudgetMinutes = 300 }, // Lv0 施設なし（現状・従来一致）
        new() { Coef = 1.35, BudgetMinutes = 400 }, // Lv1
        new() { Coef = 1.70, BudgetMinutes = 500 }, // Lv2
        new() { Coef = 2.05, BudgetMinutes = 600 }, // Lv3
        new() { Coef = 2.40, BudgetMinutes = 700 }, // Lv4 フル施設
    };

    /// <summary>
    /// レベルアップ必要exp = LevelUpBase × LevelUpGrowth^v（設計書02 §5.1）。
    /// Growth は 2026-08-15 に 1.05→1.04（案D）。1.05 だと Lv74 の必要expが Lv32 の7.8倍で高レベル帯の成長が
    /// ほぼ止まり、名声で入る B/A の新入生が「入学時が完成形」になる。1.04 で Lv74 でも3年 +2〜3（施設フル +10）。
    /// </summary>
    public double LevelUpBase { get; init; } = 100.0;
    public double LevelUpGrowth { get; init; } = 1.04;

    /// <summary>
    /// 能力別 trainability（伸ばしやすさ）係数（Issue #114）。exp加算時に乗算する内在特性。
    /// 既定（全1.0）で従来と完全一致。
    /// </summary>
    public TrainabilityCoefficients Trainability { get; init; } = new();

    /// <summary>
    /// 守備位置適性の必要exp倍率（&lt;1.0で速く伸びる, 守備適性 未決1・2026-07-22）。
    /// 能力レベルの必要exp（LevelUpBase×LevelUpGrowth^v）にこれを乗じて適性専用に緩める。
    /// 既定は data/coefficients.yaml training.aptitude_required_exp_factor と一致（issue #402）。
    /// </summary>
    public double AptitudeRequiredExpFactor { get; init; } = 0.6;

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

    // === 実戦成長（設計書02 §5.3a, Q8・2026-07-20）: 精神力・走塁判断・捕手リードは試合出場でのみ伸びる ===
    /// <summary>1試合出場あたりの精神力exp（成長段階係数を乗算）。</summary>
    public double MatchMentalExp { get; init; } = 260.0;
    /// <summary>1試合の捕手出場あたりのリードexp（成長段階係数を乗算）。</summary>
    public double MatchLeadExp { get; init; } = 400.0;
    /// <summary>1試合出場あたりの走塁判断exp（成長段階係数を乗算, AbilityKind.Baserunning へ）。</summary>
    public double MatchBaserunningExp { get; init; } = 300.0;

    public double RequiredExp(int level) => LevelUpBase * Math.Pow(LevelUpGrowth, level);
}
