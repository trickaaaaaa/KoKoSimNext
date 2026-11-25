using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 戦績台帳（<see cref="MatchRecordBook"/>）の JSON 往復（issue #1413・セーブ v40）。
///
/// <para>全部残すので容量が効く。打撃・投手・打席の札の行は<b>オブジェクトでなく位置の配列</b>で焼く
/// （キー名の繰り返しを避ける。gzip 後の実測は PR 本文）。列挙型は他のセーブと同じく名前で往復する。</para>
/// </summary>
internal static class MatchRecordSaves
{
    public static JsonArray ToJson(MatchRecordBook book)
    {
        var arr = new JsonArray();
        foreach (var r in book.Records)
            arr.Add(new JsonObject()
                .Add("sid", r.SchoolId)
                .Add("y", r.Year)
                .Add("k", EnumJson.Write(r.Kind))
                .Add("m", r.Month)
                .Add("d", r.Day)
                .Add("rd", r.RoundName)
                .Add("oid", r.OpponentId)
                .Add("own", r.OwnName)
                .Add("opp", r.OpponentName)
                .Add("away", r.ManagerIsAway)
                .Add("or", r.OwnRuns)
                .Add("pr", r.OpponentRuns)
                .Add("mercy", r.MercyEnded)
                .Add("a", SideToJson(r.Away))
                .Add("h", SideToJson(r.Home)));
        return arr;
    }

