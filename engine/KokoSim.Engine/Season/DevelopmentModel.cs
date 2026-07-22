using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>
/// 1週間の練習→能力成長（設計書02 §5.1）。
/// Δexp = 基礎値 × 施設係数 × (1+指導力×0.005) × 性格係数 × 成長段階係数 × 合宿倍率。
/// 必要exp(v→v+1) = 100×1.05^v、才能上限で停止。乱数を使わない決定論。
/// </summary>
public static class DevelopmentModel
{
    /// <summary>
    /// 1週間の練習を「練習時間配分（複数メニュー×分）」で適用する（設計書03 §3.1）。
    /// 各メニューの成長は common × (配分分 ÷ 基準週時間) に線形スケール。休養メニューは効果なし。
    /// 1メニューに基準週フル配分すると TrainWeek と厳密一致（後方互換）。乱数を使わない決定論。
    /// </summary>
    public static void TrainWeekPlan(
        DevelopingPlayer p,
        IReadOnlyList<MenuAllocation> allocations,
        int referenceWeekMinutes,
        int stageIndex,
        double campMultiplier,
        GrowthStageTable stages,
        TrainingCoefficients c,
        Players.SkillCoefficients? skills = null,
        Players.PersonalityCoefficients? personalities = null)
    {
        // 怪我中は練習不可（設計書03 §3.5）: 全メニュー効果なし。
        if (p.Injury != Players.InjurySeverity.None) return;

        var stageCoef = stages.Coefficient(p.GrowthType, stageIndex);
        // ②素直さ（性格, 設計書01 §1.1）: 指導寄与を倍率補正。personalities=null または Normal は 1.0＝従来と一致。
        var receptivity = personalities?.Profile(p.Personality).CoachingReceptivity ?? 1.0;
        var coachingFactor = 1.0 + c.CoachingLevel * c.CoachingSlope * receptivity;

        var skillExp = 1.0;
        if (skills is not null)
        {
            if (p.Skills.Has(Players.Skill.Diligent)) skillExp *= skills.DiligentExpFactor;
            if (p.Skills.Has(Players.Skill.Lazy)) skillExp *= skills.LazyExpFactor;
        }

        var common = c.BaseMainExp * c.FacilityCoef * coachingFactor
                     * p.PersonalityFactor * stageCoef * campMultiplier * skillExp;

        var refMinutes = (double)referenceWeekMinutes;
        foreach (var (menu, minutes) in allocations)
        {
            if (minutes <= 0) continue;
            if (menu == TrainingMenu.Rest) continue;

            var scale = minutes / refMinutes;
            var eff = TrainingMenus.Effects(menu);
            if (eff.Main is { } mainAbility) ApplyExp(p, mainAbility, common * scale, c);
            foreach (var subAbility in eff.Subs) ApplyExp(p, subAbility, common * scale * c.SubFactor, c);
            foreach (var (pos, weight) in eff.Aptitudes) ApplyAptitudeExp(p, pos, common * scale * weight, c);
        }
    }

    public static void TrainWeek(
        DevelopingPlayer p,
        TrainingMenu menu,
        int stageIndex,
        double campMultiplier,
        GrowthStageTable stages,
        TrainingCoefficients c,
        Players.SkillCoefficients? skills = null,
        Players.PersonalityCoefficients? personalities = null)
    {
        if (menu == TrainingMenu.Rest) return;

        // 怪我中は練習不可（設計書03 §3.5: リハビリ/休養に限定）。
        if (p.Injury != Players.InjurySeverity.None) return;

        var stageCoef = stages.Coefficient(p.GrowthType, stageIndex);
        // ②素直さ（性格, 設計書01 §1.1）: 指導寄与を倍率補正。personalities=null または Normal は 1.0＝従来と一致。
        var receptivity = personalities?.Profile(p.Personality).CoachingReceptivity ?? 1.0;
        var coachingFactor = 1.0 + c.CoachingLevel * c.CoachingSlope * receptivity;

        // 体質・成長系スキル（設計書10）: 練習熱心/サボり癖は経験値効率に作用。
        // スキルなし（skills=null または未保有）なら倍率1.0で従来と完全一致。
        var skillExp = 1.0;
        if (skills is not null)
        {
            if (p.Skills.Has(Players.Skill.Diligent)) skillExp *= skills.DiligentExpFactor;
            if (p.Skills.Has(Players.Skill.Lazy)) skillExp *= skills.LazyExpFactor;
        }

        var eff = TrainingMenus.Effects(menu);
        var common = c.BaseMainExp * c.FacilityCoef * coachingFactor
                     * p.PersonalityFactor * stageCoef * campMultiplier * skillExp;

        if (eff.Main is { } mainAbility) ApplyExp(p, mainAbility, common, c);
        foreach (var subAbility in eff.Subs) ApplyExp(p, subAbility, common * c.SubFactor, c);
        // 守備位置適性（設計書01 §1.1）: 守備分野の伸びしろを乗せる。
        foreach (var (pos, weight) in eff.Aptitudes) ApplyAptitudeExp(p, pos, common * weight, c);
    }

    // internal: 実戦成長（MatchGrowthModel, Q8）が走塁判断へ同じ式でexpを注ぐため。
    internal static void ApplyExp(DevelopingPlayer p, AbilityKind ability, double delta, TrainingCoefficients c)
    {
        if (p.Level(ability) >= p.Cap(ability)) return;

        // 伸びしろ係数（分野別成長効率倍率, 設計書02 §5.1）。既定1.0では従来と同一。
        p.AddExp(ability, delta * p.GrowthMultiplier(ability));
        while (p.Level(ability) < p.Cap(ability))
        {
            var required = c.RequiredExp(p.Level(ability));
            if (p.Exp(ability) < required) break;
            p.ConsumeExp(ability, required);
            p.IncrementLevel(ability);
        }

        // 上限到達時は余剰expを捨てる。
        if (p.Level(ability) >= p.Cap(ability))
        {
            p.ConsumeExp(ability, p.Exp(ability));
        }
    }

    /// <summary>守備位置適性の育成（能力値と同じ必要exp曲線＋守備分野の伸びしろ）。設計書01 §1.1 / 03 §3.1。</summary>
    private static void ApplyAptitudeExp(DevelopingPlayer p, Match.Field.FieldPosition pos, double delta, TrainingCoefficients c)
    {
        if (p.Aptitude(pos) >= p.AptitudeCap(pos)) return;

        p.AddAptitudeExp(pos, delta * p.DefenseGrowth);
        while (p.Aptitude(pos) < p.AptitudeCap(pos))
        {
            // 適性は能力レベルより緩い必要exp（ユーティリティを現実的な年数で作れるように・守備適性 未決1）。
            var required = c.RequiredExp(p.Aptitude(pos)) * c.AptitudeRequiredExpFactor;
            if (p.AptitudeExp(pos) < required) break;
            p.ConsumeAptitudeExp(pos, required);
            p.IncrementAptitude(pos);
        }

        if (p.Aptitude(pos) >= p.AptitudeCap(pos))
        {
            p.ConsumeAptitudeExp(pos, p.AptitudeExp(pos));
        }
    }
}
