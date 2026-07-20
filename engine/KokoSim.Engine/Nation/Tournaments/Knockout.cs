using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>順位付きトーナメント結果（優勝→準優勝→ベスト4…の並び）。</summary>
public sealed record KnockoutResult(IReadOnlyList<School> Placement, IReadOnlyDictionary<int, int> WinsBySchool)
{
    public School Champion => Placement[0];
    /// <summary>上位 n 校（進出枠のカット）。</summary>
    public IReadOnlyList<School> Top(int n) => Placement.Take(System.Math.Max(0, n)).ToList();
}

/// <summary>
/// シード付きシングルエリミネーション（設計書05 §1.5 knockout）。強豪をブラケット対角へ分散。
/// 敗退ラウンドを記録して順位（優勝/準優勝/ベスト4…）を返す。3位決定戦にも対応。決定論。
/// </summary>
public static class Knockout
{
    public static KnockoutResult Run(
        IReadOnlyList<School> entrants, NationCoefficients coeff, IRandomSource rng, bool thirdPlaceMatch = false,
        TournamentRecorder? recorder = null)
    {
        if (entrants.Count == 0) throw new System.ArgumentException("参加校が空です。");
        var wins = new Dictionary<int, int>();
        foreach (var s in entrants) wins[s.Id] = 0;
        if (entrants.Count == 1) return new KnockoutResult(new[] { entrants[0] }, wins);

        var seeded = entrants.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).ToList();
        var size = NextPowerOfTwo(seeded.Count);
        var seedOrder = SeedOrder(size);
        var current = new School?[size];
        for (var slot = 0; slot < size; slot++)
        {
            var seed = seedOrder[slot] - 1;
            current[slot] = seed < seeded.Count ? seeded[seed] : null;
        }

        // ラウンドごとの敗者（浅い敗退＝下位）。最後に優勝→準優勝→…の順へ組み立てる。
        var losersByRound = new List<List<School>>();
        while (current.Length > 1)
        {
            var next = new School?[current.Length / 2];
            var losers = new List<School>();
            for (var i = 0; i < next.Length; i++)
            {
                var a = current[2 * i];
                var b = current[2 * i + 1];
                if (a is null) { next[i] = b; continue; }
                if (b is null) { next[i] = a; continue; }
                var (winner, loser, margin) = AggregateMatch.PlayDetailed(a, b, coeff, rng);
                wins[winner.Id]++;
                losers.Add(loser);
                next[i] = winner;
                recorder?.Record(TournamentRecorder.RoundsRemaining(current.Length), winner, loser, margin);
            }
            losersByRound.Add(losers);
            current = next;
        }

        var champion = current[0]!;
        var placement = new List<School> { champion };

        // 各ラウンド敗者を「深い敗退＝上位」から並べる。準決勝敗者は3位決定戦で並べ替え可。
        for (var r = losersByRound.Count - 1; r >= 0; r--)
        {
            var losers = losersByRound[r];
            var isSemifinal = r == losersByRound.Count - 2; // 決勝の1つ前
            if (isSemifinal && thirdPlaceMatch && losers.Count == 2)
            {
                var (third, fourth, tpMargin) = AggregateMatch.PlayDetailed(losers[0], losers[1], coeff, rng);
                wins[third.Id]++; // 3位決定戦の勝利も進出度に反映
                placement.Add(third);
                placement.Add(fourth);
                recorder?.Record(2, third, fourth, tpMargin); // 3位決定戦は準決勝相当の会場

            }
            else
            {
                foreach (var l in losers.OrderByDescending(s => s.Strength).ThenBy(s => s.Id))
                    placement.Add(l);
            }
        }

        return new KnockoutResult(placement, wins);
    }

    private static int NextPowerOfTwo(int n)
    {
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>サイズ n(2の冪)の標準シード配置。1番と2番が対角になるよう再帰生成。</summary>
    private static int[] SeedOrder(int n)
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
