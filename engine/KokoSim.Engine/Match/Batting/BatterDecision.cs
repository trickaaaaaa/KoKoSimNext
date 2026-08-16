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
    /// ゾーン内側は追加で <see cref="BattingCoefficients.ZoneEdgeSwingPenalty"/> による縁への近さの減衰を掛け、
    /// 「隅のいい球は見逃されやすい」を表現する（issue #403。中心=恒等・縁上で係数ぶん見送る）。
    /// テストから直接呼べるよう <see cref="DecideSwing"/> と分離。
    /// </summary>
    /// <param name="swingFactor">
    /// 打撃姿勢（<see cref="Tactics.BattingApproach.WorkCount"/>＝待球策）によるスイング頻度の倍率。
    /// 既定 1.0 は恒等＝従来と1ビットも変わらない。能力ではなく「振りにいく頻度」だけを動かす軸なので、
    /// 打者属性ではなく打席コンテキスト側から渡す。
    /// </param>
    /// <param name="zoneEdgeProximity">
    /// ゾーン内の縁への近さ（<see cref="Field.StrikeZone.EdgeProximity"/>、無次元 [0,1]、issue #403）。
    /// ゾーン内でのみ <see cref="BattingCoefficients.ZoneEdgeSwingPenalty"/> を掛けて「隅は見逃されやすい」を表現。
    /// 既定 0.0（＝中心）は恒等。ゾーン外では未使用（チェイス側は距離減衰が同役を担う）。
    /// </param>
    public static double SwingProbability(
        bool inZone,
        double distanceOutsideM,
        double breakMagnitudeM,
        BatterAttributes batter,
        BattingCoefficients coeff,
        double swingFactor = 1.0,
        double zoneEdgeProximity = 0.0,
        int familiarityPa = 0)
    {
        double p;
        if (inZone)
        {
            p = coeff.ZoneSwingBase + (batter.Discipline - 50) * coeff.ZoneSwingDisciplineSlope
                - coeff.ChaseBreakSlope * breakMagnitudeM
                - coeff.ZoneEdgeSwingPenalty * zoneEdgeProximity;
        }
        else
        {
            // 選球眼が高いほど釣られにくい。変化量は釣られを増やすが、ゾーンから遠いほど減衰する。
            // 慣れ（設計書02 §2.3b・issue times-through-order-familiarity）: 同一投手との対戦回数ぶん釣られにくくなる。
            p = coeff.ChaseBase - (batter.Discipline - 50) * coeff.ChaseDisciplineSlope
                + coeff.ChaseBreakSlope * breakMagnitudeM
                - coeff.ChaseDistanceSlope * distanceOutsideM
                - coeff.FamiliarityChasePerPa * ContactModel.FamiliarityEffect(familiarityPa, breakMagnitudeM, coeff);
        }

        return MathUtil.Clamp(p * swingFactor, 0.02, 0.98);
    }

    /// <summary>スイングするか。<paramref name="swingFactor"/>・<paramref name="zoneEdgeProximity"/> の意味は <see cref="SwingProbability"/> を参照。</summary>
    public static bool DecideSwing(
        bool inZone,
        double distanceOutsideM,
        double breakMagnitudeM,
        BatterAttributes batter,
        BattingCoefficients coeff,
        IRandomSource rng,
        double swingFactor = 1.0,
        double zoneEdgeProximity = 0.0,
        int familiarityPa = 0)
        => MathUtil.Chance(
            SwingProbability(inZone, distanceOutsideM, breakMagnitudeM, batter, coeff, swingFactor, zoneEdgeProximity, familiarityPa), rng);
}
