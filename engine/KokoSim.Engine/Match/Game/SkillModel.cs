using System;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// スキルの試合内効果（設計書10）。数値補正は実効能力へ、行動特性・球質は <see cref="SkillPlayMods"/> へ落とす。
/// すべて補正係数方式で打席解決パイプラインの構造は不変（不変条件#1）。
/// スキルなし（空集合）の選手には一切作用せず、既存の統計帯を変えない。
/// </summary>
public static class SkillModel
{
    /// <summary>
    /// 打者の実効能力へスキルを適用。priorPa=当該試合でこの打者が既に立った打席数（尻上がり用）。
    /// </summary>
    public static BatterAttributes ApplyBatter(BatterAttributes b, SkillSet skills, int priorPa, SkillCoefficients c)
    {
        if (skills.IsEmpty) return b;

        double contact = b.Contact;
        double power = b.Power;

        // 尻上がり: 打席を重ねるほど当たりが出る（試合内の非線形）。
        if (skills.Has(Skill.SlowStarterBat))
            contact += Math.Min(c.SlowStarterBatMaxBonus, priorPa * c.SlowStarterBatContactPerPa);
        // 怪物: 複数分野に確かな上乗せ。
        if (skills.Has(Skill.Monster))
        {
            contact += c.MonsterContactBonus;
            power += c.MonsterPowerBonus;
        }

        if (contact == b.Contact && power == b.Power) return b;
        return b with { Contact = Clamp(contact), Power = Clamp(power) };
    }

    /// <summary>
    /// 投手の実効能力へスキルを適用。priorBf=当該試合でこの投手が既に対戦した打者数（尻上がり/打者一巡用）。
    /// </summary>
    public static PitcherAttributes ApplyPitcher(PitcherAttributes p, SkillSet skills, int priorBf, SkillCoefficients c)
    {
        if (skills.IsEmpty) return p;

        double control = p.Control;

        // 尻上がり: イニングを追うごとに安定。
        if (skills.Has(Skill.SlowStarterPitch))
            control += Math.Min(c.SlowStarterPitchMaxBonus, priorBf * c.SlowStarterPitchControlPerBf);
        // 打者一巡: 3巡目以降に崩れやすい（対戦回数依存の癖・負）。
        if (skills.Has(Skill.SecondTimeThrough) && priorBf >= c.SecondTimeThroughBattersFaced)
            control -= c.SecondTimeThroughControlPenalty;
        // 荒れ球: 実効コントロールを下げる（四球増）。球威側の上乗せは PlayMods。
        if (skills.Has(Skill.EffectivelyWild))
            control -= c.EffectivelyWildControlPenalty;
        if (skills.Has(Skill.Monster))
            control += c.MonsterControlBonus;

        if (control == p.Control) return p;
        return p with { Control = Clamp(control) };
    }

    /// <summary>打者・投手のスキルから1打席の挙動補正を合成（行動特性・球質のみ）。</summary>
    public static SkillPlayMods PlayMods(SkillSet batterSkills, SkillSet pitcherSkills, int priorBf, SkillCoefficients c)
    {
        double firstPitch = 0.0, bearing = 1.0, foul = 1.0, stuff = 0.0;

        if (batterSkills.Has(Skill.FirstPitchSwinger)) firstPitch = c.FirstPitchSwingProb;
        if (batterSkills.Has(Skill.SprayHitter)) bearing = c.SprayBearingFactor;
        if (batterSkills.Has(Skill.Grinder)) foul = c.GrinderFoulFactor;

        if (pitcherSkills.Has(Skill.DeceptiveBall)) stuff += c.DeceptiveBallStuffBonus;
        if (pitcherSkills.Has(Skill.EffectivelyWild)) stuff += c.EffectivelyWildStuffBonus;
        if (pitcherSkills.Has(Skill.SecondTimeThrough) && priorBf >= c.SecondTimeThroughBattersFaced)
            stuff -= c.SecondTimeThroughStuffPenalty;
        if (pitcherSkills.Has(Skill.Monster)) stuff += c.MonsterStuffBonus;

        return new SkillPlayMods(firstPitch, bearing, foul, stuff);
    }

    /// <summary>ムラっけ: 当日の出来（day-form）の振れ幅の拡大倍率。スキルなしは1.0。</summary>
    public static double DayFormVarianceFactor(SkillSet skills, SkillCoefficients c)
        => skills.Has(Skill.Streaky) ? c.StreakyVarianceFactor : 1.0;

    private static int Clamp(double v) => Math.Clamp((int)Math.Round(v), 1, 100);
}
