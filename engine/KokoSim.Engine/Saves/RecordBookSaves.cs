using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 学校ごとの通算戦績 <see cref="SchoolRecordBook"/> の JSON 往復（issue #507）。校ID昇順で固定。
/// <see cref="SchoolRecord"/> の setter は internal＝同一アセンブリのここから直接復元できる。
/// </summary>
internal static class RecordBookSaves
{
    public static JsonArray ToJson(SchoolRecordBook book)
    {
        var ids = new List<int>(book.Records.Keys);
        ids.Sort();
        var arr = new JsonArray();
        foreach (var id in ids)
        {
            var r = book.Records[id];
            arr.Add(new JsonObject()
                .Add("schoolId", id)
                .Add("ow", r.OfficialWins)
                .Add("ol", r.OfficialLosses)
                .Add("summerApp", r.SummerAppearances)
                .Add("springApp", r.SpringAppearances)
                .Add("lastSummer", r.LastSummerYear is int ls ? JsonValue.Of(ls) : JsonNull.Instance)
                .Add("lastSpring", r.LastSpringYear is int lp ? JsonValue.Of(lp) : JsonNull.Instance)
                .Add("summerLabel", r.SummerAppearanceLabel)
                .Add("springLabel", r.SpringAppearanceLabel)
                .Add("kw", r.KoshienWins)
                .Add("kl", r.KoshienLosses)
                .Add("best", EnumJson.Write(r.BestResult)));
        }
        return arr;
    }

    public static void RestoreInto(SchoolRecordBook target, JsonArray arr)
    {
        target.ClearRecords();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            var r = target.For(o.Int("schoolId"));
            r.OfficialWins = o.Int("ow");
            r.OfficialLosses = o.Int("ol");
            r.SummerAppearances = o.Int("summerApp");
            r.SpringAppearances = o.Int("springApp");
            r.LastSummerYear = o.IsNull("lastSummer") ? (int?)null : o.Int("lastSummer");
            r.LastSpringYear = o.IsNull("lastSpring") ? (int?)null : o.Int("lastSpring");
            r.SummerAppearanceLabel = o.Str("summerLabel")!;
            r.SpringAppearanceLabel = o.Str("springLabel")!;
            r.KoshienWins = o.Int("kw");
            r.KoshienLosses = o.Int("kl");
            r.BestResult = EnumJson.Read<BestResult>(o.Str("best"));
        }
    }
}
