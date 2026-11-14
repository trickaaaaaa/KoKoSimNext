using System.Collections.Concurrent;

namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 判定に使う弾道特徴量（設計書15 Phase E）。<see cref="PitchTrajectory"/>（観測専用のフル弾道）とは別物で、
/// swing/contact 判定が読む最小限の値だけを持つ。
/// </summary>
public readonly record struct PitchTrajectoryFeatures(
    double InducedVerticalBreakM, double InducedHorizontalBreakM, double FlightTimeSeconds,
    FastballReference? Fastball = null)
{
    /// <summary>誘発変化の合成量（縦横合成, 設計書15 Phase E-2）。球種ごとの回転軸（issue #835）で
    /// 横成分・縦成分の配分が変わるため、判定側は常にこの合成量を読む。</summary>
    public double BreakMagnitudeM => System.Math.Sqrt(
        InducedVerticalBreakM * InducedVerticalBreakM + InducedHorizontalBreakM * InducedHorizontalBreakM);
}

/// <summary>
/// 同じ投手のストレートの弾道特徴量（issue #911）。打者はストレートの軌道に合わせて待つので、変化球の判定は
/// 「そのストレートからどれだけ外れたか」で効かせる（<see cref="Batting.PitchDeception"/>）。同じ1球の球速
/// サンプル（球種の球速比を掛ける前）で投げたストレートの値＝球速差は飛翔時間の差として入る。
/// ストレート自身の特徴量には付けない（null＝従来どおり無回転からの変化量で判定）。
/// </summary>
public readonly record struct FastballReference(
    double InducedVerticalBreakM, double InducedHorizontalBreakM, double FlightTimeSeconds)
{
    /// <summary>ストレートの誘発変化の合成量（<see cref="PitchTrajectoryFeatures.BreakMagnitudeM"/> と同じ定義）。</summary>
    public double BreakMagnitudeM => System.Math.Sqrt(
        InducedVerticalBreakM * InducedVerticalBreakM + InducedHorizontalBreakM * InducedHorizontalBreakM);
}

