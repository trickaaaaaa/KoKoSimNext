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

    /// <summary>
    /// 部費[万円]（設計書04 §1.4）。財布はこれ1つ（issue #1082 で施設基金を統合）。年度末に学校予算・
    /// 信頼度ぶん・学校の設備投資・後援会/OB寄付・甲子園/全国優勝の一時金が入り、施設（建物）・練習道具・
    /// 備品・合宿・年契約・遠征・練習試合すべてに使う。転任のときは一部だけ持ち越す。
    /// </summary>
    public double Funds { get; set; } = 100;

    /// <summary>通算赴任年数。</summary>
    public int CareerYears { get; set; }

    /// <summary>現在の学校での在任年数。転任のたび0にリセットされる（<see cref="CareerYears"/>と違い通算しない）。</summary>
    public int YearsAtSchool { get; set; }

    /// <summary>甲子園出場回数（フリー化資格判定）。</summary>
    public int KoshienAppearances { get; set; }

    /// <summary>
    /// 名伯楽（優秀なコーチ）が付いている残り年数（design-04 §1.1b・issue #1679）。0＝付いていない。
    /// 年度末の自然増のたびに全分野の指導力へ上乗せして1減る（<see cref="ManagerGrowthEvents.ApplyMentorYear"/>）。転任しても持ち越す。
    /// </summary>
    public int MentorYearsLeft { get; set; }

    /// <summary>指導力の平均（チーム総合力への寄与目安）。</summary>
    public double AverageCoaching => (CoachingBatting + CoachingPitching + CoachingDefense) / 3.0;

    // --- マネージャー（NPC）の入れ替わり（design-04 §1.3a・issue #1430） ---

    /// <summary>
    /// 今のマネージャーの立ち絵番号（1〜<see cref="Career.TeamManagerLifecycle.PortraitCount"/>・tm01〜tm10 に対応）。
    /// 抽選・入れ替えは <see cref="Career.TeamManagerLifecycle"/> だけが行う（不変条件#2＝Fork した専用ストリームで引く）。
    /// </summary>
    public int TeamManagerPortrait { get; set; } = 1;

    /// <summary>今のマネージャーの学年（1〜3）。3年の夏が終わると1年の新マネージャーに入れ替わる。</summary>
    public int TeamManagerGrade { get; set; } = 1;

    /// <summary>
    /// 入れ替わりの直後だけ意味を持つ、引退した側の立ち絵番号。引き継ぎイベント（<c>who: team_manager_outgoing</c>）の
    /// 立ち絵に使う。入れ替わりが起きていない間は古い値が残っていても実害はない（その役割はその会話でしか使わない）。
    /// </summary>
    public int OutgoingTeamManagerPortrait { get; set; }
}
