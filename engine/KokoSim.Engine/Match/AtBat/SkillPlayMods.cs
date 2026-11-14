using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.AtBat;

/// <summary>
/// スキルによる1打席の挙動補正（設計書10）。能力値やコントロールσで表せない「行動特性・球質」だけをここに載せる。
/// 数値の補正（目慣れのミート増・荒れ球のコントロール減など）は実効能力側で適用済みで、ここには含めない。
/// 既定 <see cref="None"/> は完全無効果＝スキルなしの打席と1ビットも変わらない。
/// </summary>
public readonly record struct SkillPlayMods(
    double FirstPitchSwingProb, // 初球から振る: 初球にスイングを仕掛ける確率（0=スキルなし）
    double BearingPullFactor,   // 流し打ち（旧 広角打法）: 引っ張りバイアスの縮小倍率（<1＝どこへでも打ち返す）
    double FoulShareFactor,     // 粘り打ち: ファウル率の拡大倍率
    double StuffBonus)          // クセ球/荒れ球/火消し/立ち上がり難: 球速に依らない球威の上乗せ（負もあり）
{
    /// <summary>速球打ち（issue #907）: ストレート系の球に対するミートの加算[能力点]。0＝スキルなし。</summary>
    public double FastballContactBonus { get; init; }

    /// <summary>変化球打ち（issue #907）: 変化球に対するミートの加算[能力点]。0＝スキルなし。</summary>
    public double BreakingContactBonus { get; init; }

    /// <summary>変化球打ち（issue #907）: 変化球に対する選球眼の加算[能力点]。0＝スキルなし。</summary>
    public double BreakingDisciplineBonus { get; init; }

    /// <summary>流し打ち・つなぎの打撃（issue #907）: 打った打球の方向・質の補正。既定＝恒等。</summary>
    public BattedBallSkillMods BattedBall { get; init; }

    public static SkillPlayMods None => new(0.0, 1.0, 1.0, 0.0);

    public bool IsIdentity
        => FirstPitchSwingProb == 0.0 && BearingPullFactor == 1.0 && FoulShareFactor == 1.0 && StuffBonus == 0.0
           && FastballContactBonus == 0.0 && BreakingContactBonus == 0.0 && BreakingDisciplineBonus == 0.0
           && BattedBall.IsIdentity;

    /// <summary>ストレート系（速球打ちが効く球）。ストレート・ツーシーム。それ以外は変化球（変化球打ちが効く球）。</summary>
    public static bool IsFastballFamily(PitchType t) => t is PitchType.Fastball or PitchType.TwoSeam;

    /// <summary>
    /// この球の球種に応じて実効打者能力を補正する（速球打ち・変化球打ち）。補正が無ければ同じインスタンスを返す。
    /// 乱数は使わない＝判定の乱数消費順は変わらない。
    /// </summary>
    public BatterAttributes ForPitch(BatterAttributes b, PitchType type)
    {
        if (IsFastballFamily(type))
        {
            if (FastballContactBonus == 0.0) return b;
            return b with { Contact = Clamp(b.Contact + FastballContactBonus) };
        }
        if (BreakingContactBonus == 0.0 && BreakingDisciplineBonus == 0.0) return b;
        return b with
        {
            Contact = Clamp(b.Contact + BreakingContactBonus),
            Discipline = Clamp(b.Discipline + BreakingDisciplineBonus),
        };
    }

    private static int Clamp(double v) => System.Math.Clamp((int)System.Math.Round(v), 1, 100);
}
