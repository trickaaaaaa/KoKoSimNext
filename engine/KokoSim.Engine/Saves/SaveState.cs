using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Saves;

/// <summary>
/// セーブ1本ぶんの中身（セーブ DTO・issue #507）。純C#・<c>UnityEngine</c> 非依存（不変条件#3）。
/// 画面状態・スクリーン遷移など Unity 固有の一時状態は<b>載せない</b>。全国の学校・県は決定論再生成なので
/// 保存せず（School.cs の設計注記）、保存するのは「進行して変化した実体」だけ＝自校部員・監督メタ・施設・
/// materialize 済み全国ロスター・成績/戦績・母種シード・暦。
///
/// <para><b>大会中も試合と試合の間なら保存できる</b>（issue #565 F1。#483 論点3「大会中は不可」の撤回）。
/// 進行体の可変状態は <see cref="Tournament"/> に載る。<b>観戦中（<c>MatchProgression</c> を持つ）の
/// 中断セーブだけは対象外</b>で、これは F2 の話。</para>
///
/// <para>Shell の各 static サービスは <c>Capture()</c> でこの DTO を組み、<c>Restore()</c> で押し戻す。
/// DI が要る <see cref="Nation.Roster.NationRosters"/> は <see cref="Nation.Roster.NationRosters.FromSaved"/> で
/// 復元し、成績/戦績はサービス側の実体へ据える。</para>
/// </summary>
public sealed class SaveState
{
    /// <summary>スキーマ版（先頭・マイグレーション用）。</summary>
    public int Version { get; set; } = SaveCodec.CurrentVersion;

    // --- 乱数マスタ・暦 ---
    public ulong MasterSeed { get; set; }
    public int Week { get; set; }
    public int YearIndex { get; set; } = 1;

    // --- 自校部員（背番号・引退フラグ含む。生成順の全員） ---
    public List<DevelopingPlayer> Roster { get; set; } = new();

    /// <summary>
    /// 自校選手の安定IDの続き番号（issue #644）。0＝未保存（この項目より前のセーブ）＝復元側がロスターと
    /// 成績ストアの最大IDから推定する（<see cref="Season.PlayerIdCounter.Resume"/>）。
    /// </summary>
    public int NextPlayerId { get; set; }

    // --- 監督メタ・施設・年契約・監督成長の二重適用ガード ---
    public Manager Manager { get; set; } = new();
    public FacilitySet Facilities { get; set; } = new();
    /// <summary>年契約の契約状態（設計書04 §1.4a）。v3 で追加。</summary>
    public ContractSet Contracts { get; set; } = ContractSet.Initial();
    public int ManagerLastGrownYear { get; set; }
    public int ManagerGrownWinsBaseline { get; set; }
    public int ManagerGrownKoshienBaseline { get; set; }
    /// <summary>
    /// 今年度の自校公式戦の番狂わせ名声デルタ（試合ごと・<see cref="Career.FameUpsetModel.MatchDelta"/> の値）。
    /// 年度末の監督成長で畳み込んで消費する。v12 で追加（2026-08-15・番狂わせ加点の実ゲーム配線）。
    /// </summary>
    public List<double> ManagerPendingUpsetDeltas { get; set; } = new();

    // --- 全国（materialize 済みロスターのみ。学校・県は再生成） ---
    public List<AiSchoolRoster> NationRosters { get; set; } = new();

    /// <summary>
    /// 決定論で再生成した全国へ重ねる差分（v21・issue #641）。転任で AI校として残った新設校と、
    /// 監督がいま率いている生成校（全国から外す）。
    /// </summary>
    public NationOverlay NationOverlay { get; set; } = new();
    public NationTournamentStats TournamentStats { get; set; } = new();
    /// <summary>
    /// AI校の進化後 Strength/Fame（issue wire-ai-school-evolve-into-game・v13）。<b>空＝一度も年度末を跨いでいない</b>
    /// （旧セーブも空）＝生成値のまま。年度末の <see cref="Nation.AiSchoolEvolution.ApplyYear"/> 以降は全校ぶん載る。
    /// 学校・県の名前は決定論再生成で戻るので、変化する2値だけを持つ。
    /// </summary>
    public List<SchoolEvolutionState> NationSchools { get; set; } = new();
    /// <summary>今年度の校別勝数（年度末の進化で消費・v13）。校ID→勝数。空＝今年度まだ大会が無い。</summary>
    public Dictionary<int, int> NationYearWins { get; set; } = new();

