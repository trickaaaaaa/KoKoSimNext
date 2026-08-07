using System.Collections.Generic;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 成績集計（<see cref="PlayerStatStore"/> / <see cref="StatBook"/> / 各 *StatLine / <see cref="TournamentArchive"/> /
/// <see cref="NationTournamentStats"/>）の JSON 往復（issue #507）。生カウンタは private set・private 辞書なので、
/// ドメイン型側に<b>復元専用の internal フック</b>（Restore*/GetOrCreate*/ClearRosters 等）を足して厳密に写す
/// （派生プロパティ＝打率・防御率等は焼かない）。辞書は選手ID／校ID／球種の昇順で固定＝往復で並びがぶれない。
/// </summary>
internal static class StatsSaves
{
    // ===== PlayerStatStore（4スコープ） =====

    public static JsonObject StoreToJson(PlayerStatStore s) => new JsonObject()
        .Add("career", BookToJson(s.Career))
        .Add("official", BookToJson(s.Official))
        .Add("current", BookToJson(s.CurrentTournament))
        .Add("archive", ArchiveToJson(s.Archive));

    public static void RestoreInto(PlayerStatStore target, JsonObject o)
    {
        RestoreBookInto(target.Career, o.Obj("career"));
        RestoreBookInto(target.Official, o.Obj("official"));
        RestoreBookInto(target.CurrentTournament, o.Obj("current"));
        RestoreArchiveInto(target.Archive, o.Arr("archive"));
    }

    // ===== NationTournamentStats（校ID→StatBook） =====

    public static JsonArray NationStatsToJson(NationTournamentStats s)
    {
        var ids = new List<int>(s.Schools);
        ids.Sort();
        var arr = new JsonArray();
        foreach (var id in ids)
        {
            var book = s.ForSchool(id);
            if (book is null) continue;
            arr.Add(new JsonObject().Add("schoolId", id).Add("book", BookToJson(book)));
        }
        return arr;
    }

