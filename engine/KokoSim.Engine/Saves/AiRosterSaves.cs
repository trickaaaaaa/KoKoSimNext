using System.Collections.Generic;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 全国の永続ロスター（<see cref="NationRosters"/> / <see cref="AiSchoolRoster"/> / <see cref="AiPlayer"/>）の
/// JSON 往復（issue #507）。Nation 本体（学校・県）は決定論再生成なので保存しない（School.cs の設計注記）。
/// 保存するのは<b>ブートストラップ済みの校のロスターだけ</b>＝(校ID, 現在年度) の遅延生成が年度を跨いで
/// 崩れないよう、materialize 済みの状態をそのまま持ち越す（完了条件の「年度をまたいで全国ロスター同一」）。
/// 校の並びは <see cref="AiSchoolRoster.SchoolId"/> 昇順で固定（辞書順の非決定性を除く）。
/// </summary>
internal static class AiRosterSaves
{
    // ===== NationRosters（materialize 済み全校） =====

    public static JsonArray NationRostersToJson(NationRosters rosters)
    {
        var ids = new List<int>(rosters.Schools);
        ids.Sort();
        var arr = new JsonArray();
        foreach (var id in ids)
            if (rosters.TryGet(id, out var r))
                arr.Add(SchoolRosterToJson(r));
        return arr;
    }

    /// <summary>空の <see cref="NationRosters"/> へ復元する（既存エントリは <see cref="NationRosters.ClearRosters"/> で一掃）。</summary>
    public static void RestoreInto(NationRosters target, JsonArray arr)
    {
        target.ClearRosters();
        foreach (var v in arr.Items)
            target.RestoreRoster(SchoolRosterFromJson(v.AsObject()));
    }

    // ===== AiSchoolRoster =====

    public static JsonObject SchoolRosterToJson(AiSchoolRoster r)
    {
        var players = new JsonArray();
        foreach (var p in r.Players) players.Add(AiPlayerToJson(p));
        return new JsonObject()
            .Add("schoolId", r.SchoolId)
            .Add("nextPlayerId", r.NextPlayerId)
            .Add("players", players);
    }

    public static AiSchoolRoster SchoolRosterFromJson(JsonObject o)
    {
        var players = new List<AiPlayer>();
        foreach (var v in o.Arr("players").Items) players.Add(AiPlayerFromJson(v.AsObject()));
        return new AiSchoolRoster(o.Int("schoolId"), players, o.Int("nextPlayerId"));
    }

    // ===== AiPlayer =====

    public static JsonObject AiPlayerToJson(AiPlayer p)
    {
        var apt = new JsonArray();
        foreach (var a in p.Aptitudes) apt.Add(JsonValue.Of(a));
        return new JsonObject()
            .Add("id", p.Id)
            .Add("enrollYear", p.EnrollmentYearIndex)
            .Add("apt", apt)
            .Add("phenom", EnumJson.Write(p.Phenom))
            .Add("grade", p.Grade)
            .Add("pendingBudget", p.PendingBudget)
            .Add("snapshot", PlayerSaves.ToJson(p.Snapshot));
    }

    public static AiPlayer AiPlayerFromJson(JsonObject o)
    {
        var aptArr = o.Arr("apt");
        var apt = new int[aptArr.Count];
        for (var i = 0; i < apt.Length; i++) apt[i] = ((JsonNumber)aptArr[i]).AsInt();

        var p = new AiPlayer(
            o.Int("id"),
            o.Int("enrollYear"),
            apt,
            EnumJson.Read<PhenomType>(o.Str("phenom")),
            o.Int("grade"),
            PlayerSaves.PlayerFromJson(o.Obj("snapshot")))
        {
            PendingBudget = o.Double("pendingBudget"),
        };
        return p;
    }
}
