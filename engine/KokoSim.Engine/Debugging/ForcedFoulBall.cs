using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 強制発動のファウル（issue #1074 の撮影用）の打球を作る。乱数は使わず、決まった順に候補を試して
/// 最初に条件を満たしたものを返す＝同じ守備位置・球場なら毎回同じ打球になる。見つからなければ null（強制しない）。
/// </summary>
public static class ForcedFoulBall
{
    public static BattedBall? For(
        ForcedOutcome kind, FieldGeometry field, Aerodynamics aero,
        IReadOnlyList<Fielder> fielders, FieldingCoefficients coeff)
        => kind switch
        {
            ForcedOutcome.ChasedFoulFly => ChasedFly(field, aero, fielders, coeff),
            ForcedOutcome.BigFoul => BigFoul(field, aero, coeff),
            // 叩きつけて三塁側のファウルグラウンドへ（下向き・速い）
            ForcedOutcome.GroundFoul => new BattedBall { ExitVelocityMps = 30.0, LaunchAngleDeg = -12.0, BearingDeg = -60.0 },
            // バックネットを越えて後ろのスタンドへ（守備ビューへは移らない＝対決ビューのまま）
            ForcedOutcome.HighFoul => new BattedBall { ExitVelocityMps = 33.0, LaunchAngleDeg = 62.0, BearingDeg = 135.0 },
            // 鋭いライナー性のファウル（一塁側のファウルグラウンドへ低く速く）
            ForcedOutcome.LinerFoul => new BattedBall { ExitVelocityMps = 40.0, LaunchAngleDeg = 8.0, BearingDeg = 55.0 },
            _ => null,
        };

    /// <summary>捕れない（判定でアウトにならない）が、一番早い野手が落下の 0.1〜0.45 秒後に着くファウルフライ。</summary>
    private static BattedBall? ChasedFly(
        FieldGeometry field, Aerodynamics aero, IReadOnlyList<Fielder> fielders, FieldingCoefficients coeff)
    {
        foreach (var bearing in new[] { 70.0, -70.0, 60.0, -60.0, 85.0, -85.0, 100.0, -100.0, 52.0, -52.0, 120.0, -120.0 })
            for (var la = 60.0; la <= 85.0; la += 5.0)
                for (var kmh = 50.0; kmh <= 130.0; kmh += 4.0)
                {
                    var ball = new BattedBall { ExitVelocityMps = kmh / 3.6, LaunchAngleDeg = la, BearingDeg = bearing };
                    if (FieldingResolver.ResolveFoulFly(ball, field, aero, fielders, coeff, runnerTime: 4.5) is not null) continue;
                    if (FoulView.Chase(ball, field, aero, fielders, coeff) is not { } c) continue;
                    if (c.LateSeconds > 0.1 && c.LateSeconds <= 0.45) return ball;
                }
        return null;
    }

    /// <summary>ポールのすぐ外を両翼より遠くまで飛ぶ大ファウル（三塁側→一塁側の順に探す）。</summary>
    private static BattedBall? BigFoul(FieldGeometry field, Aerodynamics aero, FieldingCoefficients coeff)
    {
        foreach (var bearing in new[] { -47.0, 47.0, -49.0, 49.0 })
            for (var kmh = 150.0; kmh <= 185.0; kmh += 5.0)
            {
                var ball = new BattedBall { ExitVelocityMps = kmh / 3.6, LaunchAngleDeg = 28.0, BearingDeg = bearing };
                var g = BallisticIntegrator.IntegrateToGround(ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), aero);
                var along = FoulView.AlongLine(g.LandingPosition.X, g.LandingPosition.Z);
                if (FoulView.Classify(bearing, along, null, field, coeff) == FoulViewKind.BigFoul) return ball;
            }
        return null;
    }
}
