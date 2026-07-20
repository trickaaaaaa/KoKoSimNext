using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Batting;

/// <summary>
/// 打球の物理層表現（設計書01 §2⑤）。初速・角度・方向の3値が打席結果の本体。
/// 座標: +Z=センター方向, +X=右翼側, Y=高さ。方位角は +Z から +X 方向を正。
/// </summary>
public sealed record BattedBall
{
    /// <summary>打球初速[m/s]。</summary>
    public required double ExitVelocityMps { get; init; }

    /// <summary>打球角度[deg]（+で上向き, 負はゴロ）。</summary>
    public required double LaunchAngleDeg { get; init; }

    /// <summary>方位角[deg]（0=センター, +=右, −=左, フェアは概ね±45°）。</summary>
    public required double BearingDeg { get; init; }

    /// <summary>コンタクト点の高さ[m]。</summary>
    public double ContactHeightM { get; init; } = 1.0;

    /// <summary>バックスピン量[rpm]（打球の伸びに寄与）。</summary>
    public double BackspinRpm { get; init; } = 1800;

    public double ExitVelocityKmh => ExitVelocityMps * 3.6;

    /// <summary>初速度ベクトル[m/s]。</summary>
    public Vector3D InitialVelocity()
    {
        var elev = LaunchAngleDeg * Math.PI / 180.0;
        var bearing = BearingDeg * Math.PI / 180.0;
        var horiz = ExitVelocityMps * Math.Cos(elev);
        var vx = horiz * Math.Sin(bearing);
        var vz = horiz * Math.Cos(bearing);
        var vy = ExitVelocityMps * Math.Sin(elev);
        return new Vector3D(vx, vy, vz);
    }

    /// <summary>コンタクト点。</summary>
    public Vector3D ContactPoint() => new(0, ContactHeightM, 0);

    /// <summary>バックスピン角速度ベクトル[rad/s]（水平速度方向に対し上向き揚力を生む軸）。</summary>
    public Vector3D SpinVector()
    {
        var v = InitialVelocity();
        var vHoriz = new Vector3D(v.X, 0, v.Z);
        if (vHoriz.Length < 1e-9)
        {
            return Vector3D.Zero;
        }
        var axis = Vector3D.Cross(vHoriz, new Vector3D(0, 1, 0)).Normalized();
        var radPerSec = BackspinRpm * 2.0 * Math.PI / 60.0;
        return axis * radPerSec;
    }
}
