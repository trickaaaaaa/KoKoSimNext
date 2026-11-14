using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>監督の赴任履歴 <see cref="ManagerPostingHistory"/> の JSON 往復（issue #980）。</summary>
internal static class ManagerPostingSaves
{
    public static JsonArray ClosedToJson(IReadOnlyList<ManagerPosting> closed)
    {
        var arr = new JsonArray();
        foreach (var p in closed)
            arr.Add(new JsonObject()
                .Add("school", p.SchoolName)
                .Add("startYear", p.StartYear)
                .Add("endYear", p.EndYear)
                .Add("wins", p.Wins)
                .Add("losses", p.Losses)
                .Add("summerApp", p.SummerAppearances)
                .Add("springApp", p.SpringAppearances));
        return arr;
    }

    public static List<ManagerPosting> ClosedFromJson(JsonArray arr)
    {
        var list = new List<ManagerPosting>();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            list.Add(new ManagerPosting
            {
                SchoolName = o.Str("school")!,
                StartYear = o.Int("startYear"),
                EndYear = o.Int("endYear"),
                Wins = o.Int("wins"),
                Losses = o.Int("losses"),
                SummerAppearances = o.Int("summerApp"),
                SpringAppearances = o.Int("springApp"),
            });
        }
        return list;
    }
}