    /// <summary>
    /// 今年度の甲子園王者（校ID・v15）。<c>-1</c>＝未確定／自校が優勝。校別勝数と同じ寿命
    /// （8月に決まり年度末の進化で消費する）なので同じ区画に置く。
    /// </summary>
    public int NationKoshienChampionId { get; set; } = -1;

    // --- 大会をまたぐ成績・戦績（Normal モードで保存＝進行体は持たない） ---
    public int SessionYear { get; set; } = 1;
    public PlayerStatStore Stats { get; set; } = new();
    public SchoolRecordBook Records { get; set; } = new();

    // --- 新チーム発足待ち ---
    public bool NewTeamPending { get; set; }
    public List<string> NewTeamRetiredNames { get; set; } = new();

    // --- 監督オファー待ち（設計書04 §1.3・転任システム。v18 で追加） ---
    /// <summary>年度末の転任判定の結果、プレイヤーの選択待ちか。</summary>
    public bool ManagerTransferPending { get; set; }
    /// <summary><see cref="TransferEventKind"/> の int 値。Pending=false のときは無効。</summary>
    public int ManagerTransferKind { get; set; }
    /// <summary>提示中の移籍先候補（残留可否は <see cref="ManagerTransferKind"/> から導く）。</summary>
    public List<TransferCandidate> ManagerTransferCandidates { get; set; } = new();

    // --- 自校チームカラー上書き（null＝校IDからの自動割り当て） ---
    public TeamColors? TeamColorOverride { get; set; }

    // --- 自校の同定（新設校スタート・issue #585。v4 で追加） ---
    /// <summary>自校の校名（「高校」を含まない幹の部分）。旧セーブは "桜丘" で補う。</summary>
    public string SchoolName { get; set; } = "桜丘";
    /// <summary>本拠県（Prefecture.Id ＝ JIS順0基点）。旧セーブは13＝神奈川で補う。</summary>
    public int PrefectureId { get; set; } = 13;
    /// <summary>県内地区（0基点。地区を持たない県・旧セーブは null）。</summary>
    public int? DistrictId { get; set; }

    // --- 入部予定者（3月確定→4月仮入部・issue #587。v5 で追加） ---
    /// <summary>
    /// 入部予定者の学年1つぶん。<b>null＝まだ一度も3月を跨いでいない</b>（旧セーブ・初年度の途中）。
    /// 4月に合流すると選手の実体は落ちて見立てだけが残る（<see cref="IncomingClass.Joined"/>）。
    /// </summary>
    public IncomingClass? Incoming { get; set; }

    // --- 能動勧誘（寮で解禁・issue #588。v6 で追加） ---
    /// <summary>
    /// 中学生リストと訪問の状態。<b>null＝寮が無い／まだ秋を跨いでいない</b>（旧セーブも null）。
    /// 3月の判定で使い切り、次の秋に新しいリストで上書きされる。
    /// </summary>
    public RecruitingState? Recruiting { get; set; }

    // --- 教え子名鑑（過年度のドラフト指名確定・issue #586。v7 で追加） ---
    /// <summary>
    /// 監督キャリアに紐づく指名実績の台帳（設計書20 §6）。空＝一度も指名が出ていない
    /// （旧セーブは Unity側でドラフトが走っていなかったので必ず空）。
    /// </summary>
    public DraftHistory Draft { get; set; } = new();

    // --- 歴代大会結果（年度別の到達ラウンドと優勝校・issue #600。v8 で追加） ---
    /// <summary>
    /// 自校の歴代大会結果（設計書06 §3.9）。空＝まだ大会を1つも終えていない
    /// （旧セーブは年度別の記録を持っていなかったので必ず空＝過去ぶんは復元できない）。
    /// </summary>
    public TournamentHistory TournamentHistory { get; set; } = new();

    // --- 週次イベントの発火状態（issue #609。v9 で追加） ---
    /// <summary>
    /// クールダウンと年次フラグ（設計書03 §5）。<b>null＝旧セーブ</b>（イベントが週送りに繋がる前の進行）。
    /// 載せないとロードでクールダウンが全部明けて、直前に見たイベントが即もう一度出る。
    /// </summary>
    public EventSchedulerSaveState? Events { get; set; }

    // --- 期間つきの練習効率補正（issue #615。v10 で追加） ---
    /// <summary>
    /// イベントが作った「N週だけ効率が上がる」の残り（設計書03 §5）。空＝補正なし。
    /// 載せないと、強化指定を受けた直後にロードすると効果が消える。
    /// </summary>
    public List<TrainingBoostSaveState> TrainingBoosts { get; set; } = new();

