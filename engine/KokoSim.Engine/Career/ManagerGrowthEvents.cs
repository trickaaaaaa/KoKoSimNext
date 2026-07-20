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
    ProObVisit,         // プロOBの訪問: 采配・育成眼上昇
    MentorCoach,        // 名伯楽との出会い（稀）: 全分野ボーナス
}

/// <summary>発火した監督成長イベント（UIフィード用の観測データ）。</summary>
public sealed record ManagerGrowthNotice(ManagerGrowthEventType Type, double Amount);

/// <summary>監督成長イベントの係数（設計書04 §1.1b）。🟡確率・上昇量は検証で調整。</summary>
public sealed record ManagerGrowthCoefficients
{
    public double KoshienTacticalBoost { get; init; } = 5.0;      // 甲子園経験（出場年に確定発火）
    public double FamousRivalProb { get; init; } = 0.15;
    public double FamousRivalBoost { get; init; } = 3.0;
    public double BigLossProb { get; init; } = 0.35;              // 文脈: 未勝利の年のみ
    public double BigLossBoost { get; init; } = 3.0;
    public double SeminarProb { get; init; } = 0.25;              // オフ講習会
    public double SeminarBoost { get; init; } = 2.0;
    public double SeminarCost { get; init; } = 20.0;              // 万円
    public double ProObVisitBaseProb { get; init; } = 0.08;       // 名声で微増
    public double ProObVisitFameSlope { get; init; } = 0.001;
    public double ProObVisitBoost { get; init; } = 2.0;
    public double MentorProb { get; init; } = 0.02;               // 名伯楽（稀）
    public double MentorBoost { get; init; } = 2.0;
    public double Cap { get; init; } = 99.0;
}

/// <summary>
/// 監督成長イベント（設計書04 §1.1b）。自然増（CareerEngine.ApplyResults）に加わる「節目の跳ね」。
/// 狙って連打するものではなく、プレイの節目で訪れる（発火は年次・文脈条件つき）。
/// </summary>
public static class ManagerGrowthEvents
{
    /// <summary>年次イベント判定。効果を Manager に適用し、発火一覧を返す。</summary>
    public static IReadOnlyList<ManagerGrowthNotice> Yearly(
        Manager m, bool reachedKoshien, int wins, IRandomSource rng, ManagerGrowthCoefficients c)
    {
        var fired = new List<ManagerGrowthNotice>();

        // 甲子園経験（出場した年に確定。采配が大きく伸びる）。
        if (reachedKoshien)
        {
            m.TacticalSense = Math.Min(c.Cap, m.TacticalSense + c.KoshienTacticalBoost);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.KoshienExperience, c.KoshienTacticalBoost));
        }

        // 大敗からの学び（文脈: 未勝利の年のみ。弱点分野が伸びる）。
        if (wins == 0 && MathUtil.Chance(c.BigLossProb, rng))
        {
            BoostWeakest(m, c.BigLossBoost, c.Cap);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.LessonFromBigLoss, c.BigLossBoost));
        }

        // 名将との練習試合。
        if (MathUtil.Chance(c.FamousRivalProb, rng))
        {
            m.TacticalSense = Math.Min(c.Cap, m.TacticalSense + c.FamousRivalBoost);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.FamousRivalLesson, c.FamousRivalBoost));
        }

        // 指導者講習会（オフ・資金があれば）。
        if (m.Funds >= c.SeminarCost && MathUtil.Chance(c.SeminarProb, rng))
        {
            m.Funds -= c.SeminarCost;
            BoostRandomCoaching(m, c.SeminarBoost, c.Cap, rng);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.CoachingSeminar, c.SeminarBoost));
        }

        // プロOBの訪問（名声で微増）。
        if (MathUtil.Chance(c.ProObVisitBaseProb + m.Fame * c.ProObVisitFameSlope, rng))
        {
            m.TalentEye = Math.Min(c.Cap, m.TalentEye + c.ProObVisitBoost);
            m.TacticalSense = Math.Min(c.Cap, m.TacticalSense + c.ProObVisitBoost * 0.5);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.ProObVisit, c.ProObVisitBoost));
        }

        // 名伯楽との出会い（稀・全分野）。
        if (MathUtil.Chance(c.MentorProb, rng))
        {
            m.CoachingBatting = Math.Min(c.Cap, m.CoachingBatting + c.MentorBoost);
            m.CoachingPitching = Math.Min(c.Cap, m.CoachingPitching + c.MentorBoost);
            m.CoachingDefense = Math.Min(c.Cap, m.CoachingDefense + c.MentorBoost);
            fired.Add(new ManagerGrowthNotice(ManagerGrowthEventType.MentorCoach, c.MentorBoost));
        }

        return fired;
    }

    private static void BoostWeakest(Manager m, double amount, double cap)
    {
        if (m.CoachingBatting <= m.CoachingPitching && m.CoachingBatting <= m.CoachingDefense)
            m.CoachingBatting = Math.Min(cap, m.CoachingBatting + amount);
        else if (m.CoachingPitching <= m.CoachingDefense)
            m.CoachingPitching = Math.Min(cap, m.CoachingPitching + amount);
        else
            m.CoachingDefense = Math.Min(cap, m.CoachingDefense + amount);
    }

    private static void BoostRandomCoaching(Manager m, double amount, double cap, IRandomSource rng)
    {
        switch (rng.NextInt(0, 3))
        {
            case 0: m.CoachingBatting = Math.Min(cap, m.CoachingBatting + amount); break;
            case 1: m.CoachingPitching = Math.Min(cap, m.CoachingPitching + amount); break;
            default: m.CoachingDefense = Math.Min(cap, m.CoachingDefense + amount); break;
        }
    }
}
