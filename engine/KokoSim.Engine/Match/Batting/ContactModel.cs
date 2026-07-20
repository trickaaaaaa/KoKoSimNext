using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Batting;

/// <summary>スイング結果。</summary>
public enum ContactOutcome
{
    Whiff,
    Foul,
    InPlay,
}

/// <summary>
/// コンタクト判定と打球生成（設計書01 §2④⑤）。ミート vs 球威で空振り/ファウル/フェアを決め、
/// フェア時は初速・角度・方向を物理層で生成する。
/// </summary>
public static class ContactModel
{
    /// <summary>
    /// 空振り確率（設計書15 Phase E-2）。ミートが下げ、球威（腕の振り＋捕手リード）と
    /// 弾道由来の誘発変化合成量（<see cref="PitchTrajectoryFeatures.BreakMagnitudeM"/>）が上げる。
    /// ゾーン外スイングは当てにくい。テスト（物理妥当性の単調性）から直接呼べるよう Resolve と分離。
    /// </summary>
    public static double WhiffProbability(
        BatterAttributes batter,
        PitchPlan plan,
        PitchTrajectoryFeatures features,
        bool inZone,
        BattingCoefficients coeff)
    {
        var z = coeff.WhiffIntercept
                - coeff.WhiffContactSlope * (batter.Contact - 50)
                + plan.Stuff
                + coeff.WhiffBreakSlope * features.BreakMagnitudeM;
        if (!inZone)
        {
            z += coeff.WhiffOutOfZonePenalty;
        }
        return MathUtil.Logistic(z);
    }

    public static (ContactOutcome Outcome, BattedBall? Ball) Resolve(
        BatterAttributes batter,
        PitchPlan plan,
        PitchTrajectoryFeatures features,
        bool inZone,
        BattingCoefficients coeff,
        IRandomSource rng)
    {
        var whiffProb = WhiffProbability(batter, plan, features, inZone, coeff);
        if (MathUtil.Chance(whiffProb, rng))
        {
            return (ContactOutcome.Whiff, null);
        }

        // 当てたうち一定割合はファウル。
        if (MathUtil.Chance(coeff.FoulShare, rng))
        {
            return (ContactOutcome.Foul, null);
        }

        // フェア: コンタクト品質 → 初速・角度・方向。バレル(品質>1)で満初速を少し越える。
        var quality = MathUtil.Clamp(
            coeff.QualityMean + coeff.QualityContactSlope * (batter.Contact - 50)
                + rng.NextGaussian(0, coeff.QualitySigma),
            0.05, 1.05);

        var maxExitKmh = coeff.ExitVeloInterceptKmh + batter.Power * coeff.ExitVeloPerPower;
        var veloFactor = coeff.MinQualityVeloFactor + (1.0 - coeff.MinQualityVeloFactor) * quality;
        var exitMps = maxExitKmh * veloFactor / 3.6;

        // 品質が低いほど角度が乱れる（ミスショットのゴロ・ポップ）。
        var angleSigma = coeff.LaunchAngleSigma * (1.0 + (1.0 - quality));
        var launchAngle = coeff.MeanLaunchAngle(batter.LaunchTendency) + rng.NextGaussian(0, angleSigma);
        var bearing = rng.NextGaussian(0, coeff.BearingSigma);

        var ball = new BattedBall
        {
            ExitVelocityMps = exitMps,
            LaunchAngleDeg = launchAngle,
            BearingDeg = bearing,
        };
        return (ContactOutcome.InPlay, ball);
    }
}
