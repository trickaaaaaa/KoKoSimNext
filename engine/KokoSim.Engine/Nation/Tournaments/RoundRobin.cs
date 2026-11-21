using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>リーグ戦の1校の成績（勝点＝勝利数, 得失点差つき）。</summary>
public sealed record Standing(School School, int Wins, int Losses, int RunMargin);

/// <summary>
/// 総当たりリーグ（設計書05 §1.5 round_robin）。全対戦を消化し、勝点（勝利数）で順位付け。
/// 2校同率は直接対決→得失点差、3校以上の同率は得失点差で解決。リーグ戦の県は公式戦数が多く実戦経験を稼ぎやすい（§1.5）。決定論。
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

        return Rank(teams, wins, losses, margin, head);
    }

    /// <summary>
    /// リーグ戦の順位付け（設計書05 §1.5）。勝点（勝利数）で並べ、同率のうち<b>ちょうど2校</b>のときだけ
    /// 直接対決の勝者を上にする。3校以上の同率は直接対決を使わず（三すくみで順位が動くため）、
    /// 得失点差→強さ→Id の総順序で決める。<see cref="Run"/> と対話リーグ
    /// （<see cref="BlockLeagueRunner"/>）が同じ規則で順位を出すための単一の入口。
    /// <paramref name="head"/> のキーは (小Id, 大Id)・値は勝者Id。
    /// </summary>
    public static IReadOnlyList<Standing> Rank(
        IReadOnlyList<School> teams, IReadOnlyDictionary<int, int> wins, IReadOnlyDictionary<int, int> losses,
        IReadOnlyDictionary<int, int> margin, IReadOnlyDictionary<(int, int), int> head)
    {
        var ordered = new List<School>(teams.Count);
        foreach (var group in teams.GroupBy(t => wins[t.Id]).OrderByDescending(g => g.Key))
        {
            var tied = group
                .OrderByDescending(t => margin[t.Id])
                .ThenByDescending(t => t.Strength)
                .ThenBy(t => t.Id)
                .ToList();
            if (tied.Count == 2)
            {
                var a = tied[0];
                var b = tied[1];
                var key = (System.Math.Min(a.Id, b.Id), System.Math.Max(a.Id, b.Id));
                if (head.TryGetValue(key, out var winnerId) && winnerId == b.Id)
                {
                    tied[0] = b; tied[1] = a;
                }
            }
            ordered.AddRange(tied);
        }

        return ordered.Select(t => new Standing(t, wins[t.Id], losses[t.Id], margin[t.Id])).ToList();
    }
}
