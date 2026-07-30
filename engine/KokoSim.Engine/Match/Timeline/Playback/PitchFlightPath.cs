using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// 表示用の投球飛翔パス（Issue #332・design-18 §4.5-③）。リリース点から本塁面の実コースへ、
/// 観測データ（飛翔時間・誘発変化量）どおりの曲がりで球を運ぶ純関数。
///
/// なぜ「弦＋逸れ」で組むか（弾道を再積分しないか）:
///  - 端点が**実データで決まっている**。着弾点は判定に使われた実着弾位置（<see cref="AtBat.PitchRecord.LocationX"/>）で、
///    観測用フル弾道（<see cref="Pitching.PitchTrajectory"/>）はコース（AimX/AimY）を入力に取らない
///    ＝まっすぐ投げた球の落ち方しか持たない。よって弾道の位置列をそのまま使うと着弾点が判定と食い違う
///  - 表示側（Unity）で積分をやり直すと物理が二箇所になる（不変条件#1「変換式は一箇所」）。
///    ここは engine 側の純関数1つに閉じ、Unity は毎フレーム呼ぶだけにする
///
/// 逸れの式: 一定加速度 a のもとで始点・終点を固定すると、弦からの逸れは <c>(a/2)·t·(T−t)</c>。
/// 終点自由なら変位は <c>(a/2)·T²</c> なので、観測量（終点での変位＝誘発変化量 IVB）から
/// <c>a = 2·IVB/T²</c> と読み替えられる。重力（−g）と誘発変化を足して
/// <c>dy(t) = (IVB/T² − g/2)·t·(T−t)</c>。T=0.45s・IVB=0 なら中点で約25cm垂れ、
/// ホップがあるぶん垂れが減る＝実際の投球の見え方と同じになる。
/// </summary>
public static class PitchFlightPath
{
    /// <summary>重力加速度[m/s²]。表示用のため <see cref="Pitching.Aerodynamics"/> の抗力までは追わない。</summary>
    public const double GravityMps2 = 9.8;

    /// <summary>
    /// 飛翔中の球の位置。座標は engine の場と同じ規約＝X=左右（+が一塁側）・Y=高さ・Z=本塁からの距離[m]。
    /// </summary>
    /// <param name="release">リリース点（X=左右, Y=高さ, Z=本塁からの距離）[m]。</param>
    /// <param name="plateX">本塁面での実着弾位置の左右[m]。</param>
    /// <param name="plateY">本塁面での実着弾位置の高さ[m]。</param>
    /// <param name="flightSeconds">飛翔時間[s]（観測弾道の実測値。無い球は呼び元が既定値を渡す）。</param>
    /// <param name="inducedVerticalBreakM">誘発縦変化（ホップ量）[m]。正で落ちにくい。無い球は0。</param>
    /// <param name="inducedHorizontalBreakM">誘発横変化[m]。現モデルは全球種バックスピン固定でほぼ0。</param>
    /// <param name="t">リリースからの経過時間[s]。[0, flightSeconds] を外れた値はクランプする。</param>
    public static Vector3D At(
        Vector3D release,
        double plateX,
        double plateY,
        double flightSeconds,
        double inducedVerticalBreakM,
        double inducedHorizontalBreakM,
        double t)
    {
        if (flightSeconds <= 0) return new Vector3D(plateX, plateY, 0);
        var clamped = t < 0 ? 0 : (t > flightSeconds ? flightSeconds : t);
        var u = clamped / flightSeconds;

        // 弦（リリース点→実着弾点の直線）。端点は常に実データどおりに通る
        var x = release.X + (plateX - release.X) * u;
        var y = release.Y + (plateY - release.Y) * u;
        var z = release.Z * (1.0 - u);

        // 弦からの逸れ。中間だけを膨らませるので端点は動かない
        var bow = clamped * (flightSeconds - clamped);
        var t2 = flightSeconds * flightSeconds;
        y += (inducedVerticalBreakM / t2 - GravityMps2 / 2.0) * bow;
        x += inducedHorizontalBreakM / t2 * bow;

        return new Vector3D(x, y, z);
    }
}
