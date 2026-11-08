using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 1球の初期条件（物理層）。表示層（球速km/h・球種ランク）からの変換結果としてここに落とし込む。
/// </summary>
public sealed record PitchSpec
{
    /// <summary>リリース位置[m]。既定は右投手の一般的なリリース高付近。</summary>
    public Vector3D ReleasePosition { get; init; } = new(0.0, 1.8, 0.0);

    /// <summary>初速[km/h]。</summary>
    public double SpeedKmh { get; init; }

    /// <summary>鉛直リリース角[deg]（+で上向き）。ホップ量は無回転球との差分で測るため本値には依存しない。</summary>
    public double LaunchAngleDeg { get; init; }

    /// <summary>水平方位角[deg]（+で三塁側へ）。</summary>
    public double AimAzimuthDeg { get; init; }

    /// <summary>角速度ベクトル[rad/s]。バックスピン主体のストレートは X 軸負方向成分を持つ（上向きマグヌス）。</summary>
    public Vector3D SpinRadPerSec { get; init; } = Vector3D.Zero;

    /// <summary>初速[km/h]・角度から初速度ベクトル[m/s]を生成する。</summary>
    public Vector3D InitialVelocity()
    {
        var speed = SpeedKmh / 3.6;
        var elev = LaunchAngleDeg * Math.PI / 180.0;
        var azim = AimAzimuthDeg * Math.PI / 180.0;
        // Z を主軸（本塁方向）に、鉛直・水平へ分解する。
        var vz = speed * Math.Cos(elev) * Math.Cos(azim);
        var vx = speed * Math.Cos(elev) * Math.Sin(azim);
        var vy = speed * Math.Sin(elev);
        return new Vector3D(vx, vy, vz);
    }

    /// <summary>rpm からバックスピン角速度ベクトル[rad/s]（X 軸負方向）を作るヘルパ。<see cref="SpinFromRpmAndTilt"/> の tilt=0 と同じ。</summary>
    public static Vector3D BackspinFromRpm(double rpm) => SpinFromRpmAndTilt(rpm, 0.0);

    /// <summary>
    /// rpm・回転軸角度[deg]から角速度ベクトル[rad/s]を作る（設計書02 §2.1・issue #835で全球種バックスピン
    /// 固定を解消）。角速度ベクトルは本塁方向(Z)に垂直なX-Y平面内に収まる（野球の投球スピンは進行方向まわりの
    /// ジャイロ成分が力を生まないため、物理層では「有効な」スピンだけをここで表現すれば十分）。
    /// tiltDeg=0 は純バックスピン（ホップ）。+90°で純サイドスピン（+X方向＝design-18「+x=一塁側」の規約）、
    /// 180°で純トップスピン（バックスピンの逆＝重力に加えて落下方向を強める）。
    /// </summary>
    public static Vector3D SpinFromRpmAndTilt(double rpm, double tiltDeg)
    {
        var radPerSec = rpm * 2.0 * Math.PI / 60.0;
        var tilt = tiltDeg * Math.PI / 180.0;
        return new Vector3D(-radPerSec * Math.Cos(tilt), radPerSec * Math.Sin(tilt), 0.0);
    }
}
