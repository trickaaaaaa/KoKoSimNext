using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>打球の4分類（Issue #63 / OPEN-QUESTIONS Q14）。物理層の積分結果から導出する。</summary>
public enum BattedBallClass
{
    /// <summary>ゴロ（低く転がる。最大バウンド頂点が閾値未満）。</summary>
    Grounder,
    /// <summary>バウンド（チョッパー/叩きつけ。最大バウンド頂点が閾値以上）。</summary>
    Bouncer,
    /// <summary>ライナー（頂点／到達距離が小さい低い弾道）。</summary>
    Liner,
    /// <summary>フライ（弧を描いて上がる打球）。</summary>
    Fly,
}

/// <summary>
/// イレギュラーバウンドの揺らぎ（一律確率で発生, Issue #63 (c)）。
/// 乱数消費は呼び出し側（<see cref="FieldingResolver"/>）が行い、ここへは結果だけを渡す
/// ＝ <see cref="BouncePath"/> は純関数で決定論・テスト容易（不変条件#2）。
/// </summary>
public readonly record struct IrregularBounce(bool Active, double LateralDeg, double RestitutionScale)
{
    public static readonly IrregularBounce None = new(false, 0, 1.0);
}

/// <summary>バウンド1回分の弾み（放物線。空気抵抗は無視＝近距離のため影響が小さい）。</summary>
public readonly record struct BounceHop(
    double T0,
    double DurationSeconds,
    Vector3D Start,
    Vector3D Direction,
    double HorizontalSpeedMps,
    double LaunchSpeedMps)
{
    /// <summary>この弾みの頂点高さ[m] = v₀²/2g。</summary>
    public double ApexHeightM => LaunchSpeedMps * LaunchSpeedMps / (2.0 * BouncePath.GravityMps2);
    /// <summary>この弾みで進む水平距離[m]。</summary>
    public double DistanceM => HorizontalSpeedMps * DurationSeconds;
    /// <summary>着地点（次の接地点）。</summary>
    public Vector3D End => Start + Direction * DistanceM;

    /// <summary>弾み開始からの経過 tt における高さ[m]。</summary>
    public double HeightAt(double tt)
        => Math.Max(0.0, LaunchSpeedMps * tt - 0.5 * BouncePath.GravityMps2 * tt * tt);

    /// <summary>弾み開始からの経過 tt における平面位置。</summary>
    public Vector3D PositionAt(double tt) => Start + Direction * (HorizontalSpeedMps * tt);
}

/// <summary>
/// 着地後のバウンド積分（Issue #63 / OPEN-QUESTIONS Q14 の方針(a)）。
///
/// 弾道積分（<see cref="Pitching.BallisticIntegrator.IntegrateToGround"/>）を最初の着地で打ち切らず、
/// 反発係数と接地摩擦で弾みを繰り返し、鉛直初速が十分小さくなったら転がりへ移行する。
/// これにより「高いバウンドで野手が待たされる／バウンドで内野手の頭上を越える」が
/// **同一の機構から創発**する（しきい値で場合分けしない＝不変条件#1）。
///
/// 各接地でのモデル（球の接地の標準的な扱い）:
/// <list type="bullet">
/// <item>鉛直: v'⊥ = e·|v⊥|（e = 反発係数）。頂点は v'⊥²/2g ＝ 反発係数の2乗で減衰する。</item>
/// <item>水平: 滑り摩擦の力積 μ(1+e)|v⊥| だけ減速する。ただし滑りが止まって転がりに移った時点で
///   摩擦は効かなくなるため、減速量は (2/7)v∥ が上限（＝保持率の下限は球の 5/7）。
///   これにより「水平に近いライナーは滑って伸び／真上から落ちる大飛球は失速して死ぬ」が式から出る。</item>
/// </list>
///
/// 適用範囲: 内野で個々のバウンドが判定に効く近距離。内野を抜けた先は Issue #24 で校正済みの
/// 集約転がりモデル（<see cref="ExtraBaseResolver.RollPath"/>）へ引き継ぐ。
/// </summary>
public sealed class BouncePath
{
    /// <summary>重力加速度[m/s²]（バウンド積分用。<see cref="Pitching.Aerodynamics"/> と同値）。</summary>
    public const double GravityMps2 = 9.80665;

    /// <summary>弾みの打ち切り上限（無限ループ防止。以降は転がり扱い）。</summary>
    private const int MaxHops = 16;

    private readonly BounceHop[] _hops;

