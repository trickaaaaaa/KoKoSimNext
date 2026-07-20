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

    /// <summary>rpm からバックスピン角速度ベクトル[rad/s]（X 軸負方向）を作るヘルパ。</summary>
    public static Vector3D BackspinFromRpm(double rpm)
    {
        var radPerSec = rpm * 2.0 * Math.PI / 60.0;
        return new Vector3D(-radPerSec, 0.0, 0.0);
    }
}
