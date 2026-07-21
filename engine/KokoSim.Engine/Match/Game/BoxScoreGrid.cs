using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;

namespace KokoSim.Engine.Match.Game;

/// <summary>打席グリッドの1マス（打者の n 打席目の結果）。表示専用の観測データ。</summary>
/// <param name="PaIndex">打順枠の中での打席番号（1始まり。代打も枠の連番を引き継ぐ）。</param>
/// <param name="Inning">イニング（1始まり）。</param>
/// <param name="Result">打席結果。表示テキストへの変換はUI側の責務。</param>
/// <param name="RunsScored">このプレーで入った得点。</param>
public sealed record BoxScoreGridCell(int PaIndex, int Inning, PlateAppearanceResult Result, int RunsScored);

/// <summary>打席グリッドの1行（打順枠×選手）。<see cref="BattingLine"/> と (Order, Name) で対応する。</summary>
/// <param name="Order">打順（1-9。ログに打順が無い場合は0）。</param>
/// <param name="BatterName">打者名。同じ打順枠に代打が入ると行が増える。</param>
/// <param name="BatterSourceId">打者の選手ID（自校のみ。相手校の生成選手は null）。</param>
/// <param name="Cells">打席（発生順）。</param>
public sealed record BoxScoreGridRow(
    int Order, string BatterName, int? BatterSourceId, IReadOnlyList<BoxScoreGridCell> Cells);

/// <summary>
/// プレー記録（<see cref="PlayLogEntry"/>）を「打者 × 打席番号」のグリッドへ組み替える純関数。
/// 試合結果・乱数順に一切触れない表示専用の変換（不変条件#2 決定論を維持）。
/// </summary>
public static class BoxScoreGrid
{
    /// <summary>
    /// 片チームぶんの打席グリッドを作る。isTop=true で先攻（表の攻撃）、false で後攻。
    /// 行は打順昇順→同じ打順枠内は初出順。打席番号は打順枠ごとの通し番号（代打は続き番号を受け取る）。
    /// </summary>
    public static IReadOnlyList<BoxScoreGridRow> Build(IReadOnlyList<PlayLogEntry> log, bool isTop)
    {
        // 打順枠ごとの打席カウンタ（枠単位。打順不明=0 は「0番の枠」として1本にまとめる）。
        var paCountByOrder = new Dictionary<int, int>();
        // (打順, 打者名) → 行。初出順を保つため別リストで順序を持つ。
        var rowIndex = new Dictionary<(int Order, string Name), int>();
        var orders = new List<int>();
        var names = new List<string>();
        var sourceIds = new List<int?>();
        var cells = new List<List<BoxScoreGridCell>>();

        foreach (var e in log)
        {
            if (e.IsTop != isTop) continue;

            paCountByOrder.TryGetValue(e.BatterOrder, out var pa);
            pa++;
            paCountByOrder[e.BatterOrder] = pa;

            var key = (e.BatterOrder, e.BatterName);
            if (!rowIndex.TryGetValue(key, out var i))
            {
                i = cells.Count;
                rowIndex[key] = i;
                orders.Add(e.BatterOrder);
                names.Add(e.BatterName);
                sourceIds.Add(e.BatterSourceId);
                cells.Add(new List<BoxScoreGridCell>());
            }
            cells[i].Add(new BoxScoreGridCell(pa, e.Inning, e.Result, e.RunsScored));
        }

        // 打順昇順（0＝不明は末尾）→ 同順は初出順。
        var order = new List<int>();
        for (var i = 0; i < cells.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            var ka = orders[a] == 0 ? int.MaxValue : orders[a];
            var kb = orders[b] == 0 ? int.MaxValue : orders[b];
            return ka != kb ? ka.CompareTo(kb) : a.CompareTo(b);
        });

        var rows = new List<BoxScoreGridRow>(cells.Count);
        foreach (var i in order)
            rows.Add(new BoxScoreGridRow(orders[i], names[i], sourceIds[i], cells[i]));
        return rows;
    }

    /// <summary>グリッドの列数＝最大打席番号（空なら0）。表のヘッダ「1打席目…n打席目」に使う。</summary>
    public static int MaxPaIndex(IReadOnlyList<BoxScoreGridRow> rows)
    {
        var max = 0;
        foreach (var r in rows)
            foreach (var c in r.Cells)
                if (c.PaIndex > max) max = c.PaIndex;
        return max;
    }
}
