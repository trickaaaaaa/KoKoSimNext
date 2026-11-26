using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Career;

/// <summary>監督成長イベントの種別（設計書04 §1.1b）。キャリアの節目で能力がぐっと動く。</summary>
public enum ManagerGrowthEventType
{
    KoshienExperience,  // 甲子園経験: 采配が大きく上昇（頂点の空気を知る）
    FamousRivalLesson,  // 名将との練習試合: 采配 or 指導力上昇（教えを乞う）
    LessonFromBigLoss,  // 大敗からの学び: 弱点分野の指導力上昇
    CoachingSeminar,    // 指導者講習会（オフ・費用と引き換え）
    ProObVisit,         // 教え子（プロ入りした自分の教え子）の訪問: 采配・育成眼上昇
    MentorCoach,        // 名伯楽との出会い（稀）: 出会った年から MentorYears 年、毎年の自然増に全分野 +MentorAnnualBoost
    MentorFarewell,     // 名伯楽が去る: 継続ボーナスの最後の年（issue #1679）
}

/// <summary>指導力の分野（打撃／投手／守備走塁）。講習会のテーマ・大敗で気づく弱点分野の表示に使う。</summary>
public enum CoachingArea { Batting, Pitching, Defense }

/// <summary>
/// 発火した監督成長イベント（UIフィード用の観測データ）。<paramref name="Area"/> は伸びた指導力の分野
/// （大敗からの学び＝弱点分野。それ以外は null）。会話に「どの分野を学んだか」を出すのに使う。
/// </summary>
public sealed record ManagerGrowthNotice(ManagerGrowthEventType Type, double Amount, CoachingArea? Area = null);

/// <summary>監督成長イベントの係数（設計書04 §1.1b）。🟡確率・上昇量は検証で調整。</summary>
public sealed record ManagerGrowthCoefficients
{
    public double KoshienTacticalBoost { get; init; } = 5.0;      // 甲子園経験（出場年に確定発火）
    public double FamousRivalProb { get; init; } = 0.15;          // 文脈: 強さティアS/Aの学校と対戦した年のみ
    public double FamousRivalBoost { get; init; } = 3.0;
    public double BigLossProb { get; init; } = 0.35;              // 文脈: その年に大差負けがあった年のみ
    public double BigLossBoost { get; init; } = 3.0;
    public double SeminarBoost { get; init; } = 2.0;
    public double SeminarCost { get; init; } = 20.0;              // 万円
    public double ProObVisitBaseProb { get; init; } = 0.08;       // 名声で微増
    public double ProObVisitFameSlope { get; init; } = 0.001;
    public double ProObVisitBoost { get; init; } = 2.0;
    public double MentorProb { get; init; } = 0.02;               // 名伯楽（稀）
    public int MentorYears { get; init; } = 3;                    // 出会った年を含めて何年効くか（issue #1679）
    public double MentorAnnualBoost { get; init; } = 1.0;         // 効いている年ごとの全分野の上乗せ
    public double Cap { get; init; } = 99.0;
}

