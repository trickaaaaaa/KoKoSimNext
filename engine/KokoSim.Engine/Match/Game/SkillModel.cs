using System;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
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

        // 目慣れ（旧 尻上がり・野手）: 打席を重ねるほど当たりが出る（試合内の非線形）。
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
    /// 投手の実効能力へスキルを適用。priorBf=当該試合でこの投手が既に対戦した打者数（尻上がり/打者一巡/火消し用）。
    /// inning=いまのイニング（立ち上がり難用。0＝不明＝効かせない）、inRelief=途中登板の投手か（火消し用）。
    /// </summary>
    public static PitcherAttributes ApplyPitcher(PitcherAttributes p, SkillSet skills, int priorBf, SkillCoefficients c,
        int inning = 0, bool inRelief = false)
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
        // 立ち上がり難（issue #907）: 1〜2回は制球が落ちる。球威側は PlayMods。
        if (IsShakyStart(skills, inning, c))
            control -= c.ShakyStartControlPenalty;
        // 火消し（issue #907）: 途中登板の直後の打者に制球が上乗せ。球威側は PlayMods。
        if (IsFiremanWindow(skills, priorBf, inRelief, c))
            control += c.FiremanControlBonus;

        if (control == p.Control) return p;
        return p with { Control = Clamp(control) };
    }

    /// <summary>
    /// 打者・投手のスキルから1打席の挙動補正を合成（行動特性・球質のみ）。
    /// inning/inRelief は立ち上がり難・火消し用（<see cref="ApplyPitcher"/> と同じ意味）、runnersOn はつなぎの打撃用。
    /// </summary>
    public static SkillPlayMods PlayMods(SkillSet batterSkills, SkillSet pitcherSkills, int priorBf, SkillCoefficients c,
        int inning = 0, bool inRelief = false, bool runnersOn = false)
    {
        double firstPitch = 0.0, bearingPull = 1.0, foul = 1.0, stuff = 0.0;
        double fastball = 0.0, breakingContact = 0.0, breakingDiscipline = 0.0;
        double oppositeExit = 0.0, bearingShift = 0.0, launchShift = 0.0, qualityShift = 0.0;

        if (batterSkills.Has(Skill.FirstPitchSwinger)) firstPitch = c.FirstPitchSwingProb;
        if (batterSkills.Has(Skill.Grinder)) foul = c.GrinderFoulFactor;
        // 流し打ち（旧 広角打法を置き換え・issue #907）: 引っ張りに偏らない方向の質＋逆方向の打球が強い。
        if (batterSkills.Has(Skill.OppositeFieldHitter))
        {
            bearingPull = c.OppositeFieldPullFactor;
            oppositeExit = c.OppositeFieldExitBonus;
        }
        // 速球打ち・変化球打ち（issue #907）: 球種が決まった球ごとに AtBatSession が実効能力へ足す。
        if (batterSkills.Has(Skill.FastballHitter)) fastball = c.FastballHitterContactBonus;
        if (batterSkills.Has(Skill.BreakingBallHitter))
        {
            breakingContact = c.BreakingBallHitterContactBonus;
            breakingDiscipline = c.BreakingBallHitterDisciplineBonus;
        }
        // つなぎの打撃（issue #907）: 走者がいる打席だけ、右方向・低い打球に寄せ、打球の質を少し落とす。
        if (runnersOn && batterSkills.Has(Skill.TeamBatting))
        {
            bearingShift = c.TeamBattingBearingShiftDeg;
            launchShift = c.TeamBattingLaunchShiftDeg;
            qualityShift = c.TeamBattingQualityShift;
        }
        if (IsShakyStart(pitcherSkills, inning, c)) stuff -= c.ShakyStartStuffPenalty;
        if (IsFiremanWindow(pitcherSkills, priorBf, inRelief, c)) stuff += c.FiremanStuffBonus;

        if (pitcherSkills.Has(Skill.DeceptiveBall)) stuff += c.DeceptiveBallStuffBonus;
        if (pitcherSkills.Has(Skill.EffectivelyWild)) stuff += c.EffectivelyWildStuffBonus;
        if (pitcherSkills.Has(Skill.SecondTimeThrough) && priorBf >= c.SecondTimeThroughBattersFaced)
            stuff -= c.SecondTimeThroughStuffPenalty;
        if (pitcherSkills.Has(Skill.Monster)) stuff += c.MonsterStuffBonus;

        var mods = new SkillPlayMods(firstPitch, bearingPull, foul, stuff);
        if (fastball == 0.0 && breakingContact == 0.0 && breakingDiscipline == 0.0 && oppositeExit == 0.0
            && bearingShift == 0.0 && launchShift == 0.0 && qualityShift == 0.0)
            return mods;
        return mods with
        {
            FastballContactBonus = fastball,
            BreakingContactBonus = breakingContact,
            BreakingDisciplineBonus = breakingDiscipline,
            BattedBall = new BattedBallSkillMods(
                oppositeExit, oppositeExit != 0.0 ? c.OppositeFieldMinBearingDeg : 0.0, bearingShift, launchShift, qualityShift),
        };
    }

    /// <summary>立ち上がり難が効く回か（1〜<see cref="SkillCoefficients.ShakyStartInnings"/> 回。inning=0 は不明＝効かせない）。</summary>
    private static bool IsShakyStart(SkillSet skills, int inning, SkillCoefficients c)
        => inning >= 1 && inning <= c.ShakyStartInnings && skills.Has(Skill.ShakyStart);

    /// <summary>火消しが効く打席か（途中登板で、対戦打者数が <see cref="SkillCoefficients.FiremanBatters"/> 未満）。</summary>
    private static bool IsFiremanWindow(SkillSet skills, int priorBf, bool inRelief, SkillCoefficients c)
        => inRelief && priorBf < c.FiremanBatters && skills.Has(Skill.Fireman);

    /// <summary>
    /// クイック○×（issue #907）: 盗塁の守備側の秒（<c>BaserunningCoefficients.PitcherQuickSeconds</c>）へ足す投手ごとの差[s]。
    /// ○は負（速い＝盗塁されにくい）、×は正。スキルなしは0。
    /// </summary>
    public static double QuickDeliveryOffsetSeconds(SkillSet pitcherSkills, SkillCoefficients c)
    {
        var d = 0.0;
        if (pitcherSkills.Has(Skill.QuickDelivery)) d -= c.QuickDeliverySeconds;
        if (pitcherSkills.Has(Skill.SlowDelivery)) d += c.SlowDeliverySeconds;
        return d;
    }

    /// <summary>
    /// 火消し（issue #907）: 継投AIがリリーフを選ぶときの比較値への加算。火消しを持たなければ0。
    /// </summary>
    public static double ReliefScoreBonus(SkillSet skills, SkillCoefficients c)
        => skills.Has(Skill.Fireman) ? c.FiremanReliefScoreBonus : 0.0;

    /// <summary>打者のチャンス○×（得点圏で効く。設計書10 §0.1）。両方持つ場合は打ち消し合う。</summary>
    public static PressureTrait BatterPressureTrait(SkillSet skills)
        => TraitOf(skills.Has(Skill.ClutchHitter), skills.Has(Skill.ChokeHitter));

    /// <summary>投手のピンチ○×（得点圏 or 終盤の接戦で効く。設計書10 §0.1）。両方持つ場合は打ち消し合う。</summary>
    public static PressureTrait PitcherPressureTrait(SkillSet skills)
        => TraitOf(skills.Has(Skill.ClutchPitcher), skills.Has(Skill.ChokePitcher));

    private static PressureTrait TraitOf(bool clutch, bool choke)
        => clutch == choke ? PressureTrait.None : clutch ? PressureTrait.Clutch : PressureTrait.Choke;

    /// <summary>ムラっけ: 当日の出来（day-form）の振れ幅の拡大倍率。スキルなしは1.0。</summary>
    public static double DayFormVarianceFactor(SkillSet skills, SkillCoefficients c)
        => skills.Has(Skill.Streaky) ? c.StreakyVarianceFactor : 1.0;

    private static int Clamp(double v) => Math.Clamp((int)Math.Round(v), 1, 100);
}
