using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 1打席のプレー記録（テキスト速報用）。試合結果には影響しない観測データ。
/// </summary>
/// <param name="Inning">イニング（1始まり）。</param>
/// <param name="IsTop">表(先攻の攻撃)なら true、裏(後攻)なら false。</param>
/// <param name="BatterName">打者名。</param>
/// <param name="Result">打席結果。</param>
/// <param name="RunsScored">このプレーで入った得点。</param>
/// <param name="Timeline">プレーのタイムライン（CHANGELOG 32。GameContext.CaptureTimelines オン時のみ非null）。</param>
/// <param name="Pitches">この打席の投球数（中継風カウントの合成に使う。集計はPitchingLine側）。</param>
/// <param name="OutsBefore">打席開始時のアウト数（0-2。BSO表示用）。</param>
/// <param name="BaseFirstBefore">打席前に一塁占有か（塁ダイヤ表示用）。</param>
/// <param name="BaseSecondBefore">打席前に二塁占有か。</param>
/// <param name="BaseThirdBefore">打席前に三塁占有か。</param>
/// <param name="BaseFirstAfter">打席解決後に一塁占有か（結果で塁ダイヤを更新）。</param>
/// <param name="BaseSecondAfter">打席解決後に二塁占有か。</param>
/// <param name="BaseThirdAfter">打席解決後に三塁占有か。</param>
/// <param name="BatterOrder">この打者の打順（1-9。0=不明）。ライブ観戦のスタメン列ハイライト用。</param>
/// <param name="BatterSourceId">打者の選手ID（自校のみ。相手校生成選手は null）。通算成績・背番号の join キー。</param>
/// <param name="BatterBats">打者の左右打（マッチアップHUD表示用）。</param>
/// <param name="BatterPosition">打者の守備位置（スタメン列の相手校背番号フォールバック用）。</param>
/// <param name="PitcherSourceId">対戦投手の選手ID（自校のみ。相手校は null）。</param>
/// <param name="PitcherName">対戦投手名（マッチアップHUD表示用）。</param>
/// <param name="PitcherThrows">対戦投手の左右投（マッチアップHUD表示用）。</param>
/// <param name="PitchLog">この打席の1球ごとの実データ（設計書15 §4）。<see cref="AtBat.AtBatSession"/> を通らない
/// 経路（バント/スクイズ等）は null（統一は Phase D）。</param>
/// <param name="BatterConditionValue">打者の調子の真値（連続値・両校とも。相手校の観測誤認は Shell 側で計算, issue #47）。</param>
/// <param name="PitcherConditionValue">対戦投手の調子の真値（連続値・両校とも）。</param>
/// <remarks>
/// Timeline 以降の追加フィールドはすべて「観測データ」で試合結果に影響しない（判定・乱数順は不変）。
/// うち PitchLog は決定論ゲート GameResultDigest のハッシュ対象（設計書15 §3.2, Phase Bで再ベースライン済み。
/// Trajectory は含めない）。それ以外のフィールドはハッシュ対象外＝足しても SHA 不変。
/// </remarks>
public sealed record PlayLogEntry(
    int Inning, bool IsTop, string BatterName, PlateAppearanceResult Result, int RunsScored,
    PlayTimeline? Timeline = null,
    int Pitches = 0,
    int OutsBefore = 0,
    bool BaseFirstBefore = false, bool BaseSecondBefore = false, bool BaseThirdBefore = false,
    bool BaseFirstAfter = false, bool BaseSecondAfter = false, bool BaseThirdAfter = false,
    int BatterOrder = 0,
    int? BatterSourceId = null, Handedness BatterBats = Handedness.Right, FieldPosition BatterPosition = FieldPosition.Pitcher,
    int? PitcherSourceId = null, string? PitcherName = null, Handedness PitcherThrows = Handedness.Right,
    int BatterNumber = 0, int PitcherNumber = 0,
    IReadOnlyList<PitchRecord>? PitchLog = null,
    double BatterConditionValue = 0.0, double PitcherConditionValue = 0.0);
