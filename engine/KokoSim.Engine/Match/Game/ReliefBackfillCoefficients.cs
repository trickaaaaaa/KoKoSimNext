namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 在フィールド野手の緊急登板時の backfill 経路（①投野交代／②旧投手退場＋ベンチ野手補充）の判定係数
/// （issue #209 Phase C path②, 設計書11 §4）。① を既定とし、ベンチ野手が空いた守備位置を旧投手より
/// 明確に上手く守れ、かつ旧投手を残す打撃的価値が低い時だけ ② に倒す。
/// この判定は AI の在フィールド野手登板でのみ走り（Standard／無指示は在フィールド野手を選ばない）、
/// 帯・決定論baseline には影響しない。
/// </summary>
public sealed record ReliefBackfillCoefficients
{
    /// <summary>
    /// ② に倒す守備値の基準差。ベンチ野手の守備値が旧投手をこの値以上上回れば ②（打撃補正前の基準）。
    /// 保守的に大きめ＝① が既定（投野交代を第一候補にする）。
    /// </summary>
    public double BaseFieldingMargin { get; init; } = 10.0;

    /// <summary>
    /// 旧投手の打力による基準差の補正重み。旧投手の打力が平均を上回るほど基準を引き上げ（＝バットを温存＝①寄り）、
    /// 下回るほど引き下げる（＝温存不要＝②寄り）。0 なら守備値だけで判定。
    /// </summary>
    public double BattingRetentionWeight { get; init; } = 0.3;

    /// <summary>打力（ミート・パワーの平均）の基準点。旧投手の打力がこれを下回ると ② に傾く。</summary>
    public double LeagueAverageBatting { get; init; } = 50.0;
}
