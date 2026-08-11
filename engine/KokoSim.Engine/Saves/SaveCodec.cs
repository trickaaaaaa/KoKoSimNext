using System;
using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Saves;

/// <summary>スロット一覧行に出す最小限のヘッダ（<see cref="SaveCodec.PeekHeader"/>）。</summary>
public readonly record struct SaveHeader(int Version, int Week, int YearIndex);

/// <summary>
/// セーブの正典コーデック（issue #507）。<see cref="SaveState"/> ⇔ JSON 文字列。<b>形（スキーマ）は engine 側</b>で、
/// gzip 圧縮とファイル書き出しは Shell 側が担う（persistentDataPath）。外部依存なし＝Unity からも同じコードで往復する。
///
/// <para>先頭に <c>version</c> を持ち、<see cref="TryDeserialize"/> は壊れた入力・未知の版でも<b>例外で落ちず</b>
/// false を返す（完了条件）。版が上がったら <see cref="Migrate"/> に移行ブロックを足す。</para>
/// </summary>
public static class SaveCodec
{
    /// <summary>現行スキーマ版。項目を増やす破壊的変更のたびに +1 し、<see cref="Migrate"/> に移行を足す。</summary>
    public const int CurrentVersion = 5;

    // ===== Serialize =====

    /// <summary>DTO を JSON 文字列へ（gzip・ファイル書き出しは呼び出し側）。</summary>
    public static string Serialize(SaveState s)
    {
        var root = new JsonObject()
            .Add("version", CurrentVersion)
            .Add("masterSeed", s.MasterSeed)
            .Add("week", s.Week)
            .Add("yearIndex", s.YearIndex)
            .Add("roster", RosterToJson(s.Roster))
            .Add("manager", CareerSaves.ManagerToJson(s.Manager))
            .Add("facilities", CareerSaves.FacilitiesToJson(s.Facilities))
            .Add("contracts", CareerSaves.ContractsToJson(s.Contracts))
            .Add("managerLastGrownYear", s.ManagerLastGrownYear)
            .Add("managerGrownWinsBaseline", s.ManagerGrownWinsBaseline)
            .Add("managerGrownKoshienBaseline", s.ManagerGrownKoshienBaseline)
            .Add("nationRosters", NationRostersToJson(s.NationRosters))
            .Add("nationTournamentStats", StatsSaves.NationStatsToJson(s.TournamentStats))
            .Add("sessionYear", s.SessionYear)
            .Add("stats", StatsSaves.StoreToJson(s.Stats))
            .Add("records", RecordBookSaves.ToJson(s.Records))
            .Add("newTeamPending", s.NewTeamPending)
            .Add("newTeamRetired", RetiredToJson(s.NewTeamRetiredNames))
            .Add("teamColorOverride", CareerSaves.TeamColorsToJson(s.TeamColorOverride))
            .Add("schoolName", s.SchoolName)
            .Add("prefectureId", s.PrefectureId)
            .Add("districtId", s.DistrictId.HasValue ? JsonValue.Of(s.DistrictId.Value) : JsonNull.Instance)
            .Add("incoming", IncomingClassSaves.ToJson(s.Incoming))
            .Add("tournament", TournamentSaves.ToJson(s.Tournament));
        return root.ToString();
    }

    // ===== Deserialize（非例外） =====

