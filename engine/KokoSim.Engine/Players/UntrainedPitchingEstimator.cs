using KokoSim.Engine.Core;

namespace KokoSim.Engine.Players;

/// <summary>
/// 投手として置かれていない選手の投手能力の推定（issue #209 Phase B／#381 で改称）。
/// 全選手が <see cref="PitcherAttributes"/> を持つ（#366「投手は配置であって属性ではない」）ので、
/// マウンド外の選手にも本人の能力を詰める必要がある。自校＝<see cref="Season.RosterTeamBuilder"/> は
/// 育成レイヤの真の投手レベルをそのまま投影できるが、AI校＝<see cref="Nation.StrengthTeamFactory"/> の
/// 非マウンド選手には材料が無いので、運動能力（肩）から見積もる。制球・スタミナ・キレは
/// 「投げ慣れていない」前提で保守的に低く置く＝低いだけで、欠けてはいない。
/// </summary>
/// <remarks>
/// 決定論・帯不変: 呼び出し側は必ず Fork したストリームを渡す（主RNGの消費列を変えない）。
/// この能力は継投人選の評価と実登板時のみ参照され、通常の打席解決には一切流れない。
/// </remarks>
public static class UntrainedPitchingEstimator
{
    // 肩の何割が球速レベルへ乗るか。強肩の野手ほど速い球を投げられる（が本職投手ほどではない）。
    private const double ArmToVelocityFactor = 0.75;
    // 投げ慣れていない前提の各次元のベース水準（低め）。個体差を Fork 乱数で少し散らす。
    private const int UntrainedControlBase = 30;
    private const int UntrainedStaminaBase = 25;
    private const int UntrainedPitchRankBase = 28;

    /// <summary>
    /// 肩の強さ（1〜100）から潜在投手能力を推定する。<paramref name="forked"/> は Fork 済みの
    /// 専用ストリーム（主RNG非消費）。制球・スタミナ・キレは保守的に低く、個体差だけ散らす。
    /// </summary>
    public static PitcherAttributes Estimate(int armStrength, IRandomSource forked)
    {
        var velocityLevel = Clamp(armStrength * ArmToVelocityFactor + forked.NextGaussian(0, 6));
        var control = Clamp(UntrainedControlBase + forked.NextGaussian(0, 8));
        var staminaLevel = Clamp(UntrainedStaminaBase + forked.NextGaussian(0, 6));
        var pitchRank = Clamp(UntrainedPitchRankBase + forked.NextGaussian(0, 8));

        return new PitcherAttributes
        {
            MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velocityLevel),
            Control = control,
            StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staminaLevel),
            PitchRank = pitchRank,
            // レパートリーはストレートのみ（未指定→ EffectiveRepertoire がストレートを返す）。
        };
    }

    private static int Clamp(double v) => (int)MathUtil.Clamp(System.Math.Round(v), 1, 100);
}
