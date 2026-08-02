using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Game;

/// <summary>継投の種別（issue #209 表現・交代ログ）。表示テキストの出し分けに使う。</summary>
public enum PitchingChangeKind
{
    /// <summary>
    /// 控えからの継投（旧投手退場・backfillなし）。issue #381 で旧「投手控えからの継投」と
    /// 旧 <c>FielderToMound</c>（ベンチ野手→マウンド）を統合した。控えを投手/野手に二分するのをやめた以上、
    /// 実況上の区別は「控えから出たか／守備に就いていた者が回ったか（＝守備に穴が開くか）」であって、
    /// 出てきた控えが本職かどうかではない。
    /// </summary>
    ReserveRelief,

    /// <summary>投野交代（在フィールド野手→マウンド・旧投手は空いた守備位置へ・両者残留）。</summary>
    TwoWaySwap,

    /// <summary>在フィールド野手が登板し、旧投手は退場・空いた守備をベンチ野手が補充（path②）。</summary>
    FielderReliefWithBackfill,

    /// <summary>
    /// コンバート継投（issue #235）。旧投手を守備位置へ回し、控え投手が登板する。旧投手は退場せず
    /// 打席に立ち続ける（＝再登板も可）。押し出された野手が退場する。
    /// Backfill は「旧投手が就いた守備位置」＝<see cref="PitchingChangeKind.TwoWaySwap"/> と同じ意味で使う。
    /// </summary>
    PitcherConvertedRelief,
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
