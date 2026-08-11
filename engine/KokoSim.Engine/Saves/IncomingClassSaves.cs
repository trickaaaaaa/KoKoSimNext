using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 入部予定者（<see cref="IncomingClass"/>）の JSON 往復（issue #587・セーブ v5）。
///
/// <para>3月確定〜4月合流の間はセーブに<b>選手の実体</b>（<c>players</c>）が載り、合流後は空になって
/// 見立て（<c>reports</c>）だけが残る＝ロスターと二重に持たない。null（＝まだ一度も3月を跨いでいない
/// 進行・旧セーブ）も往復できる。</para>
/// </summary>
internal static class IncomingClassSaves
{
    public static JsonValue ToJson(IncomingClass? c)
    {
        if (c is null) return JsonNull.Instance;

        var players = new JsonArray();
        foreach (var p in c.Players) players.Add(DevelopingPlayerSaves.ToJson(p));

        var reports = new JsonArray();
        foreach (var r in c.Reports) reports.Add(ReportToJson(r));

        return new JsonObject()
            .Add("yearIndex", c.YearIndex)
            .Add("disclosure", c.Disclosure)
            .Add("joined", c.Joined)
            .Add("players", players)
            .Add("reports", reports);
    }

    public static IncomingClass? FromJson(JsonValue? v)
    {
        if (v is null || v is JsonNull) return null;
        var o = v.AsObject();
        var c = new IncomingClass
        {
            YearIndex = o.Int("yearIndex"),
            Disclosure = o.Double("disclosure"),
            Joined = o.Bool("joined"),
        };
        foreach (var p in o.Arr("players").Items) c.Players.Add(DevelopingPlayerSaves.FromJson(p.AsObject()));
        foreach (var r in o.Arr("reports").Items) c.Reports.Add(ReportFromJson(r.AsObject()));
        return c;
    }

    private static JsonObject ReportToJson(ProspectReport r) => new JsonObject()
        .Add("id", r.PlayerId)
        .Add("name", r.Name)
        .Add("bat", r.Batting)
        .Add("mob", r.Mobility)
        .Add("def", r.Defense)
        .Add("pit", r.Pitching)
        .Add("kGrowth", r.KnowsGrowthType)
        .Add("kPotential", r.KnowsPotential)
        .Add("kPersonality", r.KnowsPersonality)
        .Add("kInjury", r.KnowsInjuryResistance);

    private static ProspectReport ReportFromJson(JsonObject o) => new()
    {
        PlayerId = o.Int("id"),
        Name = o.Str("name") ?? "",
        Batting = o.Double("bat"),
        Mobility = o.Double("mob"),
        Defense = o.Double("def"),
        Pitching = o.Double("pit"),
        KnowsGrowthType = o.Bool("kGrowth"),
        KnowsPotential = o.Bool("kPotential"),
        KnowsPersonality = o.Bool("kPersonality"),
        KnowsInjuryResistance = o.Bool("kInjury"),
    };
}
