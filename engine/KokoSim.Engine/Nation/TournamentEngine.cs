using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation;

/// <summary>トーナメント結果。優勝校と各校の勝利数（進出度＝名声更新に使う）。</summary>
public sealed record TournamentResult(School Champion, IReadOnlyDictionary<int, int> WinsBySchool);

/// <summary>
/// シード付きシングルエリミネーション（設計書05 §1.2）。強豪をブラケット対角に分散配置する。
/// 対戦は集計マッチで解決。決定論（注入乱数）。
/// </summary>
public static class TournamentEngine
{
    public static TournamentResult Run(IReadOnlyList<School> entrants, NationCoefficients coeff, IRandomSource rng)
    {
        if (entrants.Count == 0) throw new ArgumentException("参加校が空です。");

        var wins = new Dictionary<int, int>();
        foreach (var s in entrants) wins[s.Id] = 0;

        if (entrants.Count == 1) return new TournamentResult(entrants[0], wins);

        // 強さ順にシード（同値は Id で決定化）。
        var seeded = entrants.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).ToList();

        var size = NextPowerOfTwo(seeded.Count);
        var seedOrder = SeedOrder(size); // 1-based seed → スロット位置
        var slots = new School?[size];
        for (var slot = 0; slot < size; slot++)
        {
            var seed = seedOrder[slot] - 1; // 0-based
            slots[slot] = seed < seeded.Count ? seeded[seed] : null; // 不足分は不戦勝(null)
        }

        var current = slots;
        while (current.Length > 1)
        {
            var next = new School?[current.Length / 2];
            for (var i = 0; i < next.Length; i++)
            {
                var a = current[2 * i];
                var b = current[2 * i + 1];
                School? winner;
                if (a is null) winner = b;
                else if (b is null) winner = a;
                else
                {
                    winner = AggregateMatch.Play(a, b, coeff, rng);
                    wins[winner.Id]++;
                }
                next[i] = winner;
            }
            current = next;
        }

        return new TournamentResult(current[0]!, wins);
    }

    internal static int NextPowerOfTwo(int n)
    {
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>サイズ n(2の冪)の標準シード配置。1番と2番が対角になるよう再帰生成。</summary>
    internal static int[] SeedOrder(int n)
    {
        int[] order = { 1 };
        while (order.Length < n)
        {
            var m = order.Length * 2;
            var next = new int[m];
            for (var i = 0; i < order.Length; i++)
            {
                next[2 * i] = order[i];
                next[2 * i + 1] = m + 1 - order[i];
            }
            order = next;
        }
        return order;
    }
}
