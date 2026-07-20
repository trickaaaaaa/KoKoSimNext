using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>打者への1球指示（設計書15 §2.3）。エンドラン/バスター補正は
/// 係数アクセス・走者連動の設計をC-2の判断項目合意と同時に詰めてから追加する。</summary>
public enum PitchBattingOverride
{
    ForceSwing,
    ForceTake,
    /// <summary>送りバント（設計書02 §4.3）。設計書15 Phase D-2b: AtBatSession の投球ループへ統一。</summary>
    Bunt,
    /// <summary>セーフティバント（設計書02 §4.3、走力依存の内野安打狙い）。設計書15 Phase D-2b。</summary>
    SafetyBunt,
    /// <summary>スクイズ（設計書02 §4.4、三塁走者のスタート＋ウエスト読み合い）。設計書15 Phase D-2c。
    /// バント/セーフティバントと異なり打席頭の1球だけで即決着する（継続方針ではない）。</summary>
    Squeeze,
}

/// <summary>
/// 1球采配の判断に渡す状況（設計書15 §2.3）。打席頭の <see cref="TacticsSituation"/> に、
/// カウント・球数・直前の球の結果を足したもの。打撃・スコア等は打席内で不変だが、塁上/アウト数は
/// 設計書15 Phase D-2d（盗塁/牽制/重盗の毎球化）以降、盗塁の成否で打席途中に変わりうる
/// （呼び出し側 GameEngine が毎球フレッシュに Base を作り直す）。
/// </summary>
public readonly record struct PitchTacticsSituation(
    TacticsSituation Base,
    int Balls,
    int Strikes,
    int PitchNumber,
    AtBat.PitchKind? LastPitchOutcome);

/// <summary>
/// 1球采配の上書き指示（設計書15 §2.3）。null=方針まかせ（RNGを1発も引かない）。この球だけ有効、
/// 次球は打席頭の方針（<see cref="DefensiveTactics.Policy"/>/<see cref="Pitching.PitcherGear"/>）に復帰する
/// （Q12-3: 単純上書き）。<see cref="Policy"/> は打席頭の配球方針と同じ語彙を使い、実際の配球ウェイト
/// （<see cref="PitchDirective"/>）への解決は呼び出し側（GameEngine）が行う。
/// </summary>
/// <param name="StealAttempt">
/// この球で一塁走者に盗塁を試みさせるか（設計書15 Phase D-2d）。null=試みない。非null=その始動種別
/// （Normal/Gamble）で試みる。打席頭一度きりだった判断（旧 ITacticsBrain.CallOffense/CallStartType）を
/// 毎球の独立試行へ置き換えたもの。解決自体（PickoffResolver/StealReadModel/StealResolver）は
/// GameEngine がこの指示を受けてから行う（ここでは「試みるか」だけを返す）。
/// </param>
public readonly record struct PitchTacticsDirective(
    PitchBattingOverride? Batting = null,
    PitchPolicy? Policy = null,
    Pitching.PitcherGear? Gear = null,
    StartType? StealAttempt = null);

/// <summary>
/// 1球ごとの采配判断（設計書15 §2.3）。実装は任意（optional interface）。<see cref="ITacticsBrain"/> の
/// 打席頭メソッド群とは別I/Fにすることで、「実装していない brain は窓を開かずRNGを1発も引かない」を
/// 型で保証する（<c>brain is IPitchTacticsBrain</c> の分岐で非実装ならFork自体を生成しない）。
/// </summary>
public interface IPitchTacticsBrain
{
    /// <summary>その1球への上書き指示。null=方針まかせ。</summary>
    PitchTacticsDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng);
}