    private BouncePath(
        Vector3D landing, double hangTimeSeconds, BounceHop[] hops,
        Vector3D rollStart, Vector3D rollDirection, double rollStartSeconds,
        double rollSpeedMps, double rollDecelMps2, bool irregular)
    {
        Landing = landing;
        HangTimeSeconds = hangTimeSeconds;
        _hops = hops;
        RollStart = rollStart;
        RollDirection = rollDirection;
        RollStartSeconds = rollStartSeconds;
        RollSpeedMps = rollSpeedMps;
        RollDecelMps2 = rollDecelMps2;
        Irregular = irregular;

        var apex = 0.0;
        foreach (var h in hops)
        {
            if (h.ApexHeightM > apex) apex = h.ApexHeightM;
        }
        MaxBounceApexM = apex;
        RollDistanceM = rollDecelMps2 > 0 ? rollSpeedMps * rollSpeedMps / (2.0 * rollDecelMps2) : 0.0;
        SettleAtSeconds = rollStartSeconds + (rollDecelMps2 > 0 ? rollSpeedMps / rollDecelMps2 : 0.0);
    }

    /// <summary>最初の接地点（弾道積分の着地点）。</summary>
    public Vector3D Landing { get; }
    /// <summary>最初の接地時刻[s]（接触＝0基準）。</summary>
    public double HangTimeSeconds { get; }
    /// <summary>弾みの列（時刻順）。</summary>
    public IReadOnlyList<BounceHop> Hops => _hops;
    /// <summary>最大バウンド頂点[m]（4分類のゴロ/バウンド判定に使う）。</summary>
    public double MaxBounceApexM { get; }
    /// <summary>イレギュラーバウンドが適用されたか。</summary>
    public bool Irregular { get; }

    /// <summary>転がりへ移行した地点・方向・時刻・速度。</summary>
    public Vector3D RollStart { get; }
    public Vector3D RollDirection { get; }
    public double RollStartSeconds { get; }
    public double RollSpeedMps { get; }
    public double RollDecelMps2 { get; }
    /// <summary>転がりで進む距離[m]（フェンス・野手を考慮しない自由転がり）。</summary>
    public double RollDistanceM { get; }
    /// <summary>打球が完全に止まる時刻[s]。</summary>
    public double SettleAtSeconds { get; }

    /// <summary>停止位置（自由転がりの終端）。</summary>
    public Vector3D StopPosition => RollStart + RollDirection * RollDistanceM;

    /// <summary>
    /// 着地状態からバウンド列＋転がりを解く。乱数は消費しない（irregular は呼び出し側が抽選済み）。
    /// </summary>
    public static BouncePath Compute(
        Vector3D landing, Vector3D landingVelocity, double hangTimeSeconds,
        FieldingCoefficients c, IrregularBounce irregular)
    {
        var vh = Math.Sqrt(landingVelocity.X * landingVelocity.X + landingVelocity.Z * landingVelocity.Z);
        var dir = vh > 1e-6
            ? new Vector3D(landingVelocity.X / vh, 0, landingVelocity.Z / vh)
            : new Vector3D(0, 0, 1);

        var decel = Math.Max(0.1, c.RollDecelMps2);
        var restitution = MathUtil.Clamp(c.BounceRestitution, 0.0, 0.95);
        var mu = Math.Max(0.0, c.BounceFrictionMu);
        var slideFloor = MathUtil.Clamp(c.BounceRollingRetention, 0.0, 1.0);

        var hops = new List<BounceHop>(MaxHops);
        var pos = landing;
        var t = hangTimeSeconds;
        var vPerp = Math.Abs(landingVelocity.Y);

        // イレギュラーは最初の接地だけに効かせる（＝内野手の目前で一度だけ跳ね方が狂う）。
        var firstImpact = true;

        for (var i = 0; i < MaxHops; i++)
        {
            var e = restitution;
            if (firstImpact && irregular.Active)
            {
                e = MathUtil.Clamp(restitution * irregular.RestitutionScale, 0.05, 0.95);
                dir = Rotate(dir, irregular.LateralDeg);
            }

            // 水平: 滑り摩擦の力積で減速。転がりに移った時点で頭打ち（保持率の下限＝5/7）。
            var slide = mu * (1.0 + e) * vPerp;
            vh = Math.Max(vh - slide, vh * slideFloor);

            // 鉛直: 反発係数で跳ね返る。十分小さくなったら転がりへ。
            var launch = e * vPerp;
            firstImpact = false;
            if (launch < c.BounceMinLaunchMps || vh <= 1e-6)
            {
                vPerp = launch;
                break;
            }

            var dur = 2.0 * launch / GravityMps2;
            var hop = new BounceHop(t, dur, pos, dir, vh, launch);
            hops.Add(hop);
            pos = hop.End;
            t += dur;
            vPerp = launch;
        }

        return new BouncePath(
            landing, hangTimeSeconds, hops.ToArray(),
            pos, dir, t, vh, decel, irregular.Active);
    }

