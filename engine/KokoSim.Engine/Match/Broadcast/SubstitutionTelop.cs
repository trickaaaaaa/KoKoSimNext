using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Match.Broadcast;

/// <summary>交代テロップの1行（入る選手1人ぶん）。表示する文字だけを持つ。</summary>
/// <param name="Kind">種類（「代打」「投手交代」等）。</param>
/// <param name="Order">打順（「4番」。DH制の投手は空）。</param>
/// <param name="Number">背番号（0＝無し）。</param>
/// <param name="Name">入る選手のフルネーム。</param>
/// <param name="Position">いま就いた守備位置（代打・代走は守備に就いていないので空）。</param>
/// <param name="OutName">退く選手のフルネーム（誰も退かない守備位置の変更は null）。</param>
public sealed record TelopRow(string Kind, string Order, int Number, string Name, string Position, string? OutName);

/// <summary>交代テロップ1枚（1チームぶん。同じ間合いの交代はまとめて1枚）。</summary>
public sealed record TelopCard(bool TeamIsAway, IReadOnlyList<TelopRow> Rows);

/// <summary>
/// 交代のログ（<see cref="SubstitutionEvent"/>）から交代テロップと実況履歴の1行を組む（issue #920）。
/// 表示する中身の単一ソース＝UIはここで組んだ文字を並べるだけ。位置は「試合で実際に就いた位置」だけを出す
/// （CLAUDE.md「守備位置・適性はゲームが提示しない」）。
/// </summary>
public static class SubstitutionTelop
{
    /// <summary>
    /// 同じ間合いに積まれた交代（<paramref name="events"/>）をチームごとに1枚へまとめる。並びは最初に交代した順。
    /// 同じ選手が続けて動いた（入ってから守備位置を入れ替えた等）ときは1行にまとめ、最後の位置を出す。
    /// </summary>
    public static IReadOnlyList<TelopCard> Build(IEnumerable<SubstitutionEvent> events)
    {
        var order = new List<bool>();
        var rows = new Dictionary<bool, List<(SubstitutionEntry Entry, TelopRow Row)>>();
        foreach (var e in events)
        {
            if (!rows.TryGetValue(e.TeamIsAway, out var list))
            {
                rows[e.TeamIsAway] = list = new List<(SubstitutionEntry, TelopRow)>();
                order.Add(e.TeamIsAway);
            }
            foreach (var entry in e.Entries)
            {
                var i = list.FindIndex(x => SamePlayer(x.Entry.In, entry.In));
                if (i < 0) { list.Add((entry, RowOf(entry))); continue; }
                // 同じ選手の2件目。入り方（最初の種類）と退いた選手は先の行を保ち、位置・打順だけ今へ合わせる。
                var first = list[i].Entry;
                var merged = first.Kind == SubstitutionEntryKind.PositionChange
                    ? entry with { Out = entry.Out ?? first.Out }
                    : first with { Order = entry.Order, Position = entry.Position };
                list[i] = (merged, RowOf(merged));
            }
        }
        var cards = new List<TelopCard>(order.Count);
        foreach (var team in order)
        {
            var list = rows[team];
            if (list.Count == 0) continue;
            cards.Add(new TelopCard(team, list.ConvertAll(x => x.Row)));
        }
        return cards;
    }

    /// <summary>実況履歴の1行（例「5回表 代打 田中 一郎（山田 太郎）」）。1件の交代に複数あれば「／」でつなぐ。</summary>
    public static string HistoryLine(SubstitutionEvent e)
    {
        var parts = new List<string>();
        foreach (var entry in e.Entries)
        {
            var r = RowOf(entry);
            var s = r.Kind + " " + r.Name;
            if (r.Position.Length > 0) s += "（" + r.Position + "）";
            if (r.OutName != null) s += " ← " + r.OutName;
            parts.Add(s);
        }
        return e.Inning + "回" + (e.IsTopHalf ? "表" : "裏") + " " + string.Join("／", parts);
    }

    /// <summary>交代1件を表示の1行にする。</summary>
    public static TelopRow RowOf(SubstitutionEntry e)
    {
        // 代打・代走はまだ守備に就いていない＝位置を出さない（引き継いだ位置は試合で就いた位置ではない）。
        var showPosition = e.Kind is not (SubstitutionEntryKind.PinchHit or SubstitutionEntryKind.PinchRun);
        return new TelopRow(
            KindText(e.Kind),
            e.Order >= 1 ? e.Order + "番" : "",
            e.In.Number,
            e.In.Name,
            showPosition ? PositionText(e.Position) : "",
            e.Out?.Name);
    }

    /// <summary>種類の表記。</summary>
    public static string KindText(SubstitutionEntryKind k) => k switch
    {
        SubstitutionEntryKind.PinchHit => "代打",
        SubstitutionEntryKind.PinchRun => "代走",
        SubstitutionEntryKind.DefensiveSub => "守備交代",
        SubstitutionEntryKind.Relief => "投手交代",
        SubstitutionEntryKind.PositionChange => "守備位置変更",
        SubstitutionEntryKind.DhRelease => "DH解除",
        _ => "",
    };

    /// <summary>守備位置の呼び名（場内放送と同じ言い方）。</summary>
    public static string PositionText(FieldPosition p) => p switch
    {
        FieldPosition.Pitcher => "ピッチャー",
        FieldPosition.Catcher => "キャッチャー",
        FieldPosition.FirstBase => "ファースト",
        FieldPosition.SecondBase => "セカンド",
        FieldPosition.ThirdBase => "サード",
        FieldPosition.Shortstop => "ショート",
        FieldPosition.LeftField => "レフト",
        FieldPosition.CenterField => "センター",
        FieldPosition.RightField => "ライト",
        FieldPosition.DesignatedHitter => "指名打者",
        _ => "",
    };

    private static bool SamePlayer(SubstitutionPlayer a, SubstitutionPlayer b)
        => a.SourceId == b.SourceId && a.Number == b.Number && a.Name == b.Name;
}