    /// <summary>
    /// JSON 文字列から DTO を復元する。壊れた入力・未知の版は例外を投げず false を返し、<paramref name="error"/> に理由を入れる。
    /// </summary>
    public static bool TryDeserialize(string? text, out SaveState state, out string error)
    {
        state = new SaveState();
        error = "";
        if (string.IsNullOrEmpty(text)) { error = "空のセーブ"; return false; }

        JsonObject root;
        try
        {
            root = JsonValue.Parse(text!).AsObject();
        }
        catch (JsonParseException e) { error = $"JSON として不正: {e.Message}"; return false; }
        catch (InvalidCastException) { error = "ルートがオブジェクトでない"; return false; }

        int version;
        try
        {
            if (!root.Has("version")) { error = "version が無い"; return false; }
            version = root.Int("version");
        }
        catch (Exception) { error = "version が数値でない"; return false; }

        if (version < 1 || version > CurrentVersion)
        {
            error = $"未対応の版: {version}（このビルドは v1〜v{CurrentVersion}）";
            return false;
        }

        try
        {
            root = Migrate(root, version);
            state = Read(root);
            return true;
        }
        catch (Exception e)
        {
            // 版は合っていても中身が欠ける/型が違う＝壊れたセーブ。ここでも落とさず弾く。
            error = $"復元に失敗: {e.Message}";
            state = new SaveState();
            return false;
        }
    }

