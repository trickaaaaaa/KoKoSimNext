namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 野球ボールの空力・物理定数（弾道積分の係数）。
/// 純エンジンは IO を持たないため（不変条件#3）、この値は外部（KokoSim.Config が data/coefficients.yaml から）
/// 生成して注入する。ここでの既定値は物理的に妥当な初期値であり、バランス調整は YAML 側で行う（不変条件#4）。
/// </summary>
public sealed record Aerodynamics
{
    /// <summary>ボール質量[kg]（公認球 141.7〜148.8g の中央付近）。</summary>
    public double BallMassKg { get; init; } = 0.145;

    /// <summary>ボール半径[m]（周長約22.9cm → 半径約3.65cm）。</summary>
    public double BallRadiusM { get; init; } = 0.0365;

    /// <summary>空気密度[kg/m^3]（海面・15℃）。</summary>
    public double AirDensity { get; init; } = 1.225;

    /// <summary>重力加速度[m/s^2]。</summary>
    public double Gravity { get; init; } = 9.81;

    /// <summary>抗力係数 Cd（この速度域でほぼ一定とみなす）。</summary>
    public double DragCoefficient { get; init; } = 0.35;

    /// <summary>
    /// 揚力係数 Cl の対スピンファクター係数。Cl = min(此係数 × S, MaxLiftCoefficient)。
    /// S（スピンファクター）= r|ω| / |v|。
    /// </summary>
    public double LiftCoefficientPerSpinFactor { get; init; } = 0.80;

    /// <summary>揚力係数 Cl の上限（高回転域での飽和）。</summary>
    public double MaxLiftCoefficient { get; init; } = 0.35;

    /// <summary>断面積[m^2]。</summary>
    public double CrossSectionalArea => Math.PI * BallRadiusM * BallRadiusM;

    /// <summary>スピンファクター S から揚力係数 Cl を求める。</summary>
    public double LiftCoefficient(double spinFactor)
        => Math.Min(LiftCoefficientPerSpinFactor * spinFactor, MaxLiftCoefficient);
}
