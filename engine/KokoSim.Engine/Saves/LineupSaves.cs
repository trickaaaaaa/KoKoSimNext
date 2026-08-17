using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 前回スタメンの記憶（<see cref="LineupMemory"/>）の JSON 往復（issue lineup-carry-over-previous-game・セーブ v14）。
/// 選手はIDで持つ（参照ではない）ので、ロード後にロスターへ照合し直せる。null＝まだ一度も確定していない。
/// </summary>
internal static class LineupSaves
{
    public static JsonValue ToJson(LineupMemory? m)
    {
        if (m is null) return JsonNull.Instance;
        var ids = new JsonArray();
        foreach (var id in m.PlayerIds) ids.Add(JsonValue.Of(id));
        var pos = new JsonArray();
        foreach (var p in m.Positions) pos.Add(EnumJson.Write(p));
        return new JsonObject()
            .Add("playerIds", ids)
            .Add("positions", pos)
            .Add("startingPitcherId", m.StartingPitcherId is int sp ? JsonValue.Of(sp) : JsonNull.Instance);
    }

    public static LineupMemory? FromJson(JsonValue? value)
    {
        if (value is not JsonObject o) return null;
        var ids = new List<int>();
        foreach (var v in o.Arr("playerIds").Items) ids.Add(((JsonNumber)v).AsInt());
        var pos = new List<FieldPosition>();
        foreach (var v in o.Arr("positions").Items) pos.Add(EnumJson.Read<FieldPosition>(((JsonString)v).Value));
        int? sp = o.IsNull("startingPitcherId") ? null : o.Int("startingPitcherId");
        return new LineupMemory(ids, pos, sp);
    }
}
