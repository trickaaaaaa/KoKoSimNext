using System.Collections.Concurrent;

namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 判定に使う弾道特徴量（設計書15 Phase E）。<see cref="PitchTrajectory"/>（観測専用のフル弾道）とは別物で、
/// swing/contact 判定が読む最小限の値だけを持つ。
/// </summary>
public readonly record struct PitchTrajectoryFeatures(
    double InducedVerticalBreakM, double InducedHorizontalBreakM, double FlightTimeSeconds)
{
    /// <summary>誘発変化の合成量（縦横合成, 設計書15 Phase E-2）。現行モデルは全球種バックスピン固定のため
    /// 横成分は常に0（実質縦成分のみ）だが、将来球種ごとに回転軸を持たせた時にそのまま効くよう合成量で持つ。</summary>
    public double BreakMagnitudeM => System.Math.Sqrt(
        InducedVerticalBreakM * InducedVerticalBreakM + InducedHorizontalBreakM * InducedHorizontalBreakM);
}

/// <summary>
/// (球速, rpm) の2次元グリッドを起動時に一度だけ <see cref="PitchSimulator"/>（RK4）で埋め、
/// 毎球はバイリニア補間で参照する（設計書15 Phase E-1）。<see cref="Aerodynamics"/>/<see cref="MoundGeometry"/>
/// はゲーム内で不変（YAML起動時に確定）なので、値の組ごとにテーブルを1回だけ構築してキャッシュする。
///
/// <para>Trajectory（誘発縦変化・到達時間）はリリース角・方位角に依存しない（ホップ量は無回転球との差分で
/// 測るため）ため、実質的に (球速, rpm) だけの純関数。2次元グリッド＋補間に理想的な形。</para>
/// </summary>
public sealed class TrajectoryFeatureTable
{
    private readonly double[] _speeds;
    private readonly double[] _rpms;
    private readonly double _speedStep;
    private readonly double _rpmStep;
    private readonly double[,] _verticalBreakM;
    private readonly double[,] _horizontalBreakM;
    private readonly double[,] _flightTimeSeconds;

    private static readonly ConcurrentDictionary<(Aerodynamics, MoundGeometry), TrajectoryFeatureTable> Cache = new();

    private TrajectoryFeatureTable(
        double[] speeds, double[] rpms, double[,] verticalBreakM, double[,] horizontalBreakM, double[,] flightTimeSeconds)
    {
        _speeds = speeds;
        _rpms = rpms;
        _speedStep = speeds[1] - speeds[0];
        _rpmStep = rpms[1] - rpms[0];
        _verticalBreakM = verticalBreakM;
        _horizontalBreakM = horizontalBreakM;
        _flightTimeSeconds = flightTimeSeconds;
    }

    /// <summary>
    /// (aero, mound) の組ごとにテーブルを1回だけ構築してキャッシュを返す（決定論的な純構築＝並行呼び出しでも安全）。
    /// </summary>
    public static TrajectoryFeatureTable GetOrBuild(Aerodynamics aero, MoundGeometry mound)
        => Cache.GetOrAdd((aero, mound), key => Build(key.Item1, key.Item2));

    /// <summary>
    /// グリッドを構築する。範囲は実運用の (球速, rpm) レンジに余裕を持たせた値（設計書02 §1.1d/§1.1b, coefficients.yaml
    /// の spin_rpm_* から実際に出うる値を包含）。範囲外は <see cref="Lookup"/> 側でクランプする。
    /// </summary>
    public static TrajectoryFeatureTable Build(
        Aerodynamics aero,
        MoundGeometry mound,
        double speedMinKmh = 70.0,
        double speedMaxKmh = 176.0,
        double speedStepKmh = 2.0,
        double rpmMin = 1800.0,
        double rpmMax = 2700.0,
        double rpmStep = 50.0)
    {
        var speeds = Range(speedMinKmh, speedMaxKmh, speedStepKmh);
        var rpms = Range(rpmMin, rpmMax, rpmStep);
        var vertical = new double[speeds.Length, rpms.Length];
        var horizontal = new double[speeds.Length, rpms.Length];
        var flight = new double[speeds.Length, rpms.Length];

        for (var i = 0; i < speeds.Length; i++)
        {
            for (var j = 0; j < rpms.Length; j++)
            {
                var spec = new PitchSpec
                {
                    SpeedKmh = speeds[i],
                    SpinRadPerSec = PitchSpec.BackspinFromRpm(rpms[j]),
                };
                var traj = PitchSimulator.Simulate(spec, aero, mound);
                vertical[i, j] = traj.InducedVerticalBreakM;
                horizontal[i, j] = traj.InducedHorizontalBreakM;
                flight[i, j] = traj.FlightTimeSeconds;
            }
        }

        return new TrajectoryFeatureTable(speeds, rpms, vertical, horizontal, flight);
    }

    /// <summary>グリッド範囲外はクランプし、バイリニア補間で (誘発縦変化, 到達時間) を返す。RNG非依存の決定論計算。</summary>
    public PitchTrajectoryFeatures Lookup(double speedKmh, double rpm)
    {
        var (i0, i1, fi) = Locate(_speeds, _speedStep, speedKmh);
        var (j0, j1, fj) = Locate(_rpms, _rpmStep, rpm);

        var vertical = Bilerp(_verticalBreakM, i0, i1, fi, j0, j1, fj);
        var horizontal = Bilerp(_horizontalBreakM, i0, i1, fi, j0, j1, fj);
        var flight = Bilerp(_flightTimeSeconds, i0, i1, fi, j0, j1, fj);
        return new PitchTrajectoryFeatures(vertical, horizontal, flight);
    }

    private static (int lower, int upper, double frac) Locate(double[] grid, double step, double value)
    {
        var clamped = Core.MathUtil.Clamp(value, grid[0], grid[^1]);
        var idx = (clamped - grid[0]) / step;
        var lower = (int)idx;
        if (lower >= grid.Length - 1) lower = grid.Length - 2;
        var upper = lower + 1;
        var frac = (clamped - grid[lower]) / (grid[upper] - grid[lower]);
        return (lower, upper, frac);
    }

    private static double Bilerp(double[,] table, int i0, int i1, double fi, int j0, int j1, double fj)
    {
        var a = table[i0, j0] * (1 - fj) + table[i0, j1] * fj;
        var b = table[i1, j0] * (1 - fj) + table[i1, j1] * fj;
        return a * (1 - fi) + b * fi;
    }

    private static double[] Range(double min, double max, double step)
    {
        var count = (int)System.Math.Round((max - min) / step) + 1;
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = min + i * step;
        }
        return values;
    }
}
