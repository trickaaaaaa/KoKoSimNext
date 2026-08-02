using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 控え名簿の組み立て（issue #381）。投手控えと野手控えに分かれていた2本を
/// <see cref="Team.Reserves"/> 1本へ畳むときの並びをここに集約する。
///
/// 並びは<b>背番号順＝プレイヤーが決めた優先度</b>（issue #270）。継投の既定指名・当日の出来の
/// サンプリングは「投手として置かれた控え」だけを名簿順に見るので、投手どうしの相対順が保たれる限り
/// この並べ替えでは結果が変わらない（＝乱数消費列も投手行の並びも不変）。
/// </summary>
public static class TeamReserves
{
    /// <summary>
    /// 投手控えと野手控えを1本へ畳む。背番号順（0＝未割当は末尾）。
    /// <see cref="Enumerable.OrderBy{TSource,TKey}(IEnumerable{TSource},System.Func{TSource,TKey})"/> は
    /// 安定ソートなので、同じ背番号どうしは渡した順（投手控え→野手控え）を保つ。
    /// </summary>
    public static IReadOnlyList<Player> Merge(
        IReadOnlyList<Player> pitchers, IReadOnlyList<Player> fielders)
        => pitchers.Concat(fielders)
            .OrderBy(p => p.UniformNumber > 0 ? p.UniformNumber : int.MaxValue)
            .ToList();
}
