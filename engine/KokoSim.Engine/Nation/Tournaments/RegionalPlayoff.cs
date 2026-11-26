using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 地区大会の「出場校決定戦」（近畿の2026年秋〜・設計書05 §1.5）。決定戦に候補を出す県（<see cref="RegionalPlayoffRule"/>）の
/// 県大会 <c>CandidateRank</c> 位の校を抽選で2校ずつ組み、勝った校だけが地区大会へ進む（4校なら2試合・勝者2校）。
///
/// <para><b>決定論</b>（不変条件#2）: 組み合わせの抽選も各試合も、渡された乱数から <see cref="IRandomSource.Fork"/> した
/// 専用ストリームで引く＝本編の乱数列は進めない。試合は組ごとに別ストリーム（<see cref="MatchStreamId"/>＋組の添字）なので、
/// 自校の組だけ対話で戦い、残りの組を裏で解いても結果は全部裏で解いたときと一致する。</para>
/// </summary>
public static class RegionalPlayoff
{
    /// <summary>抽選ストリームの分岐ID（'PLYD'）。</summary>
    public const ulong DrawStreamId = TournamentForkKeys.RegionalPlayoffDraw;
    /// <summary>試合ストリームの分岐IDの基点（'PLYM'）。組の添字を足して使う。</summary>
    public const ulong MatchStreamId = 0x50_4C_59_4DUL;

    /// <summary>県大会の順位から「直接進む校」と「決定戦の候補」を分ける（県の並びは呼び出し側の順）。</summary>
    public static (List<School> Direct, List<School> Candidates) Split(
        RegionalFormat fmt, int year, IEnumerable<(string Pref, IReadOnlyList<School> Placement)> placementsByPref)
    {
        var direct = new List<School>();
        var candidates = new List<School>();
        foreach (var (pref, placement) in placementsByPref)
        {
            if (placement.Count == 0) continue;
            var berths = System.Math.Max(1, fmt.BerthsFor(pref, year));
            direct.AddRange(placement.Take(berths));
            if (fmt.PlayoffCandidateIndex(pref, year) is { } idx && idx < placement.Count && idx >= berths)
                candidates.Add(placement[idx]);
        }
        return (direct, candidates);
    }

    /// <summary>
    /// 候補を抽選で2校ずつ組む。奇数のときの余り1校は不戦勝で進む（B が null）。
    /// 候補は Id 順に整えてから抽選する＝呼び出し側の並びに依らず同じ組になる。
    /// </summary>
    public static IReadOnlyList<(School A, School? B)> Pair(IReadOnlyList<School> candidates, IRandomSource rng)
    {
        var order = candidates.OrderBy(s => s.Id).ToList();
        var draw = rng.Fork(DrawStreamId);
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = draw.NextInt(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        var pairs = new List<(School, School?)>();
        for (var i = 0; i < order.Count; i += 2)
            pairs.Add((order[i], i + 1 < order.Count ? order[i + 1] : null));
        return pairs;
    }

    /// <summary>組の添字 <paramref name="pairIndex"/> の試合の勝者（集計モデル）。</summary>
    public static School Play(School a, School b, int pairIndex, NationCoefficients coeff, IRandomSource rng)
        => AggregateMatch.Play(a, b, coeff, rng.Fork(MatchStreamId + (ulong)pairIndex));

    /// <summary>候補を組んで全部解き、勝ち上がった校を組の順で返す。</summary>
    public static IReadOnlyList<School> Resolve(IReadOnlyList<School> candidates, NationCoefficients coeff, IRandomSource rng)
    {
        var winners = new List<School>();
        var pairs = Pair(candidates, rng);
        for (var i = 0; i < pairs.Count; i++)
        {
            var (a, b) = pairs[i];
            winners.Add(b is null ? a : Play(a, b, i, coeff, rng));
        }
        return winners;
    }

    /// <summary>地区大会の出場校＝直接枠の校＋決定戦の勝者（決定戦の無い地区・年は直接枠だけ）。</summary>
    public static List<School> Qualify(
        RegionalFormat fmt, int year, IEnumerable<(string Pref, IReadOnlyList<School> Placement)> placementsByPref,
        NationCoefficients coeff, IRandomSource rng)
    {
        var (direct, candidates) = Split(fmt, year, placementsByPref);
        direct.AddRange(Resolve(candidates, coeff, rng));
        return direct;
    }
}
