using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 卒業生の名前の表（<see cref="GraduateRoll"/>）の JSON 往復（issue #1414・セーブ v41）。
/// 1行＝<c>[選手ID, 名前, 入学年度, 卒業年度]</c> の位置配列（キー名の繰り返しを避ける。年10〜15人ずつ増える表なので）。
/// </summary>
internal static class GraduateSaves
{
    public static JsonArray ToJson(GraduateRoll roll)
    {
        var arr = new JsonArray();
        foreach (var e in roll.Entries)
        {
            var row = new JsonArray();
            row.Add(JsonValue.Of(e.Id));
            row.Add(JsonValue.Of(e.Name));
            row.Add(JsonValue.Of(e.EnrolledYearIndex));
            row.Add(JsonValue.Of(e.GraduatedYearIndex));
            arr.Add(row);
        }
        return arr;
    }

    public static GraduateRoll FromJson(JsonArray arr)
    {
        var roll = new GraduateRoll();
        foreach (var v in arr.Items)
        {
            var row = v.AsArray();
            roll.Add(new GraduateEntry(
                ((JsonNumber)row[0]).AsInt(), ((JsonString)row[1]).Value,
                ((JsonNumber)row[2]).AsInt(), ((JsonNumber)row[3]).AsInt()));
        }
        return roll;
    }
}
