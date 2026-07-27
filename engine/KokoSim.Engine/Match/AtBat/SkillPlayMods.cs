namespace KokoSim.Engine.Match.AtBat;

/// <summary>
/// スキルによる1打席の挙動補正（設計書10）。能力値やコントロールσで表せない「行動特性・球質」だけをここに載せる。
/// 数値の補正（尻上がりのミート増・荒れ球のコントロール減など）は実効能力側で適用済みで、ここには含めない。
/// 既定 <see cref="None"/> は完全無効果＝スキルなしの打席と1ビットも変わらない。
/// </summary>
public readonly record struct SkillPlayMods(
    double FirstPitchSwingProb, // 初球から振る: 初球にスイングを仕掛ける確率（0=スキルなし）
    double BearingPullFactor,   // 広角打法: 引っ張りバイアスの縮小倍率（<1＝どこへでも打ち返す）
    double FoulShareFactor,     // 粘り打ち: ファウル率の拡大倍率
    double StuffBonus)          // クセ球/荒れ球: 球速に依らない球威（空振り誘発）の上乗せ
{
    public static SkillPlayMods None => new(0.0, 1.0, 1.0, 0.0);

    public bool IsIdentity
        => FirstPitchSwingProb == 0.0 && BearingPullFactor == 1.0 && FoulShareFactor == 1.0 && StuffBonus == 0.0;
}
