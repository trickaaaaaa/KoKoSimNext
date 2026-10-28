using System;
using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Saves;

/// <summary>
/// スロット一覧行に出す最小限のヘッダ（<see cref="SaveCodec.PeekHeader"/>）。
///
/// <para><paramref name="SchoolName"/> は v4（issue #585）から本体に入っている自校名。
/// <b>それより前のセーブには無い</b>ので null になる＝呼び出し側で既定（桜丘）へ落とす。
/// ここで既定を埋めない（「読めなかった」と「桜丘という校名だった」を潰さないため）。</para>
/// </summary>
public readonly record struct SaveHeader(int Version, int Week, int YearIndex, string? SchoolName);

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
    public const int CurrentVersion = 22;

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
            .Add("nextPlayerId", s.NextPlayerId)
            .Add("manager", CareerSaves.ManagerToJson(s.Manager))
            .Add("facilities", CareerSaves.FacilitiesToJson(s.Facilities))
            .Add("contracts", CareerSaves.ContractsToJson(s.Contracts))
            .Add("managerLastGrownYear", s.ManagerLastGrownYear)
            .Add("managerGrownWinsBaseline", s.ManagerGrownWinsBaseline)
            .Add("managerGrownKoshienBaseline", s.ManagerGrownKoshienBaseline)
            .Add("managerPendingUpsetDeltas", DoublesToJson(s.ManagerPendingUpsetDeltas))
            .Add("nationRosters", NationRostersToJson(s.NationRosters))
            .Add("nationTournamentStats", StatsSaves.NationStatsToJson(s.TournamentStats))
            .Add("nationCareerStats", StatsSaves.NationCareerToJson(s.TournamentStats))
            .Add("nationPrefQualifierStats", StatsSaves.NationPrefQualifierToJson(s.TournamentStats))
            .Add("nationRegionalQualifierStats", StatsSaves.NationRegionalQualifierToJson(s.TournamentStats))
            .Add("nationSchools", NationSchoolsToJson(s.NationSchools))
            .Add("nationYearWins", IntMapToJson(s.NationYearWins))
            .Add("nationKoshienChampionId", s.NationKoshienChampionId)
            .Add("sessionYear", s.SessionYear)
            .Add("stats", StatsSaves.StoreToJson(s.Stats))
            .Add("records", RecordBookSaves.ToJson(s.Records))
            .Add("newTeamPending", s.NewTeamPending)
            .Add("newTeamRetired", RetiredToJson(s.NewTeamRetiredNames))
            .Add("managerTransferPending", s.ManagerTransferPending)
            .Add("managerTransferKind", s.ManagerTransferKind)
            .Add("managerTransferCandidates", TransferCandidatesToJson(s.ManagerTransferCandidates))
            .Add("teamColorOverride", CareerSaves.TeamColorsToJson(s.TeamColorOverride))
            .Add("schoolName", s.SchoolName)
            .Add("prefectureId", s.PrefectureId)
            .Add("districtId", s.DistrictId.HasValue ? JsonValue.Of(s.DistrictId.Value) : JsonNull.Instance)
            .Add("nationOverlay", NationOverlayToJson(s.NationOverlay))
            .Add("incoming", IncomingClassSaves.ToJson(s.Incoming))
            .Add("recruiting", RecruitingSaves.ToJson(s.Recruiting))
            .Add("draft", DraftSaves.ToJson(s.Draft))
            .Add("tournamentHistory", TournamentHistorySaves.ToJson(s.TournamentHistory))
            .Add("events", EventSaves.ToJson(s.Events))
            .Add("trainingBoosts", EventSaves.BoostsToJson(s.TrainingBoosts))
            .Add("tournament", TournamentSaves.ToJson(s.Tournament))
            .Add("koshien", TournamentSaves.ToJson(s.Koshien))
            .Add("senbatsu", TournamentSaves.ToJson(s.Senbatsu))
            .Add("pendingSenbatsuFieldIds", IntsToJson(s.PendingSenbatsuFieldIds))
            .Add("pendingSenbatsuJinguRegion", s.PendingSenbatsuJinguRegion)
            .Add("lastPrefChampionId", NullableIntToJson(s.LastPrefChampionId))
            .Add("lastAutumnPlacementIds", IntsToJson(s.LastAutumnPlacementIds))
            .Add("lastDistrictChampionId", NullableIntToJson(s.LastDistrictChampionId))
            .Add("lastDistrictQualifierIds", IntsToJson(s.LastDistrictQualifierIds))
            .Add("lastRegionalChampionId", NullableIntToJson(s.LastRegionalChampionId))
            .Add("lastRegionalPlacementIds", IntsToJson(s.LastRegionalPlacementIds))
            .Add("lastJinguChampionId", NullableIntToJson(s.LastJinguChampionId))
            .Add("lastLineup", LineupSaves.ToJson(s.LastLineup));
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
    /// スロット一覧表示に必要な最小限（version/week/yearIndex/schoolName）だけを覗く（issue #508・#624）。
    /// ロスター等は読まないため <see cref="TryDeserialize"/> より軽い。読めなければ null。
    ///
    /// <para>校名は <b>スキーマ版を上げずに</b>読む＝v4 から本体に既に書かれている項目を覗くだけで、
    /// 書式は何も変わらない。無い（v3以前）／文字列でない場合は <c>SchoolName = null</c> を返し、
    /// 表示側が既定へ落とす（issue #624）。</para>
    /// </summary>
    public static SaveHeader? PeekHeader(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        using var reader = new System.IO.StringReader(text!);
        return PeekHeader(reader);
    }

    private static readonly string[] HeaderKeys = { "version", "week", "yearIndex", "schoolName" };

    /// <summary>
    /// <see cref="PeekHeader(string?)"/> の流し読み版（issue #660）。値を組み立てずに読み飛ばすので、
    /// 24MB のセーブでも DOM パースより桁違いに軽く、展開後の巨大な文字列も要らない
    /// （呼び出し側は gzip の展開ストリームをそのまま渡せる）。判定は文字列版と同じ:
    /// 文書全体の構文が壊れていれば null、version/week/yearIndex は整数必須、校名は任意。
    /// </summary>
    public static SaveHeader? PeekHeader(System.IO.TextReader reader)
    {
        try
        {
            var v = JsonHeaderScanner.Scan(reader, HeaderKeys);
            if (v[0] is not JsonHeaderScanner.NumberToken ver || !ver.TryInt(out var version)) return null;
            if (v[1] is not JsonHeaderScanner.NumberToken wk || !wk.TryInt(out var week)) return null;
            if (v[2] is not JsonHeaderScanner.NumberToken yi || !yi.TryInt(out var yearIndex)) return null;
            // ⚠ 校名は任意項目（v3以前は無い）。無い・文字列でない＝null で返し、ヘッダ全体は読める扱い
            // （校名が無いだけの旧セーブを「壊れている」表示に落とさない）。
            return new SaveHeader(version, week, yearIndex, v[3] as string);
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
        // v5→v6（issue #588）: 能動勧誘（中学生リストと訪問の状態）が増えた。v5 以前は寮を建てても
        // 勧誘枠が無かったので、リストを持たない＝null で補う（次の秋に初めて作られる）。
        if (version < 6 && !root.Has("recruiting")) root.Add("recruiting", JsonNull.Instance);
        // v6→v7（issue #586）: 教え子名鑑（過年度のドラフト指名確定）が増えた。v6 以前は Unity 側で
        // 指名が一度も走っていなかった＝勲章が存在しえないので、空配列で補う。
        if (version < 7 && !root.Has("draft")) root.Add("draft", new JsonArray());
        // v7→v8（issue #600）: 歴代大会結果（年度別の到達ラウンドと優勝校）が増えた。v7 以前は年度の情報を
        // 累計へ畳んで捨てていた＝過去ぶんは作り直せないので、空配列で補う（通算戦績 records は無傷で残る）。
        // 大会別アーカイブに増えた year は要素ごとの欠損＝Read 側で 0（＝年不明）として読む。
        if (version < 8 && !root.Has("tournamentHistory")) root.Add("tournamentHistory", new JsonArray());
        // ⚠ v9 以降は **別々のブランチが同時に版を上げた**（#634 が v9、#609 が v9、#615 が v10）。
        // ローカル統合時に 11 へ揃え、3つとも「11 未満なら欠けているものを補う」形にした
        // ＝どちらの枝で書いたセーブ（v9/v10）を開いても取りこぼしが出ない。
        // ・nationCareerStats（issue #634）: 全国の学校別成績の通算スコープ。過去ぶんは作り直せないので空配列。
        // ・events（issue #609）: 週次イベントの発火状態（クールダウン・年次フラグ）。未発火＝null。
        // ・trainingBoosts（issue #615）: 期間つきの練習効率補正。効いている補正は存在しえないので空配列。
        if (version < 11 && !root.Has("nationCareerStats")) root.Add("nationCareerStats", new JsonArray());
        if (version < 11 && !root.Has("events")) root.Add("events", JsonNull.Instance);
        if (version < 11 && !root.Has("trainingBoosts")) root.Add("trainingBoosts", new JsonArray());
        // v12（2026-08-15）: 番狂わせ名声デルタの持ち越し。旧セーブは今年度ぶんが無い＝空配列（順当扱い）。
        if (version < 12 && !root.Has("managerPendingUpsetDeltas")) root.Add("managerPendingUpsetDeltas", new JsonArray());
        // v13（issue wire-ai-school-evolve-into-game）: AI校の進化後 Strength/Fame と今年度の校別勝数。
        // 旧セーブは進化が一度も走っていない＝生成値のまま・勝数なし＝どちらも空配列で補う。
        if (version < 13 && !root.Has("nationSchools")) root.Add("nationSchools", new JsonArray());
        if (version < 13 && !root.Has("nationYearWins")) root.Add("nationYearWins", new JsonArray());
        // v14（issue lineup-carry-over-previous-game）: 前回スタメンの記憶。旧セーブは一度も確定していない扱い＝null。
        if (version < 14 && !root.Has("lastLineup")) root.Add("lastLineup", JsonNull.Instance);
        // v15（夏の甲子園）: 全国の県予選スコープ（甲子園の大会展望が引く読み物）と、非対話の甲子園
        // （自校が出場しなかった年の進行体）。旧セーブはどちらも存在しえない＝空配列／null で補う。
        // 自校ぶんの県予選スコープ（stats.prefQualifier）は StatsSaves 側の Has 分岐で読む。
        if (version < 15 && !root.Has("nationPrefQualifierStats")) root.Add("nationPrefQualifierStats", new JsonArray());
        if (version < 15 && !root.Has("koshien")) root.Add("koshien", JsonNull.Instance);
        if (version < 15 && !root.Has("nationKoshienChampionId")) root.Add("nationKoshienChampionId", -1);
        // v16（春のセンバツ）: 観るだけのセンバツ（自校が選ばれていない年の進行体）と、選考発表を待つ32校。
        // 旧セーブはどちらも存在しえない＝null／空配列で補う。
        if (version < 16 && !root.Has("senbatsu")) root.Add("senbatsu", JsonNull.Instance);
        if (version < 16 && !root.Has("pendingSenbatsuFieldIds")) root.Add("pendingSenbatsuFieldIds", new JsonArray());
        if (version < 16 && !root.Has("pendingSenbatsuJinguRegion")) root.Add("pendingSenbatsuJinguRegion", "");
        // v17（センバツの地区大会成績）: 全国の地区大会スコープ（センバツの大会展望が引く読み物）。
        // 旧セーブは存在しえない＝空配列で補う。自校ぶん（stats.regionalQualifier）は StatsSaves 側の
        // Has 分岐で読む。
        if (version < 17 && !root.Has("nationRegionalQualifierStats"))
            root.Add("nationRegionalQualifierStats", new JsonArray());
        // v18（監督の転任システム・設計書04 §1.3）: 監督オファーの保留状態が増えた。
        // 旧セーブは待機が存在しえない＝false/0/空配列で補う。
        if (version < 18)
        {
            if (!root.Has("managerTransferPending")) root.Add("managerTransferPending", false);
            if (!root.Has("managerTransferKind")) root.Add("managerTransferKind", 0);
            if (!root.Has("managerTransferCandidates")) root.Add("managerTransferCandidates", new JsonArray());
        }
        // v19（issue autumn-chain-placements-not-saved）: 大会連鎖の中継値（県大会→甲子園／
        // 県大会→地区大会→明治神宮→センバツ選考）が増えた。旧セーブは一時値として捨てられていて
        // 作り直せないので「未確定」＝null／空配列で補う。ロード直後に連鎖の後段へ入る局面
        // （地区大会・明治神宮の最中のセーブ）だけは旧セーブでは救えず、従来どおり選考が飛ぶ。
        if (version < 19)
        {
            if (!root.Has("lastPrefChampionId")) root.Add("lastPrefChampionId", JsonNull.Instance);
            if (!root.Has("lastAutumnPlacementIds")) root.Add("lastAutumnPlacementIds", new JsonArray());
            if (!root.Has("lastRegionalChampionId")) root.Add("lastRegionalChampionId", JsonNull.Instance);
            if (!root.Has("lastRegionalPlacementIds")) root.Add("lastRegionalPlacementIds", new JsonArray());
            if (!root.Has("lastJinguChampionId")) root.Add("lastJinguChampionId", JsonNull.Instance);
        }
        // v20（issue autumn-district-tournament-not-wired）: 秋季地区予選（AutumnDistrict）→
        // 秋季県大会の中継値が増えた。旧セーブは地区予選という段階が存在しえない＝未確定null で補う
        // （v19 の lastPrefChampionId 等と同じ理由・同じ扱い）。
        if (version < 20 && !root.Has("lastDistrictChampionId"))
            root.Add("lastDistrictChampionId", JsonNull.Instance);
        // v21（issue #641）: 転任で全国に残った学校・監督が率いる生成校。旧セーブは転任で旧校が消えた
        // 状態のまま（差分なし）で読む＝その後の転任から新しい挙動になる。
        if (version < 21 && !root.Has("nationOverlay"))
            root.Add("nationOverlay", NationOverlayToJson(new NationOverlay()));
        // v22（issue #688）: 秋季地区予選から県大会へ進む校が優勝校1校から上位N校に増えた。旧セーブには
        // 進出校の一覧が無い＝空で補い、DistrictEntry は空なら従来どおり優勝校1校だけを使う。
        if (version < 22 && !root.Has("lastDistrictQualifierIds"))
            root.Add("lastDistrictQualifierIds", new JsonArray());
        // 勝敗投手の概念撤去（監督判断2026-08-27）: 投手成績の "w"/"l" キーを書かなくなった。
        // 版は上げない＝項目が減っただけで、旧セーブに残る "w"/"l" は StatsSaves.RestorePitchingInto が
        // 読まずに無視するので支障なく開ける（余分なキーがあるだけ）。
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
            // 採番の続き番号は issue #644 で追加。無い旧セーブは 0＝復元側がロスターと成績から推定する
            // （版は上げない＝欠けても既定値で正しく読める項目の追加）。
            NextPlayerId = root.Has("nextPlayerId") ? root.Int("nextPlayerId") : 0,
            Manager = CareerSaves.ManagerFromJson(root.Obj("manager")),
            Facilities = CareerSaves.FacilitiesFromJson(root.Obj("facilities")),
            // 年契約は v3 で追加。旧セーブ（v1/v2）には無いので、当時と同額・同効果の
            // スカウト網 Lv1 で読む＝その進行の収支を後から変えない（新規ゲームの既定は未契約だが別物）。
            Contracts = root.Has("contracts")
                ? CareerSaves.ContractsFromJson(root.Obj("contracts"))
                : ContractSet.LegacyScoutingLevel1(),
            ManagerLastGrownYear = root.Int("managerLastGrownYear"),
            ManagerGrownWinsBaseline = root.Int("managerGrownWinsBaseline"),
            ManagerGrownKoshienBaseline = root.Int("managerGrownKoshienBaseline"),
            // 番狂わせデルタは v12 で追加。Migrate が旧セーブへ空配列を補うので素直に読む。
            ManagerPendingUpsetDeltas = DoublesFromJson(root.Arr("managerPendingUpsetDeltas")),
            NationRosters = NationRostersFromJson(root.Arr("nationRosters")),
            // AI校の進化状態・年間勝数は v13 で追加。Migrate が旧セーブへ空配列を補うので素直に読む。
            NationSchools = NationSchoolsFromJson(root.Arr("nationSchools")),
            NationYearWins = IntMapFromJson(root.Arr("nationYearWins")),
            // 甲子園王者は v15 で追加。Migrate が旧セーブへ -1（未確定）を補うので素直に読む。
            NationKoshienChampionId = root.Int("nationKoshienChampionId"),
            SessionYear = root.Int("sessionYear"),
            NewTeamPending = root.Bool("newTeamPending"),
            NewTeamRetiredNames = RetiredFromJson(root.Arr("newTeamRetired")),
            // 監督オファー待ちは v18 で追加。Migrate が旧セーブへ false/0/空配列を補うので素直に読む。
            ManagerTransferPending = root.Bool("managerTransferPending"),
            ManagerTransferKind = root.Int("managerTransferKind"),
            ManagerTransferCandidates = TransferCandidatesFromJson(root.Arr("managerTransferCandidates")),
            TeamColorOverride = CareerSaves.TeamColorsFromJson(root["teamColorOverride"]),
            // 自校の同定は v4 で追加。Migrate が旧セーブへ既定（桜丘・神奈川・地区なし）を補うので素直に読む。
            SchoolName = root.Str("schoolName") ?? "桜丘",
            PrefectureId = root.Int("prefectureId"),
            DistrictId = root["districtId"] is JsonNull ? (int?)null : root.Int("districtId"),
            // 転任で全国に残った学校は v21 で追加。Migrate が旧セーブへ空の差分を補うので素直に読む。
            NationOverlay = NationOverlayFromJson(root.Obj("nationOverlay")),
            // 入部予定者は v5 で追加。Migrate が旧セーブへ null を補うので素直に読む。
            Incoming = IncomingClassSaves.FromJson(root.Has("incoming") ? root["incoming"] : null),
            // 能動勧誘は v6 で追加。Migrate が旧セーブへ null を補うので素直に読む。
            Recruiting = RecruitingSaves.FromJson(root.Has("recruiting") ? root["recruiting"] : null),
            // 週次イベントの発火状態は v9 で追加。Migrate が旧セーブへ null を補うので素直に読む。
            Events = EventSaves.FromJson(root.Has("events") ? root["events"] : null),
            // 練習効率の補正は v10 で追加。Migrate が旧セーブへ空配列を補うので素直に読む。
            TrainingBoosts = EventSaves.BoostsFromJson(root.Arr("trainingBoosts")),
            Tournament = TournamentSaves.FromJson(root.Has("tournament") ? root["tournament"] : null),
            // 非対話の甲子園（自校が出場していない年）は v15 で追加。Migrate が旧セーブへ null を補うので素直に読む。
            Koshien = TournamentSaves.FromJson(root.Has("koshien") ? root["koshien"] : null),
            // 非対話のセンバツ・選考発表待ち32校は v16 で追加。Migrate が旧セーブへ null/空配列を補うので素直に読む。
            Senbatsu = TournamentSaves.FromJson(root.Has("senbatsu") ? root["senbatsu"] : null),
            PendingSenbatsuFieldIds = IntsFromJson(root.Arr("pendingSenbatsuFieldIds")),
            LastPrefChampionId = NullableIntFromJson(root, "lastPrefChampionId"),
            LastAutumnPlacementIds = IntsFromJson(root.Arr("lastAutumnPlacementIds")),
            LastDistrictChampionId = NullableIntFromJson(root, "lastDistrictChampionId"),
            LastDistrictQualifierIds = IntsFromJson(root.Arr("lastDistrictQualifierIds")),
            LastRegionalChampionId = NullableIntFromJson(root, "lastRegionalChampionId"),
            LastRegionalPlacementIds = IntsFromJson(root.Arr("lastRegionalPlacementIds")),
            LastJinguChampionId = NullableIntFromJson(root, "lastJinguChampionId"),
            PendingSenbatsuJinguRegion = root.Has("pendingSenbatsuJinguRegion") ? (root.Str("pendingSenbatsuJinguRegion") ?? "") : "",
            // 前回スタメンは v14 で追加。Migrate が旧セーブへ null を補うので素直に読む。
            LastLineup = LineupSaves.FromJson(root["lastLineup"]),
        };

        s.TournamentStats = new NationTournamentStats();
        StatsSaves.RestoreInto(s.TournamentStats, root.Arr("nationTournamentStats"));
        // 通算スコープは v9 で追加。Migrate が旧セーブへ空配列を補うので素直に読む。
        StatsSaves.RestoreCareerInto(s.TournamentStats, root.Arr("nationCareerStats"));
        // 県予選スコープは v15 で追加。Migrate が旧セーブへ空配列を補うので素直に読む。
        StatsSaves.RestorePrefQualifierInto(s.TournamentStats, root.Arr("nationPrefQualifierStats"));
        // 地区大会スコープは v17 で追加。Migrate が旧セーブへ空配列を補うので素直に読む。
        StatsSaves.RestoreRegionalQualifierInto(s.TournamentStats, root.Arr("nationRegionalQualifierStats"));

        s.Stats = new PlayerStatStore();
        StatsSaves.RestoreInto(s.Stats, root.Obj("stats"));

        s.Records = new SchoolRecordBook();
        RecordBookSaves.RestoreInto(s.Records, root.Arr("records"));

        // 教え子名鑑は v7 で追加。Migrate が旧セーブへ空配列を補うので素直に読む。
        s.Draft = new DraftHistory();
        DraftSaves.RestoreInto(s.Draft, root.Arr("draft"));

        // 歴代大会結果は v8 で追加。Migrate が旧セーブへ空配列を補うので素直に読む。
        s.TournamentHistory = new TournamentHistory();
        TournamentHistorySaves.RestoreInto(s.TournamentHistory, root.Arr("tournamentHistory"));

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

    // 全国への差分（v21・issue #641）: { managedSchoolId, added: [[id, name, pref, district, strength, fame], ...] }。
    private static JsonObject NationOverlayToJson(NationOverlay o)
    {
        var added = new List<AddedSchool>(o.Added);
        added.Sort((a, b) => a.Id.CompareTo(b.Id));
        var arr = new JsonArray();
        foreach (var a in added)
            arr.Add(new JsonArray()
                .Add(JsonValue.Of(a.Id)).Add(JsonValue.Of(a.Name)).Add(JsonValue.Of(a.PrefectureId))
                .Add(a.DistrictId.HasValue ? JsonValue.Of(a.DistrictId.Value) : JsonNull.Instance)
                .Add(JsonValue.Of(a.Strength)).Add(JsonValue.Of(a.Fame)));
        return new JsonObject()
            .Add("managedSchoolId", o.ManagedSchoolId.HasValue ? JsonValue.Of(o.ManagedSchoolId.Value) : JsonNull.Instance)
            .Add("added", arr);
    }

    private static NationOverlay NationOverlayFromJson(JsonObject obj)
    {
        var o = new NationOverlay
        {
            ManagedSchoolId = obj["managedSchoolId"] is JsonNull ? (int?)null : obj.Int("managedSchoolId"),
        };
        foreach (var v in obj.Arr("added").Items)
        {
            var t = v.AsArray();
            o.Added.Add(new AddedSchool(
                ((JsonNumber)t[0]).AsInt(), ((JsonString)t[1]).Value, ((JsonNumber)t[2]).AsInt(),
                t[3] is JsonNull ? (int?)null : ((JsonNumber)t[3]).AsInt(),
                ((JsonNumber)t[4]).AsDouble(), ((JsonNumber)t[5]).AsDouble()));
        }
        return o;
    }

    // AI校の進化状態（v13）: [[id, strength, fame], ...] を校ID順で。4000校×3値なので配列の配列で詰める。
    private static JsonArray NationSchoolsToJson(List<SchoolEvolutionState> states)
    {
        var sorted = new List<SchoolEvolutionState>(states);
        sorted.Sort((a, b) => a.SchoolId.CompareTo(b.SchoolId));
        var arr = new JsonArray();
        foreach (var st in sorted)
            arr.Add(new JsonArray().Add(JsonValue.Of(st.SchoolId)).Add(JsonValue.Of(st.Strength)).Add(JsonValue.Of(st.Fame)));
        return arr;
    }

    private static List<SchoolEvolutionState> NationSchoolsFromJson(JsonArray arr)
    {
        var list = new List<SchoolEvolutionState>(arr.Count);
        foreach (var v in arr.Items)
        {
            var t = v.AsArray();
            list.Add(new SchoolEvolutionState(
                ((JsonNumber)t[0]).AsInt(), ((JsonNumber)t[1]).AsDouble(), ((JsonNumber)t[2]).AsDouble()));
        }
        return list;
    }

    // 校ID→整数（年間勝数）: [[id, n], ...] を校ID順で。
    private static JsonArray IntMapToJson(Dictionary<int, int> map)
    {
        var ids = new List<int>(map.Keys);
        ids.Sort();
        var arr = new JsonArray();
        foreach (var id in ids) arr.Add(new JsonArray().Add(JsonValue.Of(id)).Add(JsonValue.Of(map[id])));
        return arr;
    }

    private static Dictionary<int, int> IntMapFromJson(JsonArray arr)
    {
        var map = new Dictionary<int, int>(arr.Count);
        foreach (var v in arr.Items)
        {
            var t = v.AsArray();
            map[((JsonNumber)t[0]).AsInt()] = ((JsonNumber)t[1]).AsInt();
        }
        return map;
    }

    private static JsonArray DoublesToJson(List<double> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(JsonValue.Of(v));
        return arr;
    }

    private static List<double> DoublesFromJson(JsonArray arr)
    {
        var list = new List<double>(arr.Count);
        foreach (var v in arr.Items) list.Add(((JsonNumber)v).AsDouble());
        return list;
    }

    // 校ID列（v16・センバツ選考発表待ちの32校）: [id, id, ...]。
    private static JsonArray IntsToJson(List<int> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(JsonValue.Of(v));
        return arr;
    }

    private static List<int> IntsFromJson(JsonArray arr)
    {
        var list = new List<int>(arr.Count);
        foreach (var v in arr.Items) list.Add(((JsonNumber)v).AsInt());
        return list;
    }

    // 校ID1つ（v19・大会連鎖の中継値）。null＝未確定なので JSON null と往復させる。
    private static JsonValue NullableIntToJson(int? value)
        => value.HasValue ? JsonValue.Of(value.Value) : (JsonValue)JsonNull.Instance;

    private static int? NullableIntFromJson(JsonObject root, string key)
        => !root.Has(key) || root[key] is JsonNull ? (int?)null : root.Int(key);

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

    // 監督オファーの候補校（v18）: [id, name, prefectureId, districtId|null, strength, fame] の配列。
    private static JsonArray TransferCandidatesToJson(List<TransferCandidate> candidates)
    {
        var arr = new JsonArray();
        foreach (var cand in candidates)
            arr.Add(new JsonArray()
                .Add(JsonValue.Of(cand.SchoolId))
                .Add(JsonValue.Of(cand.SchoolName))
                .Add(JsonValue.Of(cand.PrefectureId))
                .Add(cand.DistrictId.HasValue ? JsonValue.Of(cand.DistrictId.Value) : JsonNull.Instance)
                .Add(JsonValue.Of(cand.Strength))
                .Add(JsonValue.Of(cand.Fame)));
        return arr;
    }

    private static List<TransferCandidate> TransferCandidatesFromJson(JsonArray arr)
    {
        var list = new List<TransferCandidate>(arr.Count);
        foreach (var v in arr.Items)
        {
            var t = v.AsArray();
            list.Add(new TransferCandidate(
                SchoolId: ((JsonNumber)t[0]).AsInt(),
                SchoolName: ((JsonString)t[1]).Value,
                PrefectureId: ((JsonNumber)t[2]).AsInt(),
                DistrictId: t[3] is JsonNull ? (int?)null : ((JsonNumber)t[3]).AsInt(),
                Strength: ((JsonNumber)t[4]).AsDouble(),
                Fame: ((JsonNumber)t[5]).AsDouble()));
        }
        return list;
    }
}