    public static void RestoreInto(MatchRecordBook target, JsonArray arr)
    {
        target.Clear();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            target.Add(new MatchRecord
            {
                SchoolId = o.Int("sid"),
                Year = o.Int("y"),
                Kind = EnumJson.Read<TournamentKind>(o.Str("k")),
                Month = o.Int("m"),
                Day = o.Int("d"),
                RoundName = o.Str("rd") ?? "",
                OpponentId = o.Int("oid"),
                OwnName = o.Str("own") ?? "",
                OpponentName = o.Str("opp") ?? "",
                ManagerIsAway = o.Bool("away"),
                OwnRuns = o.Int("or"),
                OpponentRuns = o.Int("pr"),
                MercyEnded = o.Bool("mercy"),
                Away = SideFromJson(o["a"]),
                Home = SideFromJson(o["h"]),
            });
        }
    }

    private static JsonValue SideToJson(MatchRecordSide? s)
    {
        if (s is null) return JsonNull.Instance;

        var line = new JsonArray();
        foreach (var n in s.LineScore) line.Add(JsonValue.Of(n));

        var bat = new JsonArray();
        foreach (var b in s.Batting)
            bat.Add(Row(
                JsonValue.Of(b.Order), EnumJson.Write(b.Position), EnumJson.Write(b.Entry),
                PositionsToJson(b.Positions), JsonValue.Of(b.Name),
                JsonValue.Of(b.AtBats), JsonValue.Of(b.Hits), JsonValue.Of(b.Rbi), JsonValue.Of(b.HomeRuns),
                JsonValue.Of(b.Triples), JsonValue.Of(b.Doubles), JsonValue.Of(b.WalksHbp),
                JsonValue.Of(b.StrikeOuts), JsonValue.Of(b.StolenBases)));

        var pit = new JsonArray();
        foreach (var p in s.Pitching)
            pit.Add(Row(
                JsonValue.Of(p.Name), JsonValue.Of(p.Outs), JsonValue.Of(p.Pitches), JsonValue.Of(p.Hits),
                JsonValue.Of(p.StrikeOuts), JsonValue.Of(p.WalksHbp), JsonValue.Of(p.Runs)));

        var pa = new JsonArray();
        foreach (var row in s.PaRows)
        {
            var cells = new JsonArray();
            foreach (var c in row.Cells)
                cells.Add(Row(JsonValue.Of(c.PaIndex), EnumJson.Write(c.Result), JsonValue.Of(Flags(c))));
            pa.Add(Row(JsonValue.Of(row.Order), JsonValue.Of(row.BatterName), cells));
        }

        return new JsonObject()
            .Add("r", s.Runs).Add("h", s.Hits).Add("e", s.Errors)
            .Add("ls", line).Add("bat", bat).Add("pit", pit).Add("pa", pa);
    }

    private static MatchRecordSide? SideFromJson(JsonValue v)
    {
        if (v is not JsonObject o) return null;

        var line = new List<int>();
        foreach (var n in o.Arr("ls").Items) line.Add(((JsonNumber)n).AsInt());

        var bat = new List<MatchRecordBatter>();
        foreach (var item in o.Arr("bat").Items)
        {
            var a = item.AsArray();
            bat.Add(new MatchRecordBatter(
                Int(a, 0), EnumJson.Read<FieldPosition>(Str(a, 1)), EnumJson.Read<BoxEntry>(Str(a, 2)),
                PositionsFromJson(a[3]), Str(a, 4) ?? "",
                Int(a, 5), Int(a, 6), Int(a, 7), Int(a, 8), Int(a, 9), Int(a, 10), Int(a, 11), Int(a, 12), Int(a, 13)));
        }

        var pit = new List<MatchRecordPitcher>();
        foreach (var item in o.Arr("pit").Items)
        {
            var a = item.AsArray();
            pit.Add(new MatchRecordPitcher(Str(a, 0) ?? "", Int(a, 1), Int(a, 2), Int(a, 3), Int(a, 4), Int(a, 5), Int(a, 6)));
        }

        var paRows = new List<MatchRecordPaRow>();
        foreach (var item in o.Arr("pa").Items)
        {
            var a = item.AsArray();
            var cells = new List<MatchRecordPa>();
            foreach (var cv in a[2].AsArray().Items)
            {
                var c = cv.AsArray();
                var flags = Int(c, 2);
                cells.Add(new MatchRecordPa(
                    Int(c, 0), EnumJson.Read<PlateAppearanceResult>(Str(c, 1)),
                    (flags & SacFlyBit) != 0, (flags & SacBuntBit) != 0, (flags & DoublePlayBit) != 0));
            }
            paRows.Add(new MatchRecordPaRow(Int(a, 0), Str(a, 1) ?? "", cells));
        }

        return new MatchRecordSide(o.Int("r"), o.Int("h"), o.Int("e"), line, bat, pit, paRows);
    }

    private static JsonArray PositionsToJson(IReadOnlyList<FieldPosition> positions)
    {
        var arr = new JsonArray();
        foreach (var p in positions) arr.Add(EnumJson.Write(p));
        return arr;
    }

    private static IReadOnlyList<FieldPosition> PositionsFromJson(JsonValue v)
    {
        var list = new List<FieldPosition>();
        if (v is JsonArray arr)
            foreach (var item in arr.Items)
                if (item is JsonString s) list.Add(EnumJson.Read<FieldPosition>(s.Value));
        return list;
    }

    /// <summary>
    /// v46 以前の打撃成績の行は「最後に守った位置」を1つ（文字列か null）だけ持っていた。位置の並びへ直す
    /// （issue #1608）: スタメンは今の位置1つ、途中出場は就いた位置があればその1つ・無ければ空。
    /// </summary>
    internal static void MigrateToPositionTrail(JsonArray records)
    {
        foreach (var rec in records.Items)
        {
            if (rec is not JsonObject r) continue;
            foreach (var side in new[] { "a", "h" })
            {
                if (r[side] is not JsonObject so || so["bat"] is not JsonArray bat) continue;
                foreach (var row in bat.Items)
                {
                    if (row is not JsonArray a || a.Count < 5 || a[3] is JsonArray) continue;
                    var fielded = Str(a, 3);
                    var starter = Str(a, 2) == nameof(BoxEntry.Starter);
                    var trail = new JsonArray();
                    var pos = starter ? Str(a, 1) : fielded;
                    if (pos is not null) trail.Add(JsonValue.Of(pos));
                    a.Set(3, trail);
                }
            }
        }
    }

    private const int SacFlyBit = 1, SacBuntBit = 2, DoublePlayBit = 4;

    private static int Flags(MatchRecordPa c)
        => (c.SacFly ? SacFlyBit : 0) | (c.SacBunt ? SacBuntBit : 0) | (c.DoublePlay ? DoublePlayBit : 0);

    private static JsonArray Row(params JsonValue[] items)
    {
        var a = new JsonArray();
        foreach (var i in items) a.Add(i);
        return a;
    }

    private static int Int(JsonArray a, int i) => ((JsonNumber)a[i]).AsInt();
    private static string? Str(JsonArray a, int i) => a[i] is JsonString s ? s.Value : null;
}
