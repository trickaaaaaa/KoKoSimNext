using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// ライブ観戦のスタメン列1行（現在のラインナップの1スロット・交代反映済み）。今日の成績のみ保持し、
/// 通算・背番号・調子は Shell 側で <see cref="SourceId"/> をキーに join する。観測データ＝試合結果に影響しない。
/// </summary>
/// <param name="Order">打順（1-9）。</param>
/// <param name="SourceId">選手ID（自校のみ。相手校生成選手は null）。</param>
/// <param name="Number">背番号（0=番号なし/ベンチ外・1〜=ベンチ入り）。</param>
/// <param name="Name">選手名。</param>
/// <param name="Position">守備位置。</param>
/// <param name="Bats">左右打。</param>
/// <param name="AtBats">今日の打数。</param>
/// <param name="Hits">今日の安打。</param>
/// <param name="Rbi">今日の打点。</param>
/// <param name="ReplacedName">このスロットで直近に退いた選手名（交代表示用。無交代なら null）。</param>
public sealed record LiveBatterSlot(
    int Order, int? SourceId, int Number, string Name, FieldPosition Position, Handedness Bats,
    int AtBats, int Hits, int Rbi, string? ReplacedName);

/// <summary>ライブ観戦の現投手の今日の成績。通算防御率は Shell 側で <see cref="SourceId"/> から join する。</summary>
/// <param name="SourceId">選手ID（自校のみ。相手校は null）。</param>
/// <param name="Number">背番号（0=番号なし・1〜=ベンチ入り）。</param>
/// <param name="Name">投手名。</param>
/// <param name="Throws">左右投。</param>
/// <param name="Pitches">今日の球数。</param>
/// <param name="Outs">今日のアウト数（投球回＝Outs/3）。</param>
/// <param name="Runs">今日の失点（RA。自責点は未追跡）。</param>
/// <param name="StrikeOuts">今日の奪三振。</param>
public sealed record LivePitcherToday(
    int? SourceId, int Number, string Name, Handedness Throws,
    int Pitches, int Outs, int Runs, int StrikeOuts);

/// <summary>
/// 1チームのライブ観戦スナップショット（現在の打順9人＋現投手＋控え）。
/// <see cref="Bench"/>=未出場の野手控え、<see cref="Bullpen"/>=未登板の控え投手（いずれも交代候補）。
/// </summary>
public sealed record LiveTeamSnapshot(
    IReadOnlyList<LiveBatterSlot> Lineup, LivePitcherToday Pitcher,
    IReadOnlyList<LiveBatterSlot> Bench, IReadOnlyList<LivePitcherToday> Bullpen);

/// <summary>
/// 試合のライブ観戦スナップショット（両チームのスタメン列・現投手・現在の攻撃側/打者）。
/// <see cref="Timeline.Playback.MatchProgression.Snapshot"/> が試合中の可変状態から組む観測データ。
/// </summary>
/// <param name="Away">先攻チーム。</param>
/// <param name="Home">後攻チーム。</param>
/// <param name="OffenseIsTop">現在の攻撃側が先攻(表)なら true。</param>
/// <param name="CurrentBatterOrder">現在の打者の打順（1-9。攻撃側列のハイライト用）。</param>
public sealed record MatchLiveSnapshot(
    LiveTeamSnapshot Away, LiveTeamSnapshot Home, bool OffenseIsTop, int CurrentBatterOrder);