    public static void RestoreInto(NationTournamentStats target, JsonArray arr)
    {
        target.StartTournament();   // 全消去してから積む
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            RestoreBookInto(target.GetOrCreateBook(o.Int("schoolId")), o.Obj("book"));
        }
    }

    // ===== StatBook（選手ID→PlayerStats） =====

    public static JsonObject BookToJson(StatBook book)
    {
        var ids = new List<int>(book.Players.Keys);
        ids.Sort();
        var players = new JsonArray();
        foreach (var id in ids)
            players.Add(PlayerStatsToJson(book.Players[id]));
        return new JsonObject().Add("players", players);
    }

    public static void RestoreBookInto(StatBook target, JsonObject o)
    {
        target.Clear();
        foreach (var v in o.Arr("players").Items)
        {
            var po = v.AsObject();
            RestorePlayerStatsInto(target.GetOrCreate(po.Int("id")), po);
        }
    }

    // ===== PlayerStats =====

    public static JsonObject PlayerStatsToJson(PlayerStats p) => new JsonObject()
        .Add("id", p.SourceId)
        .Add("bat", BattingToJson(p.Batting))
        .Add("pit", PitchingToJson(p.Pitching))
        .Add("fld", FieldingToJson(p.Fielding));

    private static void RestorePlayerStatsInto(PlayerStats target, JsonObject o)
    {
        RestoreBattingInto(target.Batting, o.Obj("bat"));
        RestorePitchingInto(target.Pitching, o.Obj("pit"));
        RestoreFieldingInto(target.Fielding, o.Obj("fld"));
    }

    // ===== BattingStatLine =====

    public static JsonObject BattingToJson(BattingStatLine b) => new JsonObject()
        .Add("g", b.Games)
        .Add("pa", b.PlateAppearances)
        .Add("ab", b.AtBats)
        .Add("h", b.Hits)
        .Add("2b", b.Doubles)
        .Add("3b", b.Triples)
        .Add("hr", b.HomeRuns)
        .Add("rbi", b.Rbi)
        .Add("bb", b.Walks)
        .Add("hbp", b.HitByPitches)
        .Add("so", b.StrikeOuts)
        .Add("sb", b.StolenBases)
        .Add("cs", b.CaughtStealing)
        .Add("r", b.Runs)
        .Add("sf", b.SacrificeFlies);

    public static void RestoreBattingInto(BattingStatLine target, JsonObject o) => target.Restore(
        o.Int("g"), o.Int("pa"), o.Int("ab"), o.Int("h"), o.Int("2b"), o.Int("3b"), o.Int("hr"),
        o.Int("rbi"), o.Int("bb"), o.Int("hbp"), o.Int("so"), o.Int("sb"), o.Int("cs"), o.Int("r"), o.Int("sf"));

    // ===== PitchingStatLine =====

    public static JsonObject PitchingToJson(PitchingStatLine p)
    {
        var byPitch = new JsonArray();
        var keys = new List<PitchType>(p.BattingAgainstByPitch.Keys);
        keys.Sort((a, b) => ((int)a).CompareTo((int)b));
        foreach (var k in keys)
        {
            var line = p.BattingAgainstByPitch[k];
            byPitch.Add(new JsonObject()
                .Add("type", EnumJson.Write(k))
                .Add("ab", line.AtBats)
                .Add("h", line.Hits)
                .Add("hr", line.HomeRuns));
        }
        return new JsonObject()
            .Add("g", p.Games)
            .Add("gs", p.GamesStarted)
            .Add("w", p.Wins)
            .Add("l", p.Losses)
            .Add("outs", p.Outs)
            .Add("bf", p.BattersFaced)
            .Add("h", p.Hits)
            .Add("r", p.Runs)
            .Add("er", p.EarnedRuns)
            .Add("so", p.StrikeOuts)
            .Add("bb", p.Walks)
            .Add("hb", p.HitBatters)
            .Add("np", p.Pitches)
            .Add("hra", p.HomeRunsAllowed)
            .Add("byPitch", byPitch);
    }

    public static void RestorePitchingInto(PitchingStatLine target, JsonObject o)
    {
        target.Restore(
            o.Int("g"), o.Int("gs"), o.Int("w"), o.Int("l"), o.Int("outs"), o.Int("bf"), o.Int("h"),
            o.Int("r"), o.Int("er"), o.Int("so"), o.Int("bb"), o.Int("hb"), o.Int("np"), o.Int("hra"));
        foreach (var v in o.Arr("byPitch").Items)
        {
            var bo = v.AsObject();
            target.RestorePitchType(EnumJson.Read<PitchType>(bo.Str("type")), bo.Int("ab"), bo.Int("h"), bo.Int("hr"));
        }
    }

    // ===== FieldingStatLine =====

    public static JsonObject FieldingToJson(FieldingStatLine f) => new JsonObject().Add("e", f.Errors);

    public static void RestoreFieldingInto(FieldingStatLine target, JsonObject o) => target.Restore(o.Int("e"));

    // ===== TournamentArchive =====

    public static JsonArray ArchiveToJson(TournamentArchive archive)
    {
        var keys = new List<TournamentArchiveKey>(archive.Keys);
        keys.Sort((a, b) =>
        {
            var c = a.Grade.CompareTo(b.Grade);
            return c != 0 ? c : ((int)a.Slot).CompareTo((int)b.Slot);
        });
        var arr = new JsonArray();
        foreach (var key in keys)
        {
            var slot = archive.Slot(key);
            if (slot is null) continue;
            var ids = new List<int>(slot.Keys);
            ids.Sort();
            var players = new JsonArray();
            foreach (var id in ids)
            {
                var a = slot[id];
                players.Add(new JsonObject()
                    .Add("id", id)
                    .Add("num", a.UniformNumber)
                    .Add("bat", BattingToJson(a.Batting))
                    .Add("pit", PitchingToJson(a.Pitching)));
            }
            arr.Add(new JsonObject()
                .Add("grade", key.Grade)
                .Add("slot", EnumJson.Write(key.Slot))
                .Add("players", players));
        }
        return arr;
    }

    public static void RestoreArchiveInto(TournamentArchive target, JsonArray arr)
    {
        target.Clear();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            var key = new TournamentArchiveKey(o.Int("grade"), EnumJson.Read<TournamentSlot>(o.Str("slot")));
            foreach (var pv in o.Arr("players").Items)
            {
                var po = pv.AsObject();
                var a = target.GetOrCreate(key, po.Int("id"));
                a.UniformNumber = po.Int("num");
                RestoreBattingInto(a.Batting, po.Obj("bat"));
                RestorePitchingInto(a.Pitching, po.Obj("pit"));
            }
        }
    }
}
