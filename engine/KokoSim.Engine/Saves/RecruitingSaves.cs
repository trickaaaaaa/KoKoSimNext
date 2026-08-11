using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 能動勧誘（<see cref="RecruitingState"/>）の JSON 往復（issue #588・セーブ v6）。
///
/// <para>中学生本人（<see cref="RecruitProspect.Player"/>）まで丸ごと保存する。リストは
/// 「秋に引いた顔ぶれ」で、訪問を重ねた履歴と対になっていないと意味が無い＝<b>再生成では復元できない</b>。
/// 3月の判定で使い切ったら、次の秋に新しいリストで上書きされる。</para>
/// </summary>
internal static class RecruitingSaves
{
    public static JsonValue ToJson(RecruitingState? s)
    {
        if (s is null) return JsonNull.Instance;

        var arr = new JsonArray();
        foreach (var p in s.Prospects) arr.Add(ProspectToJson(p));

        return new JsonObject()
            .Add("yearIndex", s.YearIndex)
            .Add("slots", s.Slots)
            .Add("resolved", s.Resolved)
            .Add("prospects", arr);
    }

    public static RecruitingState? FromJson(JsonValue? v)
    {
        if (v is null || v is JsonNull) return null;
        var o = v.AsObject();
        var s = new RecruitingState
        {
            YearIndex = o.Int("yearIndex"),
            Slots = o.Int("slots"),
            Resolved = o.Bool("resolved"),
        };
        foreach (var item in o.Arr("prospects").Items) s.Prospects.Add(ProspectFromJson(item.AsObject()));
        return s;
    }

    private static JsonObject ProspectToJson(RecruitProspect p) => new JsonObject()
        .Add("index", p.Index)
        .Add("prefectureId", p.PrefectureId)
        .Add("distance", EnumJson.Write(p.Distance))
        .Add("rivalTier", EnumJson.Write(p.RivalTier))
        .Add("visits", p.Visits)
        .Add("targeted", p.Targeted)
        .Add("decided", p.Decided)
        .Add("succeeded", p.Succeeded)
        .Add("player", DevelopingPlayerSaves.ToJson(p.Player))
        .Add("report", ProspectReportSaves.ToJson(p.Report));

    private static RecruitProspect ProspectFromJson(JsonObject o) => new()
    {
        Index = o.Int("index"),
        PrefectureId = o.Int("prefectureId"),
        Distance = EnumJson.Read<RecruitDistance>(o.Str("distance")),
        RivalTier = EnumJson.Read<Tier>(o.Str("rivalTier")),
        Visits = o.Int("visits"),
        Targeted = o.Bool("targeted"),
        Decided = o.Bool("decided"),
        Succeeded = o.Bool("succeeded"),
        Player = DevelopingPlayerSaves.FromJson(o.Obj("player")),
        Report = ProspectReportSaves.FromJson(o.Obj("report")),
    };
}
