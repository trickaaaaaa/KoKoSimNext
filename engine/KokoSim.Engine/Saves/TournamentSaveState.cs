using System.Collections.Generic;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 大会モードの保存分（issue #565 F1）。<see cref="SaveState.Tournament"/> が null＝通常モードのセーブ
/// （v1 のセーブは常に null＝後方互換）。
///
/// <para><b>試合と試合の間だけ</b>保存する。観戦中（<c>MatchProgression</c> を持つ）の中断セーブは F2 で、
/// ここには載せない（<see cref="TournamentRunner.CanCaptureState"/>）。</para>
///
/// <para><b>結果モーダルの表示待ち・大会終了の後始末待ち・試合開始フローの途中は保存しない</b>
/// （Shell の <c>CanSave</c> が抑止する）。これらは「まだ確定していない副作用」を抱えた状態で、
/// 保存すると復元後に週送りや通知が失われる＝進行が詰まる。よってこの DTO にも載せない。</para>
///
/// <para>スタメン（<c>GameSession.Lineup</c>）も載せない。試合は必ずスタメン設定画面を通ってから
/// 始まる（ホームの「試合日→はい」→ LineupSetting →対戦カード→試合開始）ので、復元後に組み直される。</para>
/// </summary>
public sealed class TournamentSaveState
{
    /// <summary>大会種別（夏の県大会 / 秋季県大会）。</summary>
    public TournamentKind Kind { get; set; }

    /// <summary>大会名（開幕バナー・見出しの表記）。</summary>
    public string Title { get; set; } = "";

    /// <summary>大会内経過日（開幕=0日目）。次戦までの残り日数を決める。</summary>
    public int TournamentDay { get; set; }

    /// <summary>開幕演出をUIがまだ出していないか。</summary>
    public bool BannerPending { get; set; }

    /// <summary>今大会の出場校 Id（自校を含む）。大会展望が実際の出場校を引くために持つ。</summary>
    public List<int> FieldSchoolIds { get; set; } = new();

    /// <summary>進行体の可変状態。</summary>
    public TournamentRunnerState Runner { get; set; } = new();
}
