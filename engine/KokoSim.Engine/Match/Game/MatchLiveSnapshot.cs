using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// ライブ観戦のスタメン列1行（現在のラインナップの1スロット・交代反映済み）。今日の成績のみ保持し、
/// 通算・背番号・調子は Shell 側で <b>(校ID, <see cref="SourceId"/>)</b> をキーに join する
/// （校IDは試合中不変なので観測データに載せず静的経路で渡す, issue #634）。観測データ＝試合結果に影響しない。
/// </summary>
/// <param name="Order">打順（1-9）。</param>
/// <param name="SourceId">
/// 校内選手ID（両校とも持つ。永続ロスター #80 以降は相手校の生成選手も採番済み）。
/// <b>校をまたいで一意ではない</b>（校ごとに1から振る）ので、成績の join は必ず校IDと組で行う（#634）。
/// </param>
/// <param name="Number">背番号（0=番号なし/ベンチ外・1〜=ベンチ入り）。</param>
/// <param name="Name">選手名。</param>
/// <param name="Position">守備位置（DHスロットは <see cref="FieldPosition.DesignatedHitter"/>）。</param>
/// <param name="Bats">左右打。</param>
/// <param name="AtBats">今日の打数。</param>
/// <param name="Hits">今日の安打。</param>
/// <param name="Rbi">今日の打点。</param>
/// <param name="ReplacedName">このスロットで直近に退いた選手名（交代表示用。無交代なら null）。</param>
/// <param name="ConditionValue">調子の真値（連続値・両校とも）。相手校の観測誤認は Shell 側の
/// <see cref="Players.FormModel.Observe"/> が計算する（設計書02 §3.3, issue #47）。</param>
/// <param name="Grade">学年（1-3）。両校とも <see cref="Player.Grade"/> から引ける（相手校も生成時に採番済み）。</param>
public sealed record LiveBatterSlot(
    int Order, int? SourceId, int Number, string Name, FieldPosition Position, Handedness Bats,
    int AtBats, int Hits, int Rbi, string? ReplacedName, double ConditionValue = 0.0,
    int Grade = 0);

/// <summary>ライブ観戦の現投手の今日の成績。通算防御率は Shell 側で (校ID, <see cref="SourceId"/>) から join する。</summary>
/// <param name="SourceId">校内選手ID（両校とも持つ。校をまたいで一意ではない, issue #634）。</param>
/// <param name="Number">背番号（0=番号なし・1〜=ベンチ入り）。</param>
/// <param name="Name">投手名。</param>
/// <param name="Throws">左右投。</param>
/// <param name="Pitches">今日の球数。</param>
/// <param name="Outs">今日のアウト数（投球回＝Outs/3）。</param>
/// <param name="Runs">今日の失点（RA。自責点は <see cref="EarnedRuns"/> 参照, issue #449）。</param>
/// <param name="StrikeOuts">今日の奪三振。</param>
/// <param name="ConditionValue">調子の真値（連続値・両校とも。相手校の観測誤認は Shell 側, issue #47）。</param>
/// <param name="Grade">学年（1-3）。</param>
/// <param name="ReliefOrder">登板順（1=先発、2=2番手…）。チームの継投回数+1。</param>
/// <param name="StaminaRemaining">
/// スタミナ残量（1=満タン、0=目安投球数を使い切った）。実効消費球数（ギア重み込み）を
/// その投手の目安投球数で割った残り。継投の目安として表示するための観測値で、
/// 実際の継投判定（<see cref="PitchingFatigue.ShouldRelieve"/>）は目安投球数＋マージンで見る（ハードキャップ撤廃, issue #356）。
/// 打席途中は <see cref="PendingPitches"/> をギア倍率抜き（1.0）で加えた近似値（issue #397）。
/// </param>
/// <param name="PendingPitches">
/// 進行中の打席で既にこの投手が投げた球数（未確定＝<see cref="Pitches"/> にはまだ載っていない）。
/// 打席が確定すると engine 集計（<see cref="Pitches"/>）へ吸収されるので UI 側で足し込む
/// （<see cref="LiveLineScore.PendingRuns"/> と同型, issue #397）。守備側の現投手だけに乗り、
/// 攻撃側チームの現投手（今は投げていない）は常に0。
/// </param>
/// <param name="Walks">今日の与四球（issue #407）。</param>
/// <param name="HitByPitch">今日の与死球（issue #407）。表示では与四球と合算して「与四死球」として出す。</param>
/// <param name="EarnedRuns">今日の自責点（issue #449。ライブ防御率＝通算＋本日の合算に使う）。</param>
public sealed record LivePitcherToday(
    int? SourceId, int Number, string Name, Handedness Throws,
    int Pitches, int Outs, int Runs, int StrikeOuts, double ConditionValue = 0.0,
    int Grade = 0, int ReliefOrder = 1, double StaminaRemaining = 1.0, int PendingPitches = 0,
    int Walks = 0, int HitByPitch = 0, int EarnedRuns = 0);

/// <summary>
/// 1チームのライブ観戦スナップショット（現在の打順9人＋現投手＋控え）。
/// <see cref="Reserves"/>=未出場の控え（1本の名簿・交代候補）、
/// <see cref="ReservePitchers"/>=そのうち投手として置かれている者（継投の見通し表示用, issue #381）。
/// </summary>
public sealed record LiveTeamSnapshot(
    IReadOnlyList<LiveBatterSlot> Lineup, LivePitcherToday Pitcher,
    IReadOnlyList<LiveBatterSlot> Reserves, IReadOnlyList<LivePitcherToday> ReservePitchers);

/// <summary>
/// ライブ観戦のラインスコア1チーム分（回別得点＋R/H/E）。設計書16 §4-2 の LineScorePanel が引く観測データ。
/// <see cref="InningRuns"/> は<b>完了した半回だけ</b>を持つ（半回が終わった時点で確定するため）。
/// 進行中の半回で既に入った点は <see cref="PendingRuns"/> に分けてある。UI はこの2つを
/// 「確定セル＋進行中セル」に描き分ければよく、UI側で得点を再計算する必要がない（不変条件: 数値はエンジン集計から）。
/// </summary>
/// <param name="Name">校名。</param>
/// <param name="InningRuns">完了した半回の得点（添字0＝1回）。</param>
/// <param name="PendingRuns">進行中の半回でこれまでに入った点（守備側なら常に0）。</param>
/// <param name="Runs">総得点 R。</param>
/// <param name="Hits">安打 H。</param>
/// <param name="Errors">失策 E。</param>
public sealed record LiveLineScore(
    string Name, IReadOnlyList<int> InningRuns, int PendingRuns, int Runs, int Hits, int Errors);

/// <summary>
/// 試合のライブ観戦スナップショット（両チームのスタメン列・現投手・現在の攻撃側/打者・ラインスコア）。
/// <see cref="Timeline.Playback.MatchProgression.Snapshot"/> が試合中の可変状態から組む観測データ。
/// </summary>
/// <param name="Away">先攻チーム。</param>
/// <param name="Home">後攻チーム。</param>
/// <param name="OffenseIsTop">現在の攻撃側が先攻(表)なら true。</param>
/// <param name="CurrentBatterOrder">現在の打者の打順（1-9。攻撃側列のハイライト用）。</param>
/// <param name="AwayLine">先攻のラインスコア。</param>
/// <param name="HomeLine">後攻のラインスコア。</param>
public sealed record MatchLiveSnapshot(
    LiveTeamSnapshot Away, LiveTeamSnapshot Home, bool OffenseIsTop, int CurrentBatterOrder,
    LiveLineScore AwayLine, LiveLineScore HomeLine);
