using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Season;

/// <summary>気づきの話題（設計書03 §5.6）。隠しパラメータの部分開示。</summary>
public enum InsightTopic
{
    RecentGrowth,      // 「最近伸びてきているようだ」（好調＋成長中のサイン）
    HighGrowthDomain,  // 「〜はまだ伸びる余地がありそうだ」（伸びしろ高の示唆）
    LowGrowthDomain,   // 「〜の伸びしろはないかもしれない」（伸びしろ低の示唆）
    GrowthType,        // 「大器晩成かもしれない」（成長タイプの示唆）
    Personality,       // 「〜という性格かもしれない」（性格タイプの示唆, 設計書01 §1.1）
}

/// <summary>
/// 気づき通知（設計書03 §5.6）。断定しない温度で提示し、外れることもある（IsAccurate=false は読み違え）。
/// UI はこのレコードを「〜かもしれない」文言に整形する。IsAccurate は内部検証用（プレイヤーには見せない）。
/// </summary>
public sealed record InsightNotice(DevelopingPlayer Player, InsightTopic Topic, string Domain, bool IsAccurate);

/// <summary>気づきの係数（設計書03 §5.6）。🟡頻度・精度カーブは検証で調整。</summary>
public sealed record InsightCoefficients
{
    /// <summary>選手1人あたり週次の基礎発生率（育成眼50時）。「たまに気づく」。</summary>
    public double BaseWeeklyProb { get; init; } = 0.006;
    /// <summary>育成眼1あたりの頻度増（高いほど気づきが増える）。</summary>
    public double ProbPerTalentEye { get; init; } = 0.00008;
    /// <summary>精度の基礎値（育成眼0時。低いと読み違える）。</summary>
    public double AccuracyBase { get; init; } = 0.55;
    /// <summary>育成眼1あたりの精度増（上限0.97: 完全な答え合わせにはしない）。</summary>
    public double AccuracyPerTalentEye { get; init; } = 0.004;
    public double AccuracyMax { get; init; } = 0.97;
}

/// <summary>
/// 気づきイベント（設計書03 §5.6）。監督の育成眼を通じて隠しパラメータの兆しを受動的に開示する。
/// 個別指導の「見極め」（能動的な精度向上）と相補。育成眼が高いほど頻度・精度が上がる。
/// </summary>
public static class InsightModel
{
    /// <summary>週次の気づき抽選。talentEye=監督の育成眼(1〜100)。状態は変更しない（通知のみ）。</summary>
    public static IReadOnlyList<InsightNotice> Week(
        IReadOnlyList<DevelopingPlayer> roster, double talentEye, IRandomSource rng, InsightCoefficients c)
    {
        var notices = new List<InsightNotice>();
        var prob = c.BaseWeeklyProb + talentEye * c.ProbPerTalentEye;
        var accuracy = MathUtil.Clamp(c.AccuracyBase + talentEye * c.AccuracyPerTalentEye, 0.0, c.AccuracyMax);

        foreach (var p in roster)
        {
            if (!MathUtil.Chance(prob, rng)) continue;

            var accurate = MathUtil.Chance(accuracy, rng);
            notices.Add(Compose(p, accurate, rng));
        }
        return notices;
    }

    private static InsightNotice Compose(DevelopingPlayer p, bool accurate, IRandomSource rng)
    {
        // 話題を選ぶ: 成長中の兆し > 性格 > 伸びしろの高低 > 成長タイプ。
        if (p.ConditionValue >= 0.2 && rng.NextDouble() < 0.4)
        {
            return new InsightNotice(p, InsightTopic.RecentGrowth, "", accurate);
        }

        // 性格の兆し（設計書01 §1.1 / 03 §5.6）: たまに性格に触れる。読み違え時は別タイプを示唆する。
        if (p.Personality != Players.Personality.Normal && rng.NextDouble() < 0.30)
        {
            var shown = accurate ? p.Personality : WrongPersonality(p.Personality, rng);
            return new InsightNotice(p, InsightTopic.Personality, Players.Personalities.DisplayName(shown), accurate);
        }

        // 分野別伸びしろの最大/最小を話題にする。読み違え(accurate=false)時は逆の示唆を返す。
        var (highDomain, lowDomain) = GrowthExtremes(p);
        if (rng.NextDouble() < 0.5)
        {
            var domain = accurate ? highDomain : lowDomain;
            return new InsightNotice(p, InsightTopic.HighGrowthDomain, domain, accurate);
        }
        else
        {
            var domain = accurate ? lowDomain : highDomain;
            return new InsightNotice(p, InsightTopic.LowGrowthDomain, domain, accurate);
        }
    }

    /// <summary>読み違え用: 本当の性格と異なる生成対象タイプを1つ選ぶ。</summary>
    private static Players.Personality WrongPersonality(Players.Personality actual, IRandomSource rng)
    {
        var pool = Players.Personalities.Spawnable;
        var idx = rng.NextInt(0, pool.Length);
        // actual と一致したら隣へずらす（別タイプを保証）。
        if (pool[idx] == actual) idx = (idx + 1) % pool.Length;
        return pool[idx];
    }

    private static (string High, string Low) GrowthExtremes(DevelopingPlayer p)
    {
        var pairs = new (string Name, double V)[]
        {
            ("pitching", p.PitchingGrowth), ("batting", p.BattingGrowth), ("defense", p.DefenseGrowth),
        };
        var high = pairs[0];
        var low = pairs[0];
        foreach (var x in pairs)
        {
            if (x.V > high.V) high = x;
            if (x.V < low.V) low = x;
        }
        return (high.Name, low.Name);
    }
}
