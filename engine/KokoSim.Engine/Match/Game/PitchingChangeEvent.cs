using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Game;

/// <summary>継投の種別（issue #209 表現・交代ログ）。表示テキストの出し分けに使う。</summary>
public enum PitchingChangeKind
{
    /// <summary>通常継投（投手→投手・旧投手退場）。</summary>
    BullpenRelief,

    /// <summary>ベンチ野手が投手として登板（旧投手退場・backfillなし）。</summary>
    FielderToMound,

    /// <summary>投野交代（在フィールド野手→マウンド・旧投手は空いた守備位置へ・両者残留）。</summary>
    TwoWaySwap,

    /// <summary>在フィールド野手が登板し、旧投手は退場・空いた守備をベンチ野手が補充（path②）。</summary>
    FielderReliefWithBackfill,
}

/// <summary>
/// 1回の継投の詳細（issue #209 表現）。<see cref="TeamState"/> が交代確定時にイニング非依存で保持する。
/// <see cref="PitchingChangeEvent"/> がこれにイニング・攻守を付けてゲーム全体のログ行にする。
/// Backfill 系（<see cref="PitchingChangeKind.TwoWaySwap"/>／<see cref="PitchingChangeKind.FielderReliefWithBackfill"/>）は
/// 空いた守備位置に入った選手を <see cref="BackfillName"/>／<see cref="BackfillPosition"/> で表す
/// （①=旧投手本人・②=ベンチ野手）。
/// </summary>
public readonly record struct PitchingChangeOutcome(
    PitchingChangeKind Kind,
    string OutPitcherName, int OutPitcherNumber, int? OutPitcherSourceId,
    string InPitcherName, int InPitcherNumber, int? InPitcherSourceId,
    FieldPosition? BackfillPosition, string? BackfillName, int BackfillNumber, int? BackfillSourceId);

/// <summary>
/// 継投1件のログ行（issue #209 表現）。<see cref="IsTopHalf"/> は起きた半イニング（表=先攻の攻撃）、
/// <see cref="DefenseIsAway"/> は継投した（＝守っている）チームが先攻側か。観測専用で試合結果・決定論digestには
/// 影響しない（<see cref="GameResultDigest"/> は本ログを含めない）。
/// </summary>
public sealed record PitchingChangeEvent(int Inning, bool IsTopHalf, bool DefenseIsAway, PitchingChangeOutcome Change);
