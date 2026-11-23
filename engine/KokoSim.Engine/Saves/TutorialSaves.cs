using System;
using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// チュートリアルの進み具合（<see cref="TutorialProgress"/>）の JSON 往復（issue #1496・セーブ v42）。
/// <c>{"seen": ["Basics", ...], "dismissed": false}</c>。場面は名前で焼く（並べ替えに強い）。未知の名前は読み飛ばす。
/// </summary>
internal static class TutorialSaves
{
    public static JsonObject ToJson(TutorialProgress p)
    {
        var seen = new JsonArray();
        foreach (var s in p.Seen) seen.Add(EnumJson.Write(s));
        return new JsonObject().Add("seen", seen).Add("dismissed", p.Dismissed);
    }

    public static TutorialProgress FromJson(JsonObject o)
    {
        var seen = new List<TutorialScene>();
        foreach (var v in o.Arr("seen").Items)
            if (v is JsonString str && Enum.TryParse<TutorialScene>(str.Value, out var s)) seen.Add(s);
        return TutorialProgress.Restore(seen, o.Bool("dismissed"));
    }

    /// <summary>v41 以前のセーブを埋める値（全部見た扱い）。</summary>
    public static JsonObject AllSeenJson() => ToJson(TutorialProgress.AllSeen());
}
