namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>星取表の1マスの種類。</summary>
public enum CrossCellKind
{
    /// <summary>対角（自分自身）。斜線で描く。</summary>
    Self,
    /// <summary>まだ戦っていない（空欄）。</summary>
    Unplayed,
    /// <summary>勝ち（○）。</summary>
    Win,
    /// <summary>負け（●）。</summary>
    Loss,
}

/// <summary>
/// 星取表の1マス。スコアは<b>その行の学校から見た</b>得点－失点（A勝ち 5-3 なら A の行に ○5-3、B の行に ●3-5）。
/// <see cref="Mercy"/>＝コールドで決着した試合。
/// </summary>
public sealed record CrossCell(CrossCellKind Kind, int RunsFor = 0, int RunsAgainst = 0, bool Mercy = false);

/// <summary>星取表の1行（学校）。右端の勝敗・得失点差・順位は順位表（engine の決めた順位）の写し。</summary>
public sealed record CrossRow(
    int SchoolId, string SchoolName, bool IsManager, IReadOnlyList<CrossCell> Cells,
    int Wins, int Losses, int RunMargin, int Rank, bool InQualifyingPlace);

/// <summary>1ブロックぶんの星取表（行と列は同じ学校順）。</summary>
public sealed record LeagueCrossTableView(int Block, bool IsManagerBlock, bool Started, IReadOnlyList<CrossRow> Rows);

/// <summary>
/// ブロックの対戦（<see cref="LeagueBlockView"/>）を総当たりの星取表へ組み直す（issue #783・大会パンフレット）。
/// 行・列の並びは順位表の並び（engine の順位）。乱数・IO 不使用の純関数。
/// </summary>
public static class LeagueCrossTable
{
    public static LeagueCrossTableView Build(LeagueBlockView view)
    {
        var order = view.Standings.Select(s => s.SchoolId).ToList();
        var index = new Dictionary<int, int>();
        for (var i = 0; i < order.Count; i++) index[order[i]] = i;

        var cells = new CrossCell[order.Count, order.Count];
        for (var r = 0; r < order.Count; r++)
            for (var c = 0; c < order.Count; c++)
                cells[r, c] = new CrossCell(r == c ? CrossCellKind.Self : CrossCellKind.Unplayed);

        foreach (var g in view.Games)
        {
            if (!g.Played) continue;
            if (!index.TryGetValue(g.AId, out var a) || !index.TryGetValue(g.BId, out var b)) continue;
            cells[a, b] = new CrossCell(g.AWon ? CrossCellKind.Win : CrossCellKind.Loss, g.AScore, g.BScore, g.MercyEnded);
            cells[b, a] = new CrossCell(g.AWon ? CrossCellKind.Loss : CrossCellKind.Win, g.BScore, g.AScore, g.MercyEnded);
        }

        var rows = new List<CrossRow>(order.Count);
        for (var r = 0; r < order.Count; r++)
        {
            var s = view.Standings[r];
            var line = new CrossCell[order.Count];
            for (var c = 0; c < order.Count; c++) line[c] = cells[r, c];
            rows.Add(new CrossRow(s.SchoolId, s.SchoolName, s.IsManager, line,
                s.Wins, s.Losses, s.RunMargin, s.Rank, s.InQualifyingPlace));
        }
        return new LeagueCrossTableView(view.Block, view.IsManagerBlock, view.Games.Any(g => g.Played), rows);
    }
}
