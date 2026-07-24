using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Career.Draft;

/// <summary>
/// ドラフト（NPB指名）エンジン（設計書20）。純C#・決定論・表示専用（帯不変）。
/// (1) 在校選手（1〜3年）の注目度スナップショットを算出（週次, 候補入り／注目度変化の材料）。
/// (2) 10月最終週に3年候補の指名を確定（独立Fork乱数で既存baselineを乱さない）。
/// </summary>
public static class DraftEngine
{
    // 指名乱数の独立ストリーム（"DRAFT"）。年でずらして既存の乱数列と decorrelate（不変条件#2）。
    private const ulong DraftStreamKey = 0xD4AF_0000UL;

    /// <summary>
    /// 在校ロスター全員の注目度スナップショットを返す（元ロスター順・決定論）。
    /// <paramref name="statsById"/> は選手ID→累積成績（未出場は null）。UI は Band が None 以外を候補として表示。
    /// Shell は前回スナップショットと突き合わせて候補入り／注目度変化を通知フィードへ流す（設計書20 §5）。
    /// </summary>
    public static IReadOnlyList<DraftEvaluation> EvaluateRoster(
        IReadOnlyList<DevelopingPlayer> roster,
        Func<int, PlayerStats?> statsById,
        DraftCoefficients c)
    {
        var list = new List<DraftEvaluation>(roster.Count);
        foreach (var p in roster)
        {
            var stats = p.Id != 0 ? statsById(p.Id) : null;
            var n = NotabilityModel.Compute(p, stats, c);
            list.Add(new DraftEvaluation(
                p.Id, p.Name, p.Grade, p.IsPitcher, n, NotabilityModel.BandOf(n, c)));
        }
        return list;
    }

    /// <summary>1選手ぶんの注目度評価（Shell が個別に引きたいとき用）。</summary>
    public static DraftEvaluation Evaluate(DevelopingPlayer player, PlayerStats? stats, DraftCoefficients c)
    {
        var n = NotabilityModel.Compute(player, stats, c);
        return new DraftEvaluation(player.Id, player.Name, player.Grade, player.IsPitcher, n, NotabilityModel.BandOf(n, c));
    }

    /// <summary>
    /// 10月最終週の指名確定（設計書20 §3.2）。対象は「3年生（Grade>=3）」かつ「候補（Band!=None）」のみ。
    /// 各候補は注目度からのロジスティック確率を注入乱数で判定し、指名時はバンドの代表順位を round とする。
    /// 指名漏れも <see cref="DraftPick.Nominated"/>=false で残す。乱数は年でずらした独立Forkで既存決定論を保存。
    /// </summary>
    public static DraftResult RunNomination(
        int year,
        IReadOnlyList<DevelopingPlayer> roster,
        Func<int, PlayerStats?> statsById,
        DraftCoefficients c,
        IRandomSource rng)
    {
        var draftRng = rng.Fork(DraftStreamKey ^ (ulong)year);
        var picks = new List<DraftPick>();
        foreach (var p in roster)
        {
            if (p.Grade < 3) continue;
            var stats = p.Id != 0 ? statsById(p.Id) : null;
            var n = NotabilityModel.Compute(p, stats, c);
            var band = NotabilityModel.BandOf(n, c);
            if (!band.IsCandidate()) continue;   // 候補でない3年は指名対象外

            var prob = Logistic((n - c.NominationMidpoint) / c.NominationSpread);
            var nominated = draftRng.NextDouble() < prob;
            picks.Add(new DraftPick(
                p.Id, p.Name, n, band, nominated, nominated ? band.RepresentativeRound() : 0));
        }
        return new DraftResult(year, picks);
    }

    private static double Logistic(double z) => 1.0 / (1.0 + Math.Exp(-z));
}
