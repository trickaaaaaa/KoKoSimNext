using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 弾道積分の結果（物理層）。3D演出はこの値をそのまま再生できる。
/// </summary>
public sealed record PitchTrajectory
{
    /// <summary>本塁到達までの飛行時間[s]。</summary>
    public required double FlightTimeSeconds { get; init; }

    /// <summary>本塁面での通過位置[m]（X=左右, Y=高さ）。</summary>
    public required Vector3D PlateCrossing { get; init; }

    /// <summary>本塁面での速度ベクトル[m/s]。</summary>
    public required Vector3D FinalVelocity { get; init; }

    /// <summary>本塁到達時の球速[km/h]（抗力で初速から減速する）。</summary>
    public double FinalSpeedKmh => FinalVelocity.Length * 3.6;

    /// <summary>
    /// ホップ量（誘発縦変化, IVB）[m]。同一初期条件の無回転球に対する縦位置の差。
    /// 正ならバックスピンにより「落ちにくい＝浮き上がって見える」。
    /// </summary>
    public required double InducedVerticalBreakM { get; init; }

    /// <summary>誘発横変化[m]。無回転球に対する横位置の差。</summary>
    public required double InducedHorizontalBreakM { get; init; }
}