/// <summary>
/// (球速, rpm) の2次元グリッドを起動時に一度だけ <see cref="PitchSimulator"/>（RK4）で埋め、
/// 毎球はバイリニア補間で参照する（設計書15 Phase E-1）。<see cref="Aerodynamics"/>/<see cref="MoundGeometry"/>
/// はゲーム内で不変（YAML起動時に確定）なので、値の組ごとにテーブルを1回だけ構築してキャッシュする。
///
/// <para>Trajectory（誘発縦変化・到達時間）はリリース角・方位角に依存しない（ホップ量は無回転球との差分で
/// 測るため）ため、実質的に (球速, rpm) だけの純関数。2次元グリッド＋補間に理想的な形。
/// 回転軸（<paramref name="tiltDeg"/>, issue #835）は球種ごとに固定値（<see cref="PitchingCoefficients.AxisFor"/>）
/// なので、球種ごとに別インスタンス（tiltDeg違い）をキャッシュして持つ＝グリッドの次元を増やさずに済む。</para>
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

    // Lazy で包む＝並列プレフェッチで同じ鍵が同時に来ても構築は1回だけ（1枚 Mono で約1.9秒）。
    private static readonly ConcurrentDictionary<(Aerodynamics, MoundGeometry, double), System.Lazy<TrajectoryFeatureTable>> Cache = new();

    /// <summary>
    /// 空気密度の節点の基準[kg/m^3]＝天候なし（<see cref="Aerodynamics.AirDensity"/> の既定）。天候なしの試合は
    /// 必ずこの節点に乗る＝補間を通らず従来とビット一致する。
    /// </summary>
    public const double DensityAnchor = 1.225;

    /// <summary>
    /// 空気密度の節点の間隔[kg/m^3]。天候（気温）で密度が試合ごとに連続値でばらつくため、テーブルは節点でだけ作り、
    /// 間は直線補間する。以前は密度そのものを鍵にしていて、天候ありの試合は<b>毎試合9枚を作り直し</b>
    /// （Mono で1試合5秒超）、作った表を捨てずに溜め続けていた（2026-11-10 実測・試合開始が1分以上止まった）。
    /// 0.05 で全季節（8〜36℃＝約1.18〜1.24）が 1.175/1.225/1.275 の3節点に収まる。抗力・マグヌス力は密度に比例し、
    /// この幅（約4%）では特徴量はほぼ直線＝補間誤差は無視できる。
    /// </summary>
    public const double DensityNodeStep = 0.05;

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
    /// (aero, mound, tiltDeg) の組ごとにテーブルを1回だけ構築してキャッシュを返す（決定論的な純構築＝並行呼び出しでも安全）。
    /// グリッド範囲は <see cref="Build"/> の既定よりも広い（issue #835: 球種ごとの rpm_scale で基準回転数から
    /// 大きく外れる値が出るため。既定の狭い範囲は <see cref="Build"/> を直接呼ぶ既存テスト・用途のために残す）。
    /// </summary>
    public static TrajectoryFeatureTable GetOrBuild(Aerodynamics aero, MoundGeometry mound, double tiltDeg = 0.0)
        => Cache.GetOrAdd((aero, mound, tiltDeg), key => new System.Lazy<TrajectoryFeatureTable>(() => Build(
            key.Item1, key.Item2, tiltDeg: key.Item3, rpmMin: 600.0, rpmMax: 3000.0, rpmStep: 100.0))).Value;

    /// <summary>
    /// 判定用の弾道特徴量を引く入口（<see cref="Match.AtBat.AtBatSession"/> が毎球呼ぶ）。空気密度が節点
    /// （<see cref="DensityAnchor"/> ＋ <see cref="DensityNodeStep"/> の整数倍）に乗っていればその表をそのまま引き、
    /// 外れていれば（＝天候で密度が変わった試合）両隣の節点の表を密度で直線補間する。表は節点でしか作らない。
    /// </summary>
    public static PitchTrajectoryFeatures LookupFeatures(
        Aerodynamics aero, MoundGeometry mound, double tiltDeg, double speedKmh, double rpm)
    {
        var k = (aero.AirDensity - DensityAnchor) / DensityNodeStep;
        var k0 = System.Math.Floor(k);
        var t = k - k0;
        const double OnNode = 1e-9;
        if (t < OnNode || t > 1.0 - OnNode)
        {
            var node = aero with { AirDensity = DensityAnchor + System.Math.Round(k) * DensityNodeStep };
            // 節点ちょうど（天候なし）は元の aero をそのまま鍵にする＝従来の表と同一・ビット一致。
            if (System.Math.Abs(node.AirDensity - aero.AirDensity) < 1e-12) node = aero;
            return GetOrBuild(node, mound, tiltDeg).Lookup(speedKmh, rpm);
        }
        var lo = GetOrBuild(aero with { AirDensity = DensityAnchor + k0 * DensityNodeStep }, mound, tiltDeg)
            .Lookup(speedKmh, rpm);
        var hi = GetOrBuild(aero with { AirDensity = DensityAnchor + (k0 + 1) * DensityNodeStep }, mound, tiltDeg)
            .Lookup(speedKmh, rpm);
        return new PitchTrajectoryFeatures(
            lo.InducedVerticalBreakM + (hi.InducedVerticalBreakM - lo.InducedVerticalBreakM) * t,
            lo.InducedHorizontalBreakM + (hi.InducedHorizontalBreakM - lo.InducedHorizontalBreakM) * t,
            lo.FlightTimeSeconds + (hi.FlightTimeSeconds - lo.FlightTimeSeconds) * t);
    }

    /// <summary>
    /// 節点の表を先に作っておく（起動直後に背景で呼ぶ＝最初の試合で数十秒止まらないように）。
    /// 密度の範囲 [<paramref name="minDensity"/>, <paramref name="maxDensity"/>] を覆う節点×回転軸ぶんを作る。
    /// </summary>
    public static void Prewarm(
        Aerodynamics aero, MoundGeometry mound, System.Collections.Generic.IEnumerable<double> tiltDegs,
        double minDensity, double maxDensity, int maxParallelism = 1)
    {
        var kMin = (int)System.Math.Floor((minDensity - DensityAnchor) / DensityNodeStep);
        var kMax = (int)System.Math.Ceiling((maxDensity - DensityAnchor) / DensityNodeStep);
        var jobs = new System.Collections.Generic.List<(Aerodynamics, double)>();
        foreach (var tilt in System.Linq.Enumerable.Distinct(tiltDegs))
            for (var k = kMin; k <= kMax; k++)
                jobs.Add((k == 0 && aero.AirDensity == DensityAnchor
                    ? aero : aero with { AirDensity = DensityAnchor + k * DensityNodeStep }, tilt));
        System.Threading.Tasks.Parallel.For(0, jobs.Count,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = System.Math.Max(1, maxParallelism) },
            i => GetOrBuild(jobs[i].Item1, mound, jobs[i].Item2));
    }

    /// <summary>
    /// グリッドを構築する。範囲は実運用の (球速, rpm) レンジに余裕を持たせた値（設計書02 §1.1d/§1.1b, coefficients.yaml
    /// の spin_rpm_* から実際に出うる値を包含）。範囲外は <see cref="Lookup"/> 側でクランプする。
    /// <paramref name="tiltDeg"/> は回転軸角度（既定0=純バックスピン、<see cref="PitchSpec.SpinFromRpmAndTilt"/>）。
    /// </summary>
    public static TrajectoryFeatureTable Build(
        Aerodynamics aero,
        MoundGeometry mound,
        double speedMinKmh = 70.0,
        double speedMaxKmh = 176.0,
        double speedStepKmh = 2.0,
        double rpmMin = 1800.0,
        double rpmMax = 2700.0,
        double rpmStep = 50.0,
        double tiltDeg = 0.0)
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
                    SpinRadPerSec = PitchSpec.SpinFromRpmAndTilt(rpms[j], tiltDeg),
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
