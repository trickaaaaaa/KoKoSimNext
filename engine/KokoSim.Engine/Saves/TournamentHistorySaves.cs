using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 歴代大会結果（<see cref="TournamentHistory"/>）の JSON 往復（issue #600・セーブ v8）。
///
/// <para>大会は毎年ブラケットを引き直すので<b>再生成では復元できない</b>（裏試合の勝敗まで含めて
/// 同じ樹形図をもう一度作ることはできない）。年度ごとの1行をそのまま焼く。</para>
/// </summary>
internal static class TournamentHistorySaves
{
    public static JsonArray ToJson(TournamentHistory history)
    {
        var arr = new JsonArray();
        foreach (var e in history.Entries)
        {
            arr.Add(new JsonObject()
                .Add("year", e.Year)
                .Add("kind", EnumJson.Write(e.Kind))
                .Add("reached", e.ReachedRoundName)
                .Add("champion", e.ManagerIsChampion)
                .Add("championId", e.ChampionId)
                .Add("championName", e.ChampionName));
        }
        return arr;
    }

    public static void RestoreInto(TournamentHistory target, JsonArray arr)
    {
        target.Clear();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            target.Add(new TournamentHistoryEntry(
                o.Int("year"),
                EnumJson.Read<TournamentKind>(o.Str("kind")),
                o.Str("reached") ?? "",
                o.Bool("champion"),
                o.Int("championId"),
                o.Str("championName") ?? ""));
        }
    }
}
