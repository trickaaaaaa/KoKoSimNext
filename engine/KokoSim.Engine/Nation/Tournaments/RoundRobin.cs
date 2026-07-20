using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>リーグ戦の1校の成績（勝点＝勝利数, 得失点差つき）。</summary>
public sealed record Standing(School School, int Wins, int Losses, int RunMargin);

/// <summary>
/// 総当たりリーグ（設計書05 §1.5 round_robin）。全対戦を消化し、勝点（勝利数）で順位付け。
/// 同率は直接対決→得失点差で解決。リーグ戦の県は公式戦数が多く実戦経験を稼ぎやすい（§1.5）。決定論。
/// </summary>
public static class RoundRobin
{
    public static IReadOnlyList<Standing> Run(
        IReadOnlyList<School> teams, NationCoefficients coeff, IRandomSource rng,
        TournamentRecorder? recorder = null)
    {
        var wins = new Dictionary<int, int>();
        var losses = new Dictionary<int, int>();
        var margin = new Dictionary<int, int>();
        foreach (var t in teams) { wins[t.Id] = 0; losses[t.Id] = 0; margin[t.Id] = 0; }

        // 直接対決の勝者（順位のタイブレーク用）。キーは (小Id, 大Id)。
        var head = new Dictionary<(int, int), int>();

        for (var i = 0; i < teams.Count; i++)
        for (var j = i + 1; j < teams.Count; j++)
        {
            var (w, l, m) = AggregateMatch.PlayDetailed(teams[i], teams[j], coeff, rng);
            wins[w.Id]++; losses[l.Id]++;
            margin[w.Id] += m; margin[l.Id] -= m;
            head[(System.Math.Min(teams[i].Id, teams[j].Id), System.Math.Max(teams[i].Id, teams[j].Id))] = w.Id;
            recorder?.Record(0, w, l, m); // リーグ戦は序盤扱い（early 抽選）
        }

        // 総順序で並べ（勝点→得失点→強さ→Id）、その後 2校同点の直接対決で局所補正（§1.5）。
        var ordered = teams
            .OrderByDescending(t => wins[t.Id])
            .ThenByDescending(t => margin[t.Id])
            .ThenByDescending(t => t.Strength)
            .ThenBy(t => t.Id)
            .ToList();

        for (var k = 0; k + 1 < ordered.Count; k++)
        {
            var a = ordered[k];
            var b = ordered[k + 1];
            if (wins[a.Id] != wins[b.Id]) continue; // 同点のみ直接対決を優先
            var key = (System.Math.Min(a.Id, b.Id), System.Math.Max(a.Id, b.Id));
            if (head.TryGetValue(key, out var winnerId) && winnerId == b.Id)
            {
                ordered[k] = b; ordered[k + 1] = a; // 直接対決の勝者を上へ
            }
        }

        return ordered.Select(t => new Standing(t, wins[t.Id], losses[t.Id], margin[t.Id])).ToList();
    }
}
