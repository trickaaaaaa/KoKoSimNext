using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Players;

/// <summary>
/// 性格タイプ1種の軸係数（設計書01 §1.1）。各軸の効き先は1つ・二重計上しない。
/// 中立値: CoachingReceptivity=1 / SelfGrowthFactor=1 / BuntSuccessBonus=0 / ChanceHitFactor=1 / LeadershipMeanOffset=0。
/// </summary>
public sealed record PersonalityProfile
{
    public required Personality Type { get; init; }
    /// <summary>生成重み（Normal は0＝生成対象外）。</summary>
    public double SpawnWeight { get; init; }
    /// <summary>②素直さ: 個別指導寄与(coachingFactor)の倍率。高いほど指導が効く。</summary>
    public double CoachingReceptivity { get; init; } = 1.0;
    /// <summary>③勤勉さ: 自主的成長(PersonalityFactor)の中心。高いほど放置でも伸びる。</summary>
    public double SelfGrowthFactor { get; init; } = 1.0;
    /// <summary>④自己犠牲⇔目立ちたがり: 犠打/進塁打の成功率への加算（自己犠牲+/目立ち−）。</summary>
    public double BuntSuccessBonus { get; init; }
    /// <summary>④: 得点圏の強攻時の長打質(Power)倍率（目立ちたがり>1・自己犠牲<1）。</summary>
    public double ChanceHitFactor { get; init; } = 1.0;
    /// <summary>①統率傾向: 生成時の統率傾向平均への偏り（主将向き+/不向き−）。</summary>
    public double LeadershipMeanOffset { get; init; }
}

/// <summary>
/// 性格システムの係数（設計書01 §1.1, CHANGELOG 22b, YAML駆動）。
/// 母集団平均が概ね中立になるよう重みと係数を対称化し、統計帯のドリフトを抑える（不変条件#5）。
/// 既定表は data/coefficients.yaml personalities: と一致させる。
/// </summary>
public sealed record PersonalityCoefficients
{
    /// <summary>PersonalityFactor 生成のタイプ内ジッタσ（タイプが中心、ここは個体差）。</summary>
    public double FactorJitterSd { get; init; } = 0.06;
    public double FactorMin { get; init; } = 0.75;
    public double FactorMax { get; init; } = 1.25;

    public IReadOnlyList<PersonalityProfile> Profiles { get; init; } = DefaultProfiles();

    /// <summary>タイプ→プロファイル（見つからなければ中立）。8件の線形探索で十分軽量。</summary>
    public PersonalityProfile Profile(Personality type)
    {
        foreach (var p in Profiles)
        {
            if (p.Type == type) return p;
        }
        return Neutral(type);
    }

    private static PersonalityProfile Neutral(Personality type) => new() { Type = type };

    /// <summary>重み付きで性格タイプを抽選（SpawnWeight&gt;0 のみ対象）。決定論。生成器で共通利用。</summary>
    public Personality Sample(IRandomSource rng)
    {
        var total = 0.0;
        foreach (var p in Profiles) total += p.SpawnWeight;
        if (total <= 0) return Personality.Normal;

        var r = rng.NextDouble() * total;
        foreach (var p in Profiles)
        {
            if (p.SpawnWeight <= 0) continue;
            r -= p.SpawnWeight;
            if (r < 0) return p.Type;
        }
        return Personality.Normal; // 数値誤差の保険（実質到達しない）
    }

    /// <summary>
    /// 既定プロファイル表（8タイプ＋中立）。重み総和8.5。各軸の重み付き平均は中立近傍:
    /// SelfGrowth≈1.005 / Coaching≈1.009 / Bunt≈+0.009 / ChanceHit≈+0.013 / LeadershipOffset≈+0.8。
    /// </summary>
    private static IReadOnlyList<PersonalityProfile> DefaultProfiles() => new PersonalityProfile[]
    {
        new() { Type = Personality.Normal, SpawnWeight = 0.0 },
        //                                        w     ②Coach ③Grow  ④Bunt  ④Chance ①Lead
        new() { Type = Personality.HotBlood,     SpawnWeight = 1.0, CoachingReceptivity = 1.00, SelfGrowthFactor = 1.08, BuntSuccessBonus =  0.05, ChanceHitFactor = 0.99, LeadershipMeanOffset =  14 },
        new() { Type = Personality.Hardworker,   SpawnWeight = 1.4, CoachingReceptivity = 1.06, SelfGrowthFactor = 1.10, BuntSuccessBonus =  0.05, ChanceHitFactor = 0.99, LeadershipMeanOffset =   4 },
        new() { Type = Personality.Genius,       SpawnWeight = 0.5, CoachingReceptivity = 0.88, SelfGrowthFactor = 1.10, BuntSuccessBonus = -0.06, ChanceHitFactor = 1.10, LeadershipMeanOffset =   0 },
        new() { Type = Personality.HonorStudent, SpawnWeight = 1.0, CoachingReceptivity = 1.10, SelfGrowthFactor = 1.00, BuntSuccessBonus =  0.05, ChanceHitFactor = 0.98, LeadershipMeanOffset =  12 },
        new() { Type = Personality.MyPace,       SpawnWeight = 1.4, CoachingReceptivity = 0.94, SelfGrowthFactor = 0.95, BuntSuccessBonus =  0.00, ChanceHitFactor = 1.00, LeadershipMeanOffset =   0 },
        new() { Type = Personality.MoodMaker,    SpawnWeight = 1.2, CoachingReceptivity = 1.00, SelfGrowthFactor = 0.92, BuntSuccessBonus = -0.06, ChanceHitFactor = 1.08, LeadershipMeanOffset =   4 },
        new() { Type = Personality.LoneWolf,     SpawnWeight = 0.8, CoachingReceptivity = 0.92, SelfGrowthFactor = 1.00, BuntSuccessBonus = -0.05, ChanceHitFactor = 1.06, LeadershipMeanOffset = -16 },
        new() { Type = Personality.Introvert,    SpawnWeight = 1.2, CoachingReceptivity = 1.08, SelfGrowthFactor = 0.95, BuntSuccessBonus =  0.04, ChanceHitFactor = 0.97, LeadershipMeanOffset = -14 },
    };
}
