using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Career.Draft;

/// <summary>
/// 注目度スコアの算出（設計書20 §2）。能力の役割別合成（現在値×隠し上限のブレンド）と
/// 実績（<see cref="PlayerStats"/> 集計）の加重和。純関数・決定論・乱数不要（不変条件#2）。
/// 表示能力を直挿しせず合成＋隠し上限を混ぜる＝二層構造の趣旨に沿う（設計書20 §2.1）。
/// </summary>
public static class NotabilityModel
{
    // 役割別の能力加重（合計1.0）。投手加重は用語集 PitcherComposite と同一（球速単独で決まらない）。
    private static readonly (AbilityKind Kind, double Weight)[] BatterWeights =
    {
        (AbilityKind.Contact, 0.25), (AbilityKind.Power, 0.25), (AbilityKind.Speed, 0.15),
        (AbilityKind.Fielding, 0.15), (AbilityKind.ArmStrength, 0.10), (AbilityKind.Catching, 0.10),
    };
    private static readonly (AbilityKind Kind, double Weight)[] PitcherWeights =
    {
        (AbilityKind.Velocity, 0.40), (AbilityKind.Control, 0.25),
        (AbilityKind.Stamina, 0.15), (AbilityKind.PitchRank, 0.20),
    };

    /// <summary>選手の注目度（0〜100）。stats は未出場なら null 可（実績は中立50扱い）。</summary>
    public static double Compute(DevelopingPlayer player, PlayerStats? stats, DraftCoefficients c)
    {
        var ability = AbilityScore(player, c);
        var perf = PerformanceScore(player, stats, c);
        var n = c.AbilityWeight * ability + c.PerformanceWeight * perf;
        return Clamp(n, 0, 100);
    }

    /// <summary>能力合成スコア（0〜100）。現在値と隠し上限を <see cref="DraftCoefficients.CeilingBlend"/> でブレンド。</summary>
    public static double AbilityScore(DevelopingPlayer player, DraftCoefficients c)
    {
        var weights = player.IsPitcher ? PitcherWeights : BatterWeights;
        var cur = 0.0;
        var cap = 0.0;
        foreach (var (kind, w) in weights)
        {
            cur += w * player.Level(kind);
            cap += w * player.Cap(kind);
        }
        var blend = Clamp(c.CeilingBlend, 0, 1);
        return Clamp((1 - blend) * cur + blend * cap, 0, 100);
    }

    /// <summary>実績スコア（0〜100）。出場サンプルが少ないほど中立50へ収縮する。</summary>
    public static double PerformanceScore(DevelopingPlayer player, PlayerStats? stats, DraftCoefficients c)
    {
        if (stats is null) return 50.0;

        if (player.IsPitcher)
        {
            var p = stats.Pitching;
            if (p.BattersFaced <= 0) return 50.0;
            var raw = 50.0
                + (c.PitcherEraBase - p.Era) * c.PitcherEraScale
                + (p.KPer9 - c.PitcherK9Base) * c.PitcherK9Scale;
            var shrink = Clamp((double)p.BattersFaced / Math.Max(1, c.PitcherMinBattersFaced), 0, 1);
            return Clamp(50.0 * (1 - shrink) + raw * shrink, 0, 100);
        }
        else
        {
            var b = stats.Batting;
            if (b.PlateAppearances <= 0) return 50.0;
            var hrPerGame = b.Games > 0 ? (double)b.HomeRuns / b.Games : 0.0;
            var raw = 50.0
                + (b.Ops - c.BatterOpsBase) * c.BatterOpsScale
                + hrPerGame * c.BatterHrPerGameScale;
            var shrink = Clamp((double)b.PlateAppearances / Math.Max(1, c.BatterMinPlateAppearances), 0, 1);
            return Clamp(50.0 * (1 - shrink) + raw * shrink, 0, 100);
        }
    }

    /// <summary>注目度→予想指名順位バンド（設計書20 §3.1）。</summary>
    public static DraftRankBand BandOf(double notability, DraftCoefficients c)
    {
        if (notability >= c.FirstRoundThreshold) return DraftRankBand.FirstRound;
        if (notability >= c.UpperRoundThreshold) return DraftRankBand.UpperRound;
        if (notability >= c.MiddleRoundThreshold) return DraftRankBand.MiddleRound;
        if (notability >= c.CandidateThreshold) return DraftRankBand.LowerRound;
        return DraftRankBand.None;
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
