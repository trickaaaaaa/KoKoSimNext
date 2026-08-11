using System.Collections.Generic;
using KokoSim.Engine.Career;
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

    // --- 監督メタ・施設・年契約・監督成長の二重適用ガード ---
    public Manager Manager { get; set; } = new();
    public FacilitySet Facilities { get; set; } = new();
    /// <summary>年契約の契約状態（設計書04 §1.4a）。v3 で追加。</summary>
    public ContractSet Contracts { get; set; } = ContractSet.Initial();
    public int ManagerLastGrownYear { get; set; }
    public int ManagerGrownWinsBaseline { get; set; }
    public int ManagerGrownKoshienBaseline { get; set; }

    // --- 全国（materialize 済みロスターのみ。学校・県は再生成） ---
    public List<AiSchoolRoster> NationRosters { get; set; } = new();
    public NationTournamentStats TournamentStats { get; set; } = new();

    // --- 大会をまたぐ成績・戦績（Normal モードで保存＝進行体は持たない） ---
    public int SessionYear { get; set; } = 1;
    public PlayerStatStore Stats { get; set; } = new();
    public SchoolRecordBook Records { get; set; } = new();

    // --- 新チーム発足待ち ---
    public bool NewTeamPending { get; set; }
    public List<string> NewTeamRetiredNames { get; set; } = new();

    // --- 自校チームカラー上書き（null＝校IDからの自動割り当て） ---
    public TeamColors? TeamColorOverride { get; set; }

    /// <summary>
    /// 大会モードの進行（issue #565 F1）。<b>null＝通常モードのセーブ</b>（v1 のセーブは常に null）。
    /// 大会に入っていても保存できるのは<b>試合と試合の間</b>だけで、観戦中の中断は F2。
    /// </summary>
    public TournamentSaveState? Tournament { get; set; }
}
