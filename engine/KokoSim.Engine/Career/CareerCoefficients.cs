namespace KokoSim.Engine.Career;

/// <summary>監督キャリアの係数（設計書04 §1, YAML駆動）。</summary>
public sealed record CareerCoefficients
{
    // --- 指導力の成長（采配経験, 設計書04 §1.1） ---
    public double CoachingGrowthPerYear { get; init; } = 3.0;
    public double CoachingGrowthPerWin { get; init; } = 0.8;
    public double CoachingCap { get; init; } = 99.0;

    // --- 監督→学校強さ（指導力がチーム力を押し上げる） ---
    public double CoachingToStrength { get; init; } = 0.78;

    // --- フリー化資格（設計書04 §1.3） ---
    public int FreeKoshienThreshold { get; init; } = 1;   // 甲子園1回でフリー資格
    public double FreeFameThreshold { get; init; } = 52;  // または名声

    // --- 強制転任（教員監督, 設計書04 §1.3） ---
    /// <summary>残留確率ロジスティックの中点となる信頼度（この信頼度で残留確率＝上限の半分）。
    /// TrustReset(50)近傍から到達可能な帯に置き、信頼を積むほど腰を据えられるようにする。</summary>
    public double RetainTrustThreshold { get; init; } = 60;
    /// <summary>残留確率ロジスティックの傾き（信頼度何点で確率が大きく動くか）。</summary>
    public double RetainTrustSpread { get; init; } = 10;
    /// <summary>高信頼時の残留確率の上限。</summary>
    public double RetainProbability { get; init; } = 0.8;

    // --- 名声（設計書04 §1.2, 全国区・持ち越し） ---
    public double FameKoshienAppearance { get; init; } = 22.0;
    public double FameNationalChampion { get; init; } = 45.0;
    public double FamePerWin { get; init; } = 1.5;
    public double FameDecay { get; init; } = 0.92;

    // --- 信頼度（設計書04 §1.2, 校内・転任でリセット） ---
    public double TrustReset { get; init; } = 50.0;
    public double TrustPerWin { get; init; } = 4.0;
    public double TrustKoshien { get; init; } = 20.0;
    /// <summary>1勝もできない不振シーズンの信頼度低下。</summary>
    public double TrustPoorSeasonPenalty { get; init; } = 12.0;

    // --- 資金（設計書04 §1.4, 万円） ---
    public double AnnualBudgetBase { get; init; } = 80.0;
    public double BudgetPerTrust { get; init; } = 0.8;

    // --- 赴任校（教員監督は弱小校に飛ばされがち） ---
    public double NewSchoolStrengthMean { get; init; } = 34.0;
    public double NewSchoolStrengthSd { get; init; } = 6.0;
    /// <summary>フリー監督が選べる好条件校の強さ。</summary>
    public double FreeChoiceSchoolStrength { get; init; } = 48.0;
}