/// <summary>
/// 監督成長イベント（設計書04 §1.1b）。自然増（CareerEngine.ApplyResults）に加わる「節目の跳ね」。
/// 狙って連打するものではなく、プレイの節目で訪れる（発火は年次・文脈条件つき）。
/// </summary>
public static class ManagerGrowthEvents
{
    /// <summary>
    /// 年次イベント判定。効果を Manager に適用し、発火一覧を返す。
    /// </summary>
    /// <param name="hasProProtege">
    /// 監督が直接育ててプロ入りした教え子（教え子名鑑・<see cref="Draft.DraftHistory"/>）が1人以上いるか（issue #1615）。
    /// false なら教え子の訪問は起こさない。<b>乱数は今までどおり引いてから捨てる</b>＝引く回数を変えると
    /// 後ろの名伯楽の抽選がずれる。既定 true＝名鑑を持たないキャリアシムは従来どおり（帯不変）。
    /// </param>
    /// <param name="hadBlowoutLoss">
    /// その年に大差負け（<c>FormCoefficients.MatchBlowoutLossMargin</c> 点差以上）が1試合でもあったか（issue #1483）。
    /// 大敗からの学びの条件。null＝試合ごとの得点差を持たない呼び出し（キャリアシム）は、従来どおり
    /// 未勝利の年を大敗の年とみなす＝帯不変。
    /// </param>
    /// <param name="facedPowerhouse">
    /// その年に強さティア S/A の学校（<see cref="IsPowerhouse"/>）と戦ったか（公式戦・練習試合を問わない・issue #1483）。
    /// 名将との練習試合の条件。既定 true＝キャリアシムは常に対戦あり＝帯不変。
    /// </param>
    /// <param name="attendSeminar">
    /// 指導者講習会に参加するか（issue #1483）。参加すれば確定で効果・費用は <see cref="ManagerGrowthCoefficients.SeminarCost"/>
    /// （資金が足りなければ参加できない）。抽選は廃止。既定 true＝キャリアシムは「参加する」固定。実ゲームは冬の
    /// オフに本人へ聞いて <see cref="AttendSeminar"/> でその場に適用するので false を渡す（二重に適用しない）。
    /// </param>
    public static IReadOnlyList<ManagerGrowthNotice> Yearly(
        Manager m, bool reachedKoshien, int wins, IRandomSource rng, ManagerGrowthCoefficients c,
        bool hasProProtege = true, bool? hadBlowoutLoss = null, bool facedPowerhouse = true,
        bool attendSeminar = true)
    {
        var fired = new List<ManagerGrowthNotice>();

        // 甲子園経験（出場した年に確定。采配が大きく伸びる）。
        if (reachedKoshien)
        {
            m.TacticalSense = Math.Min(c.Cap, m.TacticalSense + c.KoshienTacticalBoost);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.KoshienExperience, c.KoshienTacticalBoost));
        }

        // 大敗からの学び（文脈: その年に大差負けがあった年のみ。弱点分野が伸びる）。
        if ((hadBlowoutLoss ?? wins == 0) && MathUtil.Chance(c.BigLossProb, rng))
        {
            var area = BoostWeakest(m, c.BigLossBoost, c.Cap);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.LessonFromBigLoss, c.BigLossBoost, area));
        }

        // 名将との練習試合（文脈: 強さティアS/Aの学校と戦った年のみ。采配が伸びる）。乱数は条件によらず引く。
        if (MathUtil.Chance(c.FamousRivalProb, rng) && facedPowerhouse)
        {
            m.TacticalSense = Math.Min(c.Cap, m.TacticalSense + c.FamousRivalBoost);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.FamousRivalLesson, c.FamousRivalBoost));
        }

        // 指導者講習会（参加するなら確定。抽選なし）。
        if (attendSeminar && CanAttendSeminar(m, c))
        {
            var area = AttendSeminar(m, SeminarTheme(rng), c);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.CoachingSeminar, c.SeminarBoost, area));
        }

        // 教え子（プロ入りした自分の教え子）の訪問（名声で微増）。教え子がいなければ起こさない（乱数は引く）。
        var proVisitRoll = MathUtil.Chance(c.ProObVisitBaseProb + m.Fame * c.ProObVisitFameSlope, rng);
        if (proVisitRoll && hasProProtege)
        {
            m.TalentEye = Math.Min(c.Cap, m.TalentEye + c.ProObVisitBoost);
            m.TacticalSense = Math.Min(c.Cap, m.TacticalSense + c.ProObVisitBoost * 0.5);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.ProObVisit, c.ProObVisitBoost));
        }

        // 名伯楽との出会い（稀）。出会った年から MentorYears 年、毎年の自然増に全分野を上乗せする（issue #1679）。
        // 効いている間は出会わない（乱数は条件によらず引く＝後ろの抽選がずれない）。出会った年の分はここで足す。
        if (MathUtil.Chance(c.MentorProb, rng) && m.MentorYearsLeft == 0 && c.MentorYears > 0)
        {
            m.MentorYearsLeft = c.MentorYears;
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.MentorCoach, c.MentorAnnualBoost));
            if (ApplyMentorYear(m, c))
                fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.MentorFarewell, c.MentorAnnualBoost));
        }

        return fired;
    }

    /// <summary>
    /// 名伯楽が付いている年の上乗せ（毎年の自然増の直後に1回）。全分野の指導力を <see cref="ManagerGrowthCoefficients.MentorAnnualBoost"/>
    /// 上げて残り年数を1減らす。付いていなければ何もしない。残りが0になった（＝コーチが去る）なら true。
    /// </summary>
    public static bool ApplyMentorYear(Manager m, ManagerGrowthCoefficients c)
    {
        if (m.MentorYearsLeft <= 0) return false;
        m.CoachingBatting = Math.Min(c.Cap, m.CoachingBatting + c.MentorAnnualBoost);
        m.CoachingPitching = Math.Min(c.Cap, m.CoachingPitching + c.MentorAnnualBoost);
        m.CoachingDefense = Math.Min(c.Cap, m.CoachingDefense + c.MentorAnnualBoost);
        m.MentorYearsLeft--;
        return m.MentorYearsLeft == 0;
    }

    /// <summary>強さティア S/A の学校か（名将との練習試合の条件・issue #1483）。</summary>
    public static bool IsPowerhouse(Nation.Tier tier) => tier is Nation.Tier.S or Nation.Tier.A;

    /// <summary>講習会に参加できるか（費用を払えるか）。</summary>
    public static bool CanAttendSeminar(Manager m, ManagerGrowthCoefficients c) => m.Funds >= c.SeminarCost;

    /// <summary>今年の講習会のテーマ（分野）。乱数を1つ引く＝実ゲームは年度ごとの独立ストリームで引く。</summary>
    public static CoachingArea SeminarTheme(IRandomSource rng) => (CoachingArea)rng.NextInt(0, 3);

    /// <summary>
    /// 講習会に参加する（費用を引き、テーマの分野の指導力が <see cref="ManagerGrowthCoefficients.SeminarBoost"/> 上がる）。
    /// 実ゲームでは冬のオフにマネージャーが聞いて「参加する」を選んだ時点で呼ぶ。分野を返す。
    /// </summary>
    public static CoachingArea AttendSeminar(Manager m, CoachingArea area, ManagerGrowthCoefficients c)
    {
        m.Funds -= c.SeminarCost;
        Boost(m, area, c.SeminarBoost, c.Cap);
        return area;
    }

    private static CoachingArea BoostWeakest(Manager m, double amount, double cap)
    {
        var area = m.CoachingBatting <= m.CoachingPitching && m.CoachingBatting <= m.CoachingDefense
            ? CoachingArea.Batting
            : m.CoachingPitching <= m.CoachingDefense ? CoachingArea.Pitching : CoachingArea.Defense;
        Boost(m, area, amount, cap);
        return area;
    }

    private static void Boost(Manager m, CoachingArea area, double amount, double cap)
    {
        switch (area)
        {
            case CoachingArea.Batting: m.CoachingBatting = Math.Min(cap, m.CoachingBatting + amount); break;
            case CoachingArea.Pitching: m.CoachingPitching = Math.Min(cap, m.CoachingPitching + amount); break;
            default: m.CoachingDefense = Math.Min(cap, m.CoachingDefense + amount); break;
        }
    }
}
