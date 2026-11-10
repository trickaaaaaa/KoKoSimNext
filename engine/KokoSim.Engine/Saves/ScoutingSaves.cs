using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// スカウト（<see cref="ScoutingState"/>）の JSON 往復（issue #867・セーブ v26）。旧 <c>RecruitingSaves</c>（v6）を置き換える。
///
/// <para>候補本人（<see cref="ScoutCandidate.Player"/>）まで丸ごと保存する。リストは
/// 「秋に引いた顔ぶれ」で、訪問を重ねた履歴と対になっていないと意味が無い＝<b>再生成では復元できない</b>。
/// 3月の判定で使い切ったら、次の秋に新しいリストで上書きされる。</para>
/// </summary>
internal static class ScoutingSaves
{
    public static JsonValue ToJson(ScoutingState? s)
    {
        if (s is null) return JsonNull.Instance;

        var arr = new JsonArray();
        foreach (var c in s.Candidates) arr.Add(CandidateToJson(c));

        return new JsonObject()
            .Add("yearIndex", s.YearIndex)
            .Add("field", EnumJson.Write(s.Field))
            .Add("slots", s.Slots)
            .Add("resolved", s.Resolved)
            .Add("candidates", arr);
    }

    public static ScoutingState? FromJson(JsonValue? v)
    {
        if (v is null || v is JsonNull) return null;
        var o = v.AsObject();
        var s = new ScoutingState
        {
            YearIndex = o.Int("yearIndex"),
            Field = EnumJson.Read<ScoutField>(o.Str("field")),
            Slots = o.Int("slots"),
            Resolved = o.Bool("resolved"),
        };
        foreach (var item in o.Arr("candidates").Items) s.Candidates.Add(CandidateFromJson(item.AsObject()));
        return s;
    }

    private static JsonObject CandidateToJson(ScoutCandidate p) => new JsonObject()
        .Add("index", p.Index)
        .Add("prefectureId", p.PrefectureId)
        .Add("distance", EnumJson.Write(p.Distance))
        .Add("rivalTier", EnumJson.Write(p.RivalTier))
        .Add("visits", p.Visits)
        .Add("targeted", p.Targeted)
        .Add("decided", p.Decided)
        .Add("succeeded", p.Succeeded)
        .Add("preference", EnumJson.Write(p.Preference))
        .Add("player", DevelopingPlayerSaves.ToJson(p.Player))
        .Add("report", ReportToJson(p.Report));

    private static ScoutCandidate CandidateFromJson(JsonObject o) => new()
    {
        Index = o.Int("index"),
        PrefectureId = o.Int("prefectureId"),
        Distance = EnumJson.Read<RecruitDistance>(o.Str("distance")),
        RivalTier = EnumJson.Read<Tier>(o.Str("rivalTier")),
        Visits = o.Int("visits"),
        Targeted = o.Bool("targeted"),
        Decided = o.Bool("decided"),
        Succeeded = o.Bool("succeeded"),
        Preference = EnumJson.Read<CareerPathPreference>(o.Str("preference")),
        Player = DevelopingPlayerSaves.FromJson(o.Obj("player")),
        Report = ReportFromJson(o.Obj("report")),
    };

    private static JsonObject ReportToJson(ScoutReport r) => new JsonObject()
        .Add("tier", r.Tier)
        .Add("blurbAxis", r.BlurbAxis)
        .Add("blurbBucket", r.BlurbBucket)
        .Add("batting", r.Batting)
        .Add("mobility", r.Mobility)
        .Add("defense", r.Defense)
        .Add("pitching", r.Pitching)
        .Add("contact", r.Contact)
        .Add("power", r.Power)
        .Add("discipline", r.Discipline)
        .Add("speed", r.Speed)
        .Add("fielding", r.Fielding)
        .Add("catching", r.Catching)
        .Add("throwAccuracy", r.ThrowAccuracy)
        .Add("baserunning", r.Baserunning)
        .Add("velocity", r.Velocity)
        .Add("control", r.Control)
        .Add("pitchRank", r.PitchRank)
        .Add("stamina", r.Stamina);

    private static ScoutReport ReportFromJson(JsonObject o) => new()
    {
        Tier = o.Int("tier"),
        BlurbAxis = o.Str("blurbAxis") ?? "",
        BlurbBucket = o.Str("blurbBucket") ?? "",
        Batting = o.Double("batting"),
        Mobility = o.Double("mobility"),
        Defense = o.Double("defense"),
        Pitching = o.Double("pitching"),
        Contact = o.Double("contact"),
        Power = o.Double("power"),
        Discipline = o.Double("discipline"),
        Speed = o.Double("speed"),
        Fielding = o.Double("fielding"),
        Catching = o.Double("catching"),
        ThrowAccuracy = o.Double("throwAccuracy"),
        Baserunning = o.Double("baserunning"),
        Velocity = o.Double("velocity"),
        Control = o.Double("control"),
        PitchRank = o.Double("pitchRank"),
        Stamina = o.Double("stamina"),
    };
}