    /// <summary>版番号だけ覗く（スロット一覧の互換表示用）。読めなければ null。</summary>
    public static int? PeekVersion(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            var root = JsonValue.Parse(text!).AsObject();
            return root.Has("version") ? root.Int("version") : (int?)null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// スロット一覧表示に必要な最小限（version/week/yearIndex）だけを覗く（issue #508）。
    /// ロスター等は読まないため <see cref="TryDeserialize"/> より軽い。読めなければ null。
    /// </summary>
    public static SaveHeader? PeekHeader(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            var root = JsonValue.Parse(text!).AsObject();
            if (!root.Has("version")) return null;
            return new SaveHeader(root.Int("version"), root.Int("week"), root.Int("yearIndex"));
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 旧版 JSON を現行スキーマへ移行する（issue #507 時点は v1 のみ＝素通し）。版が上がったら
    /// <c>if (version &lt; 2) { ...v1→v2... }</c> のブロックを積み上げる（各版で増えた項目だけ埋める）。
    /// </summary>
    private static JsonObject Migrate(JsonObject root, int version)
    {
        // v1→v2（issue #565）: 大会モードの進行（tournament）が増えた。v1 は「大会中は保存できない」
        // 仕様だったので、v1 のセーブは必ず通常モード＝tournament を null で補うだけでよい。
        if (version < 2 && !root.Has("tournament")) root.Add("tournament", JsonNull.Instance);
        // v2→v3: 施設基金（manager.facilityFund）と年契約（contracts）が増えた。どちらも欠損時の
        // 安全な既定が決まっている（基金0／スカウト網Lv1＝従来のスカウト費と同額）ので、
        // ここで補わず Read 側の Has 分岐で読む＝旧セーブがそのまま開ける。
        // v3→v4（issue #585）: 自校の同定（校名・本拠県・県内地区）が増えた。v3 以前は
        // 神奈川の「桜丘」固定だったので、その値で補う（地区は未設定＝null）。
        if (version < 4)
        {
            if (!root.Has("schoolName")) root.Add("schoolName", "桜丘");
            if (!root.Has("prefectureId")) root.Add("prefectureId", 13);
            if (!root.Has("districtId")) root.Add("districtId", JsonNull.Instance);
        }
        // v4→v5（issue #587）: 入部予定者（3月確定→4月仮入部）が増えた。v4 以前は「年度替わりで
        // その場生成」だったので、予定者を持たない＝null で補う。旧セーブを4月へ進めると
        // RosterLifecycle.AdvanceYear の従来経路（incoming=null＝その場生成）へそのまま落ちる。
        if (version < 5 && !root.Has("incoming")) root.Add("incoming", JsonNull.Instance);
        return root;
    }

    // ===== 中身の読み書き =====

    private static SaveState Read(JsonObject root)
    {
        var s = new SaveState
        {
            Version = CurrentVersion,
            MasterSeed = root.ULong("masterSeed"),
            Week = root.Int("week"),
            YearIndex = root.Int("yearIndex"),
            Roster = RosterFromJson(root.Arr("roster")),
            Manager = CareerSaves.ManagerFromJson(root.Obj("manager")),
            Facilities = CareerSaves.FacilitiesFromJson(root.Obj("facilities")),
            // 年契約は v3 で追加。旧セーブには無いので既定（スカウト網 Lv1＝従来のスカウト費15万と同額）で読む。
            Contracts = root.Has("contracts")
                ? CareerSaves.ContractsFromJson(root.Obj("contracts"))
                : ContractSet.Initial(),
            ManagerLastGrownYear = root.Int("managerLastGrownYear"),
            ManagerGrownWinsBaseline = root.Int("managerGrownWinsBaseline"),
            ManagerGrownKoshienBaseline = root.Int("managerGrownKoshienBaseline"),
            NationRosters = NationRostersFromJson(root.Arr("nationRosters")),
            SessionYear = root.Int("sessionYear"),
            NewTeamPending = root.Bool("newTeamPending"),
            NewTeamRetiredNames = RetiredFromJson(root.Arr("newTeamRetired")),
            TeamColorOverride = CareerSaves.TeamColorsFromJson(root["teamColorOverride"]),
            // 自校の同定は v4 で追加。Migrate が旧セーブへ既定（桜丘・神奈川・地区なし）を補うので素直に読む。
            SchoolName = root.Str("schoolName") ?? "桜丘",
            PrefectureId = root.Int("prefectureId"),
            DistrictId = root["districtId"] is JsonNull ? (int?)null : root.Int("districtId"),
            // 入部予定者は v5 で追加。Migrate が旧セーブへ null を補うので素直に読む。
            Incoming = IncomingClassSaves.FromJson(root.Has("incoming") ? root["incoming"] : null),
            Tournament = TournamentSaves.FromJson(root.Has("tournament") ? root["tournament"] : null),
        };

        s.TournamentStats = new NationTournamentStats();
        StatsSaves.RestoreInto(s.TournamentStats, root.Arr("nationTournamentStats"));

        s.Stats = new PlayerStatStore();
        StatsSaves.RestoreInto(s.Stats, root.Obj("stats"));

        s.Records = new SchoolRecordBook();
        RecordBookSaves.RestoreInto(s.Records, root.Arr("records"));

        return s;
    }

    private static JsonArray RosterToJson(List<DevelopingPlayer> roster)
    {
        var arr = new JsonArray();
        foreach (var p in roster) arr.Add(DevelopingPlayerSaves.ToJson(p));
        return arr;
    }

    private static List<DevelopingPlayer> RosterFromJson(JsonArray arr)
    {
        var list = new List<DevelopingPlayer>(arr.Count);
        foreach (var v in arr.Items) list.Add(DevelopingPlayerSaves.FromJson(v.AsObject()));
        return list;
    }

    private static JsonArray NationRostersToJson(List<AiSchoolRoster> rosters)
    {
        // 校ID昇順で固定（往復の再直列化を安定させる）。
        var sorted = new List<AiSchoolRoster>(rosters);
        sorted.Sort((a, b) => a.SchoolId.CompareTo(b.SchoolId));
        var arr = new JsonArray();
        foreach (var r in sorted) arr.Add(AiRosterSaves.SchoolRosterToJson(r));
        return arr;
    }

    private static List<AiSchoolRoster> NationRostersFromJson(JsonArray arr)
    {
        var list = new List<AiSchoolRoster>(arr.Count);
        foreach (var v in arr.Items) list.Add(AiRosterSaves.SchoolRosterFromJson(v.AsObject()));
        return list;
    }

    private static JsonArray RetiredToJson(List<string> names)
    {
        var arr = new JsonArray();
        foreach (var n in names) arr.Add(JsonValue.Of(n));
        return arr;
    }

    private static List<string> RetiredFromJson(JsonArray arr)
    {
        var list = new List<string>(arr.Count);
        foreach (var v in arr.Items) list.Add(((JsonString)v).Value);
        return list;
    }
}