    /// <summary>
    /// 大会モードの進行（issue #565 F1）。<b>null＝通常モードのセーブ</b>（v1 のセーブは常に null）。
    /// 大会に入っていても保存できるのは<b>試合と試合の間</b>だけで、観戦中の中断は F2。
    /// </summary>
    public TournamentSaveState? Tournament { get; set; }

    /// <summary>
    /// <b>自校が出場していない</b>夏の甲子園の進行（v15・設計書05 §1.1a）。null＝開催中でない
    /// （＝自校が出場している年は <see cref="Tournament"/> 側に載る／甲子園が終わった後も null）。
    ///
    /// <para>自校が県予選で敗退した年も甲子園は開催され、大会展望とトーナメント表を閲覧できる。
    /// プレイヤーは通常モードのまま週を進め、その経過日ぶんだけブラケットが消化される
    /// ＝進行体をセーブへ載せないと、ロード後に優勝校が永久に決まらない。</para>
    /// </summary>
    public TournamentSaveState? Koshien { get; set; }

    /// <summary>
    /// 観るだけのセンバツ（自校が選ばれていない年・v16）。<see cref="Koshien"/> と同じ理由でセーブに載せる。
    /// </summary>
    public TournamentSaveState? Senbatsu { get; set; }

    /// <summary>
    /// 選考発表を待つセンバツ32校の校ID（v16）。神宮大会が終わった時点で確定済みだが、実際の大会モードへの
    /// 遷移は選考発表週（<see cref="SeasonCalendar.SenbatsuAnnounceWeek"/>）から＝その間セーブ/ロードを挟んでも
    /// 選考結果が失われないよう保存する。空＝未確定 or 発表済み。
    /// </summary>
    public List<int> PendingSenbatsuFieldIds { get; set; } = new();

    /// <summary>選考発表時点の神宮枠地区（表示用・v16）。空文字＝該当なし。</summary>
    public string PendingSenbatsuJinguRegion { get; set; } = "";

    // --- 大会連鎖の中継値（v19。夏: 県大会→甲子園／秋: 県大会→地区大会→明治神宮→センバツ選考） ---
    //
    // 連鎖の後段は必ずこれらを読む（例: MyRegionPlacementIds が null だとセンバツ選考が丸ごと飛ぶ）。
    // 「大会中は保存できない」前提で一時値として持っていたが、issue #565 F1 で試合と試合の間の保存が
    // 解禁された時点でその前提は崩れていた＝地区大会・明治神宮の最中にロードすると連鎖が切れる
    // （issue autumn-chain-placements-not-saved）。順位は数百バイトなので素直に載せる。

    /// <summary>直近に終わった夏の県大会の優勝校ID（＝自県の甲子園代表）。null＝未確定。</summary>
    public int? LastPrefChampionId { get; set; }

    /// <summary>直近に終わった秋季地区予選の優勝校ID（＝秋季県大会の自地区代表）。null＝未確定、
    /// または自校の県が地区予選を持たない（そのときは秋季県大会が直接始まる）。</summary>
    public int? LastDistrictChampionId { get; set; }

    /// <summary>直近に終わった秋季地区予選の県大会進出校ID（順位順。上位 advance_per_group 校・v22・issue #688）。
    /// 空＝未確定、または旧セーブ（その場合は <see cref="LastDistrictChampionId"/> 1校だけが進出校）。</summary>
    public List<int> LastDistrictQualifierIds { get; set; } = new();

    /// <summary>直近に終わった秋季県大会の順位付き出場校ID（＝地区大会の出場枠判定の材料）。空＝未確定。</summary>
    public List<int> LastAutumnPlacementIds { get; set; } = new();

    /// <summary>直近に終わった地区大会の優勝校ID（＝明治神宮大会の自地区代表）。null＝未確定。</summary>
    public int? LastRegionalChampionId { get; set; }

    /// <summary>直近に終わった地区大会の順位付き出場校ID（＝センバツ選考の自地区ぶんの材料）。空＝未確定。</summary>
    public List<int> LastRegionalPlacementIds { get; set; } = new();

    /// <summary>直近に終わった明治神宮大会の優勝校ID（＝センバツ選考の神宮枠）。null＝未確定。</summary>
    public int? LastJinguChampionId { get; set; }

    /// <summary>
    /// 前の試合で確定したスタメンの記憶（issue lineup-carry-over-previous-game・v14）。スタメン設定画面の既定に使う。
    /// 選手IDで持つ純データ。<b>null＝まだ一度も確定していない</b>（v13 以前のセーブは常に null＝自動シード）。
    /// </summary>
    public LineupMemory? LastLineup { get; set; }
}
