using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 生成時のスキル付与（設計書10 §3/§6）。才能・性格に応じて0〜数個。目玉は極稀、一部は隠し。
/// 独立ストリーム（Fork）で抽選し、能力ロール列を乱さない（既存の決定論テスト非破壊）。
/// </summary>
public static class SkillGenerator
{
    // 投手が持ちうる挙動系。
    private static readonly Skill[] PitchingPool =
    {
        Skill.SlowStarterPitch, Skill.SecondTimeThrough, Skill.EffectivelyWild, Skill.DeceptiveBall,
    };

    // 野手が持ちうる打撃・守備系。
    private static readonly Skill[] BattingPool =
    {
        Skill.SlowStarterBat, Skill.Streaky, Skill.SprayHitter, Skill.FirstPitchSwinger, Skill.Grinder,
        Skill.DoublePlayArtist, Skill.MasterCatcher,
    };

    // 全員が持ちうるチーム・体質系（負スキルは確率を下げる）。
    private static readonly Skill[] TeamPool =
    {
        Skill.Moodmaker, Skill.PracticeLeader, Skill.RoleModel,
    };

    private static readonly Skill[] ConstitutionPositive =
    {
        Skill.Diligent, Skill.Durable,
    };

    private static readonly Skill[] ConstitutionNegative =
    {
        Skill.Lazy, Skill.InjuryProne,
    };

    /// <summary>スキル集合を生成。leadership は精神的支柱の付与確率に影響（設計書09 §8）。</summary>
    public static SkillSet Generate(bool isPitcher, int leadership, IRandomSource rng, SkillCoefficients c)
    {
        var visible = new List<Skill>();
        var hidden = new List<Skill>();
        var count = 0;

        void TryAdd(Skill s, double prob)
        {
            if (count >= c.MaxSkillsPerPlayer) return;
            if (!MathUtil.Chance(prob, rng)) return;
            if (MathUtil.Chance(c.HiddenShare, rng)) hidden.Add(s);
            else visible.Add(s);
            count++;
        }

        // カテゴリ適合プール（順序固定＝決定論）。
        foreach (var s in isPitcher ? PitchingPool : BattingPool) TryAdd(s, c.CommonSkillProb);
        foreach (var s in ConstitutionPositive) TryAdd(s, c.CommonSkillProb);
        foreach (var s in TeamPool) TryAdd(s, c.CommonSkillProb);
        foreach (var s in ConstitutionNegative) TryAdd(s, c.CommonSkillProb * 0.5); // 負スキルは控えめ

        // 精神的支柱: 統率傾向が高い選手が持ちやすい（設計書09 §8）。
        if (leadership >= c.PillarLeadershipThreshold) TryAdd(Skill.SpiritualPillar, c.PillarBonusProb);

        // 目玉（極稀・1プレイに何度も会わない華）。怪物はどの選手にも、投法の妙は投手に。
        if (MathUtil.Chance(c.MarqueeSkillProb, rng)) visible.Add(Skill.Monster);
        if (isPitcher && MathUtil.Chance(c.MarqueeSkillProb, rng)) visible.Add(Skill.SubmarineMastery);

        if (visible.Count == 0 && hidden.Count == 0) return SkillSet.Empty;
        return new SkillSet(visible, hidden);
    }
}
