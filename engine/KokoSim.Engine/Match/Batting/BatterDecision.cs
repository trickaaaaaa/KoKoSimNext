using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Batting;

/// <summary>打者のスイング判断（設計書01 §2③）。コース×選球眼×弾道由来の「見え方と実軌道のズレ」で決める。</summary>
public static class BatterDecision
{
    /// <summary>
    /// スイング確率（設計書15 Phase E-3）。誘発変化合成量（<see cref="PitchTrajectoryFeatures.BreakMagnitudeM"/>）
    /// は単一係数 <see cref="BattingCoefficients.ChaseBreakSlope"/> を符号だけ反転して対称に効かせる:
    /// ゾーン外は+（釣られやすくなる）、ゾーン内は−（見誤って見送りやすくなる）。ゾーン外側は追加で
    /// <see cref="BattingCoefficients.ChaseDistanceSlope"/> によるゾーンからの距離減衰を必ず掛け、
    /// 「大外れの球が変化量だけで釣れる」という不自然を防ぐ（ゾーン内は距離=0で常に無効）。
    /// テストから直接呼べるよう <see cref="DecideSwing"/> と分離。
    /// </summary>
    public static double SwingProbability(
        bool inZone,
        double distanceOutsideM,
        double breakMagnitudeM,
        BatterAttributes batter,
        BattingCoefficients coeff)
    {
        double p;
        if (inZone)
        {
            p = coeff.ZoneSwingBase + (batter.Discipline - 50) * coeff.ZoneSwingDisciplineSlope
                - coeff.ChaseBreakSlope * breakMagnitudeM;
        }
        else
        {
            // 選球眼が高いほど釣られにくい。変化量は釣られを増やすが、ゾーンから遠いほど減衰する。
            p = coeff.ChaseBase - (batter.Discipline - 50) * coeff.ChaseDisciplineSlope
                + coeff.ChaseBreakSlope * breakMagnitudeM
                - coeff.ChaseDistanceSlope * distanceOutsideM;
        }

        return MathUtil.Clamp(p, 0.02, 0.98);
    }

    /// <summary>スイングするか。</summary>
    public static bool DecideSwing(
        bool inZone,
        double distanceOutsideM,
        double breakMagnitudeM,
        BatterAttributes batter,
        BattingCoefficients coeff,
        IRandomSource rng)
        => MathUtil.Chance(SwingProbability(inZone, distanceOutsideM, breakMagnitudeM, batter, coeff), rng);
}
