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
    // 2026-08-15（案2）: 甲22/勝1.5/減衰0.92 → 甲30/勝4.0/減衰0.80。旧値は時定数 1/(1−0.92)=12.5年で
    // 「勝っても名声が動かない」（年2勝で5年目12・10年目21）。新値は時定数5年・定常＝勝数×20（旧18.75と同等）で
    // 上がりも下がりも速い（100→年1勝で6年後41）。全国優勝は甲子園本戦の実プレイ接続まで実ゲームでは発火しない。
    public double FameKoshienAppearance { get; init; } = 30.0;
    public double FameNationalChampion { get; init; } = 45.0;
    public double FamePerWin { get; init; } = 4.0;
    public double FameDecay { get; init; } = 0.80;

    // --- 番狂わせ連動の名声変動（issue #170, 設計書04 §1.2） ---
    // FamePerWin は「勝った事実」への一律加算のまま。ここは勝敗に加えて「相手との Tier 格差」だけを見る
    // 追加項で、順当な結果には効かない＝二重計上にならない（金星/取りこぼしのボーナス・ペナルティ）。
    /// <summary>格上に勝った時、Tier 格差1段あたりの名声上昇（金星）。</summary>
    public double FameUpsetWinPerTier { get; init; } = 3.0;
    /// <summary>格下に負けた時、Tier 格差1段あたりの名声低下（取りこぼし）。取りこぼしはやや重く。</summary>
    public double FameUpsetLossPerTier { get; init; } = 3.5;
    /// <summary>1シーズンの金星による名声上昇の上限（頭打ち＝短期で急上昇させない）。</summary>
    public double FameUpsetSeasonCap { get; init; } = 8.0;

    // --- 信頼度（設計書04 §1.2, 校内・転任でリセット） ---
    public double TrustReset { get; init; } = 50.0;
    public double TrustPerWin { get; init; } = 4.0;
    public double TrustKoshien { get; init; } = 20.0;
    /// <summary>1勝もできない不振シーズンの信頼度低下。</summary>
    public double TrustPoorSeasonPenalty { get; init; } = 12.0;

    // --- 資金（設計書04 §1.4, 万円） ---
    public double AnnualBudgetBase { get; init; } = 80.0;
    public double BudgetPerTrust { get; init; } = 0.8;

    // --- 年間固定支出（Issue #128, 万円）。既定は data/coefficients.yaml career.* と一致（issue #402）。
    // 以前は0で「実ゲームでは合宿・スカウトが無料」だった＝校正済み費用が sim にしか効いていなかった。 ---
    /// <summary>夏合宿の費用[万円]（設計書04 §4）。</summary>
    public double SummerCampCost { get; init; } = 20.0;
    /// <summary>冬合宿の費用[万円]（設計書04 §4）。</summary>
    public double WinterCampCost { get; init; } = 25.0;
    /// <summary>
    /// スカウト（新入生勧誘）の年間費用[万円]（設計書04 §4, #127連動）。年契約を持たない経路
    /// （キャリアシム）の固定費で、<b>年契約カタログのスカウト網 Lv1 と同額に保つ</b>
    /// （2026-08-11 の値上げで 15 → 30。片方だけ動かすと sim と実ゲームで支出がずれる）。
    /// </summary>
    public double ScoutCost { get; init; } = 30.0;

    // --- 施設基金（設計書04 §1.4「OB・後援会寄付」, 万円）。部費とは別会計で、建物だけに使う。
    // 現実の高校でも寮・室内練習場・ナイターは部費でなく学校法人の設備投資と後援会の寄付で建つ。
    // 既定を全て0にすると施設基金が一切増えない＝従来（部費で建物を買う）とは別世界になるので、
    // 実ゲームの既定として稼働する値を入れる。 ---
    /// <summary>
    /// 学校の設備投資[万円/年]。実績によらず毎年入る土台。名声由来の寄付を主役にするため小さく置く
    /// （名声10で寄付200万＝土台の2倍、名声100で2000万＝20倍）。
    /// </summary>
    public double FacilityFundSchoolBase { get; init; } = 100.0;
    /// <summary>後援会・OB寄付の名声1あたりの年額[万円]（名声に比例。名声100で年2000万）。</summary>
    public double FacilityFundDonationPerFame { get; init; } = 20.0;
    /// <summary>甲子園出場の年に後援会から入る一時金[万円]。</summary>
    public double FacilityFundKoshienBonus { get; init; } = 1000.0;

    // --- 甲子園出場が決まった日の激励金（設計書05 §1.1a・OB/卒業生の募金） ---
    // 上の FacilityFundKoshienBonus（施設基金・年度末に無言で入る）とは<b>別の財布・別のタイミング</b>。
    // こちらは**部費**＝遠征・宿泊・応援バスに使う運営費で、出場が決まったその場でイベントとして見える形で入る
    // （オーナー決定 2026-08-21）。二重計上ではない（建物の金と遠征の金は現実でも別勘定）。
    /// <summary>甲子園出場が決まった日にOB・卒業生から入る激励金の土台[万円]。</summary>
    public double KoshienBerthDonationBase { get; init; } = 120.0;
    /// <summary>同・名声1あたりの上乗せ[万円]（名声100で +150万＝年間予算の2倍強）。</summary>
    public double KoshienBerthDonationPerFame { get; init; } = 1.5;
    /// <summary>全国優勝の年に入る一時金[万円]（甲子園出場ぶんとは別に上乗せ）。</summary>
    public double FacilityFundNationalTitleBonus { get; init; } = 5000.0;

    // --- 赴任校（教員監督は弱小校に飛ばされがち） ---
    public double NewSchoolStrengthMean { get; init; } = 34.0;
    public double NewSchoolStrengthSd { get; init; } = 6.0;
    /// <summary>フリー監督が選べる好条件校の強さ。</summary>
    public double FreeChoiceSchoolStrength { get; init; } = 48.0;
}
