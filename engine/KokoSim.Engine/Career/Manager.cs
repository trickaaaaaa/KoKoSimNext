namespace KokoSim.Engine.Career;

/// <summary>監督の身分（設計書04 §1.3）。</summary>
public enum ManagerStatus
{
    Teacher, // 教員監督（毎年強制転任判定）
    Free,    // フリー監督（残留自由・オファー選択制）
}

/// <summary>
/// 監督メタ（設計書04 §1）。キャリアを通じて持ち越す唯一の資産。
/// 分野別指導力・育成眼・采配は転任で持ち越し、信頼度は赴任先ごとにリセットする。
/// </summary>
public sealed class Manager
{
    public string Name { get; init; } = "監督";
    public ManagerStatus Status { get; set; } = ManagerStatus.Teacher;

    // --- 分野別指導力（設計書04 §1.1, 練習効率係数に接続） ---
    public double CoachingBatting { get; set; } = 20;
    public double CoachingPitching { get; set; } = 20;
    public double CoachingDefense { get; set; } = 20;
    public double TalentEye { get; set; } = 20;   // 育成眼
    public double TacticalSense { get; set; } = 20; // 采配

    // --- 二軸（設計書04 §1.2）。名声は持ち越し、信頼度は転任でリセット ---
    public double Fame { get; set; } = 10;
    public double Trust { get; set; } = 50;

    /// <summary>資金[万円]（設計書04 §1.4）。</summary>
    public double Funds { get; set; } = 100;

    /// <summary>通算赴任年数。</summary>
    public int CareerYears { get; set; }

    /// <summary>甲子園出場回数（フリー化資格判定）。</summary>
    public int KoshienAppearances { get; set; }

    /// <summary>指導力の平均（チーム総合力への寄与目安）。</summary>
    public double AverageCoaching => (CoachingBatting + CoachingPitching + CoachingDefense) / 3.0;
}