    /// <summary>時刻 t の平面位置（着地前は着地点で待機扱い）。</summary>
    public Vector3D PositionAt(double t)
    {
        if (t <= HangTimeSeconds) return Landing;
        foreach (var hop in _hops)
        {
            if (t < hop.T0 + hop.DurationSeconds) return hop.PositionAt(t - hop.T0);
        }
        var tau = Math.Max(0.0, t - RollStartSeconds);
        var tStop = RollDecelMps2 > 0 ? RollSpeedMps / RollDecelMps2 : 0.0;
        var te = Math.Min(tau, tStop);
        var d = Math.Min(RollDistanceM, RollSpeedMps * te - 0.5 * RollDecelMps2 * te * te);
        return RollStart + RollDirection * Math.Max(0.0, d);
    }

    /// <summary>時刻 t の高さ[m]（転がり区間は0）。</summary>
    public double HeightAt(double t)
    {
        if (t <= HangTimeSeconds) return 0.0;
        foreach (var hop in _hops)
        {
            if (t < hop.T0 + hop.DurationSeconds) return hop.HeightAt(t - hop.T0);
        }
        return 0.0;
    }

    /// <summary>時刻 t の水平速度[m/s]（弾み中は一定、転がり中は減速）。</summary>
    public double HorizontalSpeedAt(double t)
    {
        foreach (var hop in _hops)
        {
            if (t < hop.T0 + hop.DurationSeconds) return hop.HorizontalSpeedMps;
        }
        var tau = Math.Max(0.0, t - RollStartSeconds);
        return Math.Max(0.0, RollSpeedMps - RollDecelMps2 * tau);
    }

    /// <summary>時刻 t の進行方向（水平単位ベクトル）。</summary>
    public Vector3D DirectionAt(double t)
    {
        foreach (var hop in _hops)
        {
            if (t < hop.T0 + hop.DurationSeconds) return hop.Direction;
        }
        return RollDirection;
    }

    /// <summary>
    /// 本塁からの水平到達距離が <paramref name="rangeM"/> を最初に越える時刻[s]。
    /// 越えないまま止まる場合は null（＝内野内で止まる打球）。
    /// </summary>
    public double? TimeCrossingRange(double rangeM, double stepSeconds)
    {
        var step = Math.Max(0.005, stepSeconds);
        for (var t = HangTimeSeconds; t <= SettleAtSeconds + step; t += step)
        {
            var p = PositionAt(t);
            if (Math.Sqrt(p.X * p.X + p.Z * p.Z) > rangeM) return t;
        }
        var stop = StopPosition;
        return Math.Sqrt(stop.X * stop.X + stop.Z * stop.Z) > rangeM ? SettleAtSeconds : (double?)null;
    }

    /// <summary>
    /// 時刻 <paramref name="t"/> 以降で最初に接地する（弾みが終わる）状態。
    /// 内野を抜けた打球を集約転がりモデルへ引き継ぐための受け渡し点。
    /// すでに転がっていれば その時刻の状態（鉛直速度0）を返す。
    /// </summary>
    public (Vector3D Position, Vector3D Velocity, double Seconds, bool Airborne) TouchdownAtOrAfter(double t)
    {
        foreach (var hop in _hops)
        {
            var end = hop.T0 + hop.DurationSeconds;
            if (end <= t) continue;
            var v = hop.Direction * hop.HorizontalSpeedMps;
            return (hop.End, new Vector3D(v.X, -hop.LaunchSpeedMps, v.Z), end, true);
        }
        var speed = HorizontalSpeedAt(t);
        return (PositionAt(t), DirectionAt(t) * speed, t, false);
    }

    /// <summary>方向ベクトルを水平面内で degrees だけ回す（イレギュラーの横ぶれ）。</summary>
    private static Vector3D Rotate(Vector3D dir, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return new Vector3D(dir.X * cos - dir.Z * sin, 0, dir.X * sin + dir.Z * cos);
    }
}
