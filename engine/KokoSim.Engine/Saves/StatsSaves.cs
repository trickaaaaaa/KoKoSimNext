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
    // ===== PlayerStatStore（5スコープ） =====

    public static JsonObject StoreToJson(PlayerStatStore s) => new JsonObject()
        .Add("career", BookToJson(s.Career))
        .Add("official", BookToJson(s.Official))
        .Add("current", BookToJson(s.CurrentTournament))
        .Add("prefQualifier", BookToJson(s.PrefQualifier))
        .Add("regionalQualifier", BookToJson(s.RegionalQualifier))
        .Add("archive", ArchiveToJson(s.Archive));

    public static void RestoreInto(PlayerStatStore target, JsonObject o)
    {
        RestoreBookInto(target.Career, o.Obj("career"));
        RestoreBookInto(target.Official, o.Obj("official"));
        RestoreBookInto(target.CurrentTournament, o.Obj("current"));
        // prefQualifier は v15（夏の甲子園）で追加。旧セーブは項目ごと無い＝空で読む
        // （その年の甲子園展望に県予選成績が出ないだけで進行は詰まらない）。
        if (o.Has("prefQualifier")) RestoreBookInto(target.PrefQualifier, o.Obj("prefQualifier"));
        else target.PrefQualifier.Clear();
        // regionalQualifier は v17（センバツの地区大会成績）で追加。旧セーブは項目ごと無い＝空で読む。
        if (o.Has("regionalQualifier")) RestoreBookInto(target.RegionalQualifier, o.Obj("regionalQualifier"));
        else target.RegionalQualifier.Clear();
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
        target.StartTournament();   // 今大会スコープを全消去してから積む
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            RestoreBookInto(target.GetOrCreateBook(o.Int("schoolId")), o.Obj("book"));
        }
    }

    /// <summary>全国の通算スコープ（校ID→StatBook, issue #634）。今大会と別配列で持つ。</summary>
    public static JsonArray NationCareerToJson(NationTournamentStats s)
    {
        var ids = new List<int>(s.CareerSchools);
        ids.Sort();
        var arr = new JsonArray();
        foreach (var id in ids)
        {
            var book = s.CareerForSchool(id);
            if (book is null) continue;
            arr.Add(new JsonObject().Add("schoolId", id).Add("book", BookToJson(book)));
        }
        return arr;
    }

    public static void RestoreCareerInto(NationTournamentStats target, JsonArray arr)
    {
        target.ClearCareer();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            RestoreBookInto(target.GetOrCreateCareerBook(o.Int("schoolId")), o.Obj("book"));
        }
    }

    /// <summary>
    /// 全国の県予選スコープ（校ID→StatBook。設計書05 §1.4b）。今大会・通算と別配列で持つ。
    /// 甲子園の大会展望が引く読み物なので、消えても進行は詰まらないが、代表校の再シムは
    /// 大会をまたいで走り直さないため保存する。
    /// </summary>
    public static JsonArray NationPrefQualifierToJson(NationTournamentStats s)
    {
        var ids = new List<int>(s.PrefQualifierSchools);
        ids.Sort();
        var arr = new JsonArray();
        foreach (var id in ids)
        {
            var book = s.PrefQualifierForSchool(id);
            if (book is null) continue;
            arr.Add(new JsonObject().Add("schoolId", id).Add("book", BookToJson(book)));
        }
        return arr;
    }

    public static void RestorePrefQualifierInto(NationTournamentStats target, JsonArray arr)
    {
        target.ClearPrefQualifier();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            RestoreBookInto(target.GetOrCreatePrefQualifierBook(o.Int("schoolId")), o.Obj("book"));
        }
    }

    /// <summary>
    /// 全国の地区大会スコープ（校ID→StatBook。設計書05 §1.5b）。今大会・通算・県予選と別配列で持つ。
    /// センバツの大会展望が引く読み物なので、消えても進行は詰まらないが、選出校の再シムは
    /// 大会をまたいで走り直さないため保存する。
    /// </summary>
    public static JsonArray NationRegionalQualifierToJson(NationTournamentStats s)
    {
        var ids = new List<int>(s.RegionalQualifierSchools);
        ids.Sort();
        var arr = new JsonArray();
        foreach (var id in ids)
        {
            var book = s.RegionalQualifierForSchool(id);
            if (book is null) continue;
            arr.Add(new JsonObject().Add("schoolId", id).Add("book", BookToJson(book)));
        }
        return arr;
    }

    public static void RestoreRegionalQualifierInto(NationTournamentStats target, JsonArray arr)
    {
        target.ClearRegionalQualifier();
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            RestoreBookInto(target.GetOrCreateRegionalQualifierBook(o.Int("schoolId")), o.Obj("book"));
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
        .Add("sf", b.SacrificeFlies)
        .Add("sh", b.SacrificeHits);

    // sh は issue sacrifice-bunt-counted-as-at-bat で追加。旧セーブは項目ごと無い＝0で読む。
    public static void RestoreBattingInto(BattingStatLine target, JsonObject o) => target.Restore(
        o.Int("g"), o.Int("pa"), o.Int("ab"), o.Int("h"), o.Int("2b"), o.Int("3b"), o.Int("hr"),
        o.Int("rbi"), o.Int("bb"), o.Int("hbp"), o.Int("so"), o.Int("sb"), o.Int("cs"), o.Int("r"), o.Int("sf"),
        o.Has("sh") ? o.Int("sh") : 0);

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

    // w/l（勝敗投手）は概念ごと撤去（監督判断2026-08-27）。旧セーブに残る "w"/"l" キーは読まずに無視する
    // だけで済む（余分なキーがあるだけ）。SaveCodec.CurrentVersion は上げない＝キー削除だけなので
    // 旧セーブの読み込み自体は壊れない（Migrate 側の対応も不要）。
    public static void RestorePitchingInto(PitchingStatLine target, JsonObject o)
    {
        target.Restore(
            o.Int("g"), o.Int("gs"), o.Int("outs"), o.Int("bf"), o.Int("h"),
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
                    .Add("year", a.Year)
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
                // year は v8（issue #600）で追加。旧セーブは項目ごと無い＝0（年不明）で読む。
                a.Year = po.Has("year") ? po.Int("year") : 0;
                RestoreBattingInto(a.Batting, po.Obj("bat"));
                RestorePitchingInto(a.Pitching, po.Obj("pit"));
            }
        }
    }
}
