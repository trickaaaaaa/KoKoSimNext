using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 教え子名鑑（<see cref="DraftHistory"/>）の JSON 往復（issue #586・セーブ v7）。
///
/// <para>指名は「その年の3年生を独立Fork乱数で引いた結果」なので<b>再生成で復元できない</b>
/// （その年のロスターも成績も残っていない）。勲章そのものを焼く。</para>
/// </summary>
internal static class DraftSaves
{
    public static JsonArray ToJson(DraftHistory history)
    {
        var arr = new JsonArray();
        foreach (var r in history.Records)
        {
            arr.Add(new JsonObject()
                .Add("year", r.Year)
                .Add("yearIndex", r.YearIndex)
                .Add("playerId", r.PlayerId)
                .Add("name", r.Name)
                .Add("number", r.UniformNumber)
                .Add("round", r.Round)
                .Add("band", EnumJson.Write(r.Band))
                .Add("notability", r.Notability));
        }
        return arr;
    }

    public static void RestoreInto(DraftHistory target, JsonArray arr)
    {
        target.Clear();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            target.Add(new DraftRecord(
                o.Int("year"),
                o.Int("yearIndex"),
                o.Int("playerId"),
                o.Str("name") ?? "",
                o.Int("number"),
                o.Int("round"),
                EnumJson.Read<DraftRankBand>(o.Str("band")),
                o.Double("notability")));
        }
    }
}
