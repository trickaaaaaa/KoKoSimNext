using System;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>
/// ファウルをどこで見せるか（issue #1074・表示専用）。判定・乱数には一切効かない。
/// </summary>
public enum FoulViewKind
{
    /// <summary>対決ビューのまま（ボテボテ・バックネット行き・打球の無いファウルなど）。</summary>
    None,
    /// <summary>野手が落下点へ追ったが、あと一歩届かなかったファウルフライ（守備ビューで見せる）。</summary>
    ChasedFly,
    /// <summary>ポール際・スタンドへ入る大ファウル（守備ビューで行方まで見せる）。</summary>
    BigFoul,
}

/// <summary>
/// ファウルフライを一番早く追える野手（issue #1074・表示専用）。捕れなかったファウルの打球について、
/// 判定（<see cref="FieldingResolver.ResolveFoulFly"/>）と同じ式で「誰が・どこから・何秒で落下点に着くか」を写す。
/// 乱数は使わない。
/// </summary>
/// <param name="Role">一番早く着く野手。</param>
/// <param name="StartX">その野手の出発点 X[m]（+X＝一塁側）。</param>
/// <param name="StartZ">その野手の出発点 Z[m]（+Z＝中堅方向）。</param>
/// <param name="ReachSeconds">接触から落下点に着くまで[s]（反応込み）。</param>
/// <param name="HangSeconds">接触から球が落ちるまで[s]。</param>
public sealed record FoulChase(FieldPosition Role, double StartX, double StartZ, double ReachSeconds, double HangSeconds)
{
    /// <summary>
    /// 絵に描く「落下より何秒遅れて着くか」（常に正＝間に合わない）。判定の捕球予算は落下時間より短い
    /// （<c>CatchTimeBudget</c>）ので、素の到達（<see cref="ReachSeconds"/>）が落下より先でも落球になる。
    /// そのまま描くと落下より先に着いた野手が捕らない絵になる＝<see cref="MinLateSeconds"/> を下限にして
    /// 「あと一歩届かなかった」に揃える。
    /// </summary>
    public double LateSeconds => Math.Max(ReachSeconds - HangSeconds, MinLateSeconds);

    /// <summary>落球の絵で、野手が落下点に着くのを落下より遅らせる最小の秒（表示専用）。</summary>
    public const double MinLateSeconds = 0.1;
}

/// <summary>守備ビューで見せるファウルの判定（issue #1074・オーナー決定 2026-11-17）。純関数・乱数不使用。</summary>
public static class FoulView
{
    /// <summary>
    /// 打ち上がってファウルグラウンド（スタンドより内側）に落ちる飛球について、一番早く着く野手を返す。
    /// 低い打球・スタンドに入る打球は null。捕れる打球（アウト）はこの前に判定側で処理されている。
    /// </summary>
    public static FoulChase? Chase(
        BattedBall ball, FieldGeometry field, Aerodynamics aero,
        IReadOnlyList<Fielder> fielders, FieldingCoefficients coeff)
    {
        var ground = BallisticIntegrator.IntegrateToGround(
            ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), aero);
        if (ground.ApexHeightM < coeff.FlyApexThresholdM) return null;
        var landing = ground.LandingPosition;
        if (!field.InFoulTerritory(landing)) return null;

        Fielder? best = null;
        var bestReach = double.MaxValue;
        foreach (var f in fielders)
        {
            // 判定（ResolveFoulFly）の ReachTime と同じ式
            var t = (landing - f.Location).Length / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
            if (t < bestReach) { bestReach = t; best = f; }
        }
        if (best is null) return null;
        return new FoulChase(best.Position, best.Location.X, best.Location.Z, bestReach, ground.HangTimeSeconds);
    }

    /// <summary>
    /// ファウルの見せ方を決める（オーナー決定 2026-11-17・案A）。
    /// (a) 一番早い野手が落下点に着く時刻 − 落下時刻 ≤ <see cref="FieldingCoefficients.FoulViewChaseSeconds"/> のファウルフライ
    /// (b) |方位角| ≤ 45° + <see cref="FieldingCoefficients.FoulViewPoleMarginDeg"/> で、両翼フェンスの距離より遠くへ飛ぶ打球
    /// のどちらかだけを守備ビューへ移す。それ以外は対決ビューのまま。
    /// </summary>
    /// <param name="landingAlongLineM">落下点のファウルライン方向の距離[m]（本塁から）。</param>
    public static FoulViewKind Classify(
        double bearingDeg, double landingAlongLineM, FoulChase? chase, FieldGeometry field, FieldingCoefficients coeff)
    {
        if (chase is { } c && c.LateSeconds <= coeff.FoulViewChaseSeconds) return FoulViewKind.ChasedFly;
        var abs = Math.Abs(bearingDeg);
        var lineFence = bearingDeg >= 0 ? field.RightFenceM : field.LeftFenceM;
        if (abs > 45.0 && abs <= 45.0 + coeff.FoulViewPoleMarginDeg && landingAlongLineM >= lineFence)
            return FoulViewKind.BigFoul;
        return FoulViewKind.None;
    }

    /// <summary>落下点のファウルライン方向の距離[m]（ライン＝±45° への射影）。</summary>
    public static double AlongLine(double x, double z)
    {
        var r = Math.Sqrt(x * x + z * z);
        if (r < 1e-9) return 0;
        var off = Math.Abs(Math.Atan2(x, z)) - Math.PI / 4;
        return r * Math.Cos(off);
    }
}
