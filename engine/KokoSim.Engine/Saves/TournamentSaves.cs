using System.Collections.Generic;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 大会モードの保存分（<see cref="TournamentSaveState"/>）の JSON 往復（issue #565）。
///
/// <para>学校は Id だけを書く（全国の学校は決定論再生成＝実体を保存しない）。空き枠（不戦勝）は
/// <c>null</c> で書く＝<b>0 や -1 のような番兵を使わない</b>。自校 Id は -1 で、生成校の Id と衝突しない
/// 値を番兵に選ぶと将来 Id 体系が変わったときに黙って壊れるため。</para>
/// </summary>
internal static class TournamentSaves
{
    // ===== 書き出し =====

    public static JsonValue ToJson(TournamentSaveState? t)
    {
        if (t is null) return JsonNull.Instance;
        return new JsonObject()
            .Add("kind", EnumJson.Write(t.Kind))
            .Add("title", t.Title)
            .Add("day", t.TournamentDay)
            .Add("bannerPending", t.BannerPending)
            .Add("fieldSchoolIds", IntsToJson(t.FieldSchoolIds))
            .Add("runner", RunnerToJson(t.Runner));
    }

    private static JsonObject RunnerToJson(TournamentRunnerState r)
    {
        var rng = new JsonArray();
        foreach (var w in r.RngState) rng.Add(JsonValue.Of(w));

        var rounds = new JsonArray();
        foreach (var row in r.RoundEntrants) rounds.Add(row is null ? JsonNull.Instance : NullableIdsToJson(row));

        var cards = new JsonArray();
        foreach (var c in r.Cards)
            cards.Add(new JsonObject()
                .Add("round", c.Round).Add("slot", c.Slot).Add("topWon", c.TopWon)
                .Add("ws", c.WinnerScore).Add("ls", c.LoserScore).Add("mercy", c.MercyEnded));

        var matches = new JsonArray();
        foreach (var m in r.Matches)
            matches.Add(new JsonObject()
                .Add("roundName", m.RoundName).Add("remaining", m.RoundsRemaining)
                .Add("winnerName", m.WinnerName).Add("loserName", m.LoserName)
                .Add("winnerId", m.WinnerId).Add("loserId", m.LoserId)
                .Add("ws", m.WinnerScore).Add("ls", m.LoserScore)
                .Add("managerInvolved", m.ManagerInvolved).Add("mercy", m.MercyEnded));

        var ledger = new JsonArray();
        foreach (var o in r.PitchLedger)
            ledger.Add(new JsonObject()
                .Add("bySource", o.BySource).Add("a", o.KeyA).Add("b", o.KeyB)
                .Add("day", o.MatchDay).Add("pitches", o.Pitches));

        return new JsonObject()
            .Add("title", r.Title)
            .Add("national", r.IsNationalTournament)
            .Add("playerActive", r.PlayerActive)
            .Add("isChampion", r.IsChampion)
            .Add("championName", r.ChampionName)
            .Add("roundIndex", r.RoundIndex)
            .Add("rng", rng)
            .Add("roundEntrants", rounds)
            .Add("current", NullableIdsToJson(r.Current))
            .Add("cards", cards)
            .Add("matches", matches)
            .Add("pitchLedger", ledger);
    }

    private static JsonArray IntsToJson(IReadOnlyList<int> ids)
    {
        var arr = new JsonArray();
        foreach (var id in ids) arr.Add(JsonValue.Of(id));
        return arr;
    }

    private static JsonArray NullableIdsToJson(IReadOnlyList<int?> ids)
    {
        var arr = new JsonArray();
        foreach (var id in ids) arr.Add(id is int v ? JsonValue.Of(v) : JsonNull.Instance);
        return arr;
    }

    // ===== 読み取り =====

    public static TournamentSaveState? FromJson(JsonValue? value)
    {
        if (value is not JsonObject o) return null;   // 無い/null＝通常モードのセーブ（v1 を含む）
        return new TournamentSaveState
        {
            Kind = EnumJson.Read<TournamentKind>(o.Str("kind")),
            Title = o.Str("title") ?? "",
            TournamentDay = o.Int("day"),
            BannerPending = o.Bool("bannerPending"),
            FieldSchoolIds = IntsFromJson(o.Arr("fieldSchoolIds")),
            Runner = RunnerFromJson(o.Obj("runner")),
        };
    }

    private static TournamentRunnerState RunnerFromJson(JsonObject o)
    {
        var rngArr = o.Arr("rng");
        var rng = new ulong[rngArr.Count];
        for (var i = 0; i < rngArr.Count; i++) rng[i] = ((JsonNumber)rngArr[i]).AsULong();

        var rounds = new List<List<int?>?>();
        foreach (var row in o.Arr("roundEntrants").Items)
            rounds.Add(row is JsonArray a ? NullableIdsFromJson(a) : null);

        var cards = new List<SavedBracketCard>();
        foreach (var item in o.Arr("cards").Items)
        {
            var c = item.AsObject();
            cards.Add(new SavedBracketCard(
                c.Int("round"), c.Int("slot"), c.Bool("topWon"), c.Int("ws"), c.Int("ls"), c.Bool("mercy")));
        }

        var matches = new List<BracketMatch>();
        foreach (var item in o.Arr("matches").Items)
        {
            var m = item.AsObject();
            matches.Add(new BracketMatch(
                m.Str("roundName") ?? "", m.Int("remaining"),
                m.Str("winnerName") ?? "", m.Str("loserName") ?? "",
                m.Int("winnerId"), m.Int("loserId"), m.Int("ws"), m.Int("ls"),
                m.Bool("managerInvolved"), m.Bool("mercy")));
        }

        var ledger = new List<TournamentPitchLedger.SavedOuting>();
        foreach (var item in o.Arr("pitchLedger").Items)
        {
            var l = item.AsObject();
            ledger.Add(new TournamentPitchLedger.SavedOuting(
                l.Bool("bySource"), l.Int("a"), l.Int("b"), l.Int("day"), l.Int("pitches")));
        }

        return new TournamentRunnerState
        {
            Title = o.Str("title") ?? "",
            IsNationalTournament = o.Bool("national"),
            PlayerActive = o.Bool("playerActive"),
            IsChampion = o.Bool("isChampion"),
            ChampionName = o.Str("championName"),
            RoundIndex = o.Int("roundIndex"),
            RngState = rng,
            RoundEntrants = rounds,
            Current = NullableIdsFromJson(o.Arr("current")),
            Cards = cards,
            Matches = matches,
            PitchLedger = ledger,
        };
    }

    private static List<int> IntsFromJson(JsonArray arr)
    {
        var list = new List<int>(arr.Count);
        foreach (var v in arr.Items) list.Add(((JsonNumber)v).AsInt());
        return list;
    }

    private static List<int?> NullableIdsFromJson(JsonArray arr)
    {
        var list = new List<int?>(arr.Count);
        foreach (var v in arr.Items) list.Add(v is JsonNumber n ? n.AsInt() : (int?)null);
        return list;
    }
}
