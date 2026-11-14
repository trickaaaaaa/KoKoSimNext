using KokoSim.Engine.Match.Pitching;

namespace KokoSim.Engine.Match.Batting;

/// <summary>
/// 判定が読む「変化量」の定義（issue #911・設計書02 §2.1／設計書15 Phase E-2）。<b>判定の入口はここだけ</b>
/// （空振り・チェイス・打球質・タイミング・慣れの割引は <see cref="BreakM"/>、打球角度は <see cref="LaunchAngleShiftDeg"/>）。
///
/// <para>打者はストレートの軌道に合わせて待つ。よって変化球の欺き＝<b>同じ投手のストレートからの外れ</b>:
/// 本塁での縦の外れ（誘発縦変化の差＋球速差で増えた重力落下 g/2·(T²−T_F²)）と横の外れ（誘発横変化の差）の合成。
/// 判定量は「そのストレートの合成量（従来の欺き）＋重み×外れ」で、ストレート自身（<see cref="PitchTrajectoryFeatures.Fastball"/>
/// が null）は従来の合成量そのまま＝ストレートだけの打席は1ビットも変わらない。</para>
///
/// <para>以前は無回転の球からの変化量（<see cref="PitchTrajectoryFeatures.BreakMagnitudeM"/>）をそのまま使っていたため、
/// 回転の少ない実物どおりのフォーク（ホップしない速球）は「遅くてホップも小さい球」になり、打たれる球になっていた。</para>
/// </summary>
public static class PitchDeception
{
    /// <summary>重力加速度[m/s²]（<see cref="Aerodynamics.Gravity"/> の既定と同じ）。</summary>
    private const double Gravity = 9.81;

    /// <summary>本塁でのストレートからの縦の外れ[m]（上が正）。誘発縦変化の差＋飛翔時間の差による重力落下の差。
    /// ストレート自身は0。</summary>
    public static double RelativeVerticalM(PitchTrajectoryFeatures f)
        => f.Fastball is { } fb
            ? (f.InducedVerticalBreakM - fb.InducedVerticalBreakM)
              - Gravity / 2.0 * (f.FlightTimeSeconds * f.FlightTimeSeconds - fb.FlightTimeSeconds * fb.FlightTimeSeconds)
            : 0.0;

    /// <summary>本塁でのストレートからの横の外れ[m]。ストレート自身は0。</summary>
    public static double RelativeHorizontalM(PitchTrajectoryFeatures f)
        => f.Fastball is { } fb ? f.InducedHorizontalBreakM - fb.InducedHorizontalBreakM : 0.0;

    /// <summary>ストレートからの外れの合成量[m]。ストレート自身は0。</summary>
    public static double RelativeMagnitudeM(PitchTrajectoryFeatures f)
    {
        var v = RelativeVerticalM(f);
        var h = RelativeHorizontalM(f);
        return System.Math.Sqrt(v * v + h * h);
    }

    /// <summary>
    /// 判定に使う変化量[m]。ストレート＝従来の合成量。変化球＝ストレートの合成量＋
    /// <see cref="BattingCoefficients.RelativeBreakWeight"/>×ストレートからの外れ。
    /// </summary>
    public static double BreakM(PitchTrajectoryFeatures f, BattingCoefficients coeff)
        => f.Fastball is { } fb
            ? fb.BreakMagnitudeM + coeff.RelativeBreakWeight * RelativeMagnitudeM(f)
            : f.BreakMagnitudeM;

    /// <summary>
    /// 球種の縦の外れによる打球角度シフト[deg]。ストレート＝従来どおり
    /// <see cref="BattingCoefficients.LaunchAnglePerVerticalBreakM"/>×(誘発縦変化−基準)。変化球＝そのストレートの同じ値＋
    /// <see cref="BattingCoefficients.LaunchAnglePerRelativeDropM"/>×縦の外れ（ストレートより沈む球は上を叩いてゴロになる）。
    /// </summary>
    public static double LaunchAngleShiftDeg(PitchTrajectoryFeatures f, BattingCoefficients coeff)
        => f.Fastball is { } fb
            ? coeff.LaunchAnglePerVerticalBreakM * (fb.InducedVerticalBreakM - coeff.LaunchAngleBreakRefM)
              + coeff.LaunchAnglePerRelativeDropM * RelativeVerticalM(f)
            : coeff.LaunchAnglePerVerticalBreakM * (f.InducedVerticalBreakM - coeff.LaunchAngleBreakRefM);
}
