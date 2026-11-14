using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 表示能力（球威・キレ）→ 回転ベクトル（物理層）の変換（設計書02 §2.1・issue #911）。<b>回転の入口はここだけ</b>
/// （判定用の特徴量 <see cref="TrajectoryFeatureTable"/>・観測用の弾道 <see cref="PitchSimulator"/> の両方がここを通る）。
///
/// <para>ストレート: 球威(Power) → 回転数 <c>SpinRpmBase + (Power−50)×SpinRpmPerPower</c>（×ストレートの rpm_scale）、
/// 回転軸はストレートの tilt。</para>
///
/// <para>変化球: <b>キレ＝ストレートからの回転の外れの大きさ</b>（2026-11-14 監督決定）。
/// 球種の基準回転（キレ50の回転＝<c>SpinRpmBase×rpm_scale</c>・球種の tilt）と、その投手のストレートの回転との差
/// Δ だけをキレで伸び縮みさせる:
/// <c>ω = ω_ストレート + g(キレ)·(ω_球種基準 − ω_ストレート)</c>、<c>g = max(0, 1 + (キレ−50)×SharpnessDeviationPerLevel)</c>。
/// キレが上がれば必ずストレートとの回転の差が広がる（回転→誘発変化はほぼ線形なので、変化の差も広がる）。
/// 回転数と回転軸はどちらもキレで連続に動く（例: フォークはキレが高いほど回転が少なくなり、カーブはトップスピンが増える）。</para>
///
/// <para>入力の能力は <see cref="AbilityEffectCoefficients.Pitcher"/> で寝かせ済みの値（<see cref="PitcherAttributes.EffectiveRepertoire"/>
/// を通した写し）を渡す。ここで生の Lv を読み直さない（不変条件#1・設計書21）。</para>
/// </summary>
public static class PitchSpinModel
{
    /// <summary>ストレートの回転ベクトル[rad/s]（球威 <paramref name="power"/> から）。</summary>
    public static Vector3D FastballSpin(PitchingCoefficients c, int power)
    {
        var axis = c.AxisFor(PitchType.Fastball);
        var rpm = (c.SpinRpmBase + (power - 50) * c.SpinRpmPerPower) * axis.RpmScale;
        return PitchSpec.SpinFromRpmAndTilt(rpm, axis.TiltDeg);
    }

    /// <summary>球種の基準回転（キレ50）[rad/s]＝基準回転数×rpm_scale・球種の回転軸。</summary>
    public static Vector3D NominalSpin(PitchingCoefficients c, PitchType type)
    {
        var axis = c.AxisFor(type);
        return PitchSpec.SpinFromRpmAndTilt(c.SpinRpmBase * axis.RpmScale, axis.TiltDeg);
    }

    /// <summary>キレ → ストレートからの外れの倍率 g（キレ50で1・下限0）。</summary>
    public static double DeviationScale(PitchingCoefficients c, int sharpness)
        => System.Math.Max(0.0, 1.0 + (sharpness - 50) * c.SharpnessDeviationPerLevel);

    /// <summary>
    /// 球種の回転ベクトル[rad/s]。ストレートは球威だけで決まる（<paramref name="sharpness"/> は読まない）。
    /// 変化球は <c>ω_ストレート + g(キレ)·(ω_球種基準 − ω_ストレート)</c>（<paramref name="fastballPower"/> はその投手のストレートの球威）。
    /// </summary>
    public static Vector3D SpinFor(PitchingCoefficients c, PitchType type, int sharpness, int fastballPower)
    {
        var fb = FastballSpin(c, fastballPower);
        if (PitchSlot.IsSpinByPower(type)) return fb;
        var g = DeviationScale(c, sharpness);
        return fb + (NominalSpin(c, type) - fb) * g;
    }

    /// <summary>回転ベクトル → (回転数[rpm], 回転軸[deg])。<see cref="PitchSpec.SpinFromRpmAndTilt"/> の逆
    /// （回転0は軸0）。</summary>
    public static (double Rpm, double TiltDeg) ToRpmAndTilt(Vector3D spin)
    {
        var radPerSec = System.Math.Sqrt(spin.X * spin.X + spin.Y * spin.Y);
        if (radPerSec < 1e-12) return (0.0, 0.0);
        var rpm = radPerSec * 60.0 / (2.0 * System.Math.PI);
        var tilt = System.Math.Atan2(spin.Y, -spin.X) * 180.0 / System.Math.PI;
        return (rpm, tilt);
    }
}
