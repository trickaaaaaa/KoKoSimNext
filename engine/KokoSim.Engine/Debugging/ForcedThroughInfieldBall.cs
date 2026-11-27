using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 強制発動「内野を抜けるゴロの安打」（issue #1591 の撮影用）の打球を作る。乱数は本流から引かず、決まった順に
/// 候補を試して最初に条件（内野を抜けた単打で、方向に応じた内野手2人が追う）を満たしたものを返す＝
/// 同じ守備位置・球場なら毎回同じ打球になる。見つからなければ null（強制しない）。
/// </summary>
public static class ForcedThroughInfieldBall
{
    public static BattedBall? For(
        ForcedOutcome kind, FieldGeometry field, Aerodynamics aero,
        IReadOnlyList<Fielder> fielders, FieldingCoefficients coeff)
    {
        var (bearings, pair) = kind switch
        {
            ForcedOutcome.ThroughInfieldLeft => (new[] { -22.0, -26.0, -18.0, -30.0, -14.0 }, (FieldPosition.ThirdBase, FieldPosition.Shortstop)),
            ForcedOutcome.ThroughInfieldMiddle => (new[] { -4.0, 4.0, -8.0, 8.0, 0.0 }, (FieldPosition.SecondBase, FieldPosition.Shortstop)),
            ForcedOutcome.ThroughInfieldRight => (new[] { 22.0, 26.0, 18.0, 30.0, 14.0 }, (FieldPosition.FirstBase, FieldPosition.SecondBase)),
            _ => (null, default((FieldPosition, FieldPosition))),
        };
        if (bearings is null) return null;

        // 探索用の乱数と係数: イレギュラー・失策を切り、守備の結果だけを見る
        var probe = coeff with { ErrorBaseProb = 0.0, IrregularBounceProb = 0.0 };
        foreach (var bearing in bearings)
            for (var launch = -6.0; launch <= 6.0; launch += 2.0)
                for (var exit = 30.0; exit <= 48.0; exit += 2.0)
                {
                    var ball = new BattedBall { ExitVelocityMps = exit, LaunchAngleDeg = launch, BearingDeg = bearing };
                    var play = FieldingResolver.ResolveDetailed(
                        ball, field, aero, BatterAttributes.LeagueAverage, fielders, probe, new Xoshiro256Random(1));
                    if (!play.ThroughInfield || play.Result != BattedBallResult.Single) continue;
                    if (play.ClosestInfielderRole is not { } a || play.SecondInfielderRole is not { } b) continue;
                    if ((a == pair.Item1 && b == pair.Item2) || (a == pair.Item2 && b == pair.Item1)) return ball;
                }
        return null;
    }
}

/// <summary>
/// 強制発動「二塁手のもたつくゴロ」（<see cref="ForcedOutcome.SlowHandleGrounder"/>）の打球を作る。乱数は本流から引かず、
/// 決まった順に候補を試して、内野を抜けずに二塁手が捕って送球するゴロを返す。見つからなければ null（強制しない）。
/// </summary>
public static class ForcedSlowHandleGrounder
{
    public static BattedBall? For(
        ForcedOutcome kind, FieldGeometry field, Aerodynamics aero,
        IReadOnlyList<Fielder> fielders, FieldingCoefficients coeff)
    {
        if (kind is not (ForcedOutcome.SlowHandleGrounder or ForcedOutcome.VerySlowHandleGrounder or ForcedOutcome.RecoveredWildThrowGrounder)) return null;
        var probe = coeff with { ErrorBaseProb = 0.0, IrregularBounceProb = 0.0 };
        foreach (var bearing in new[] { 12.0, 10.0, 14.0, 8.0, 16.0 })
            for (var launch = -6.0; launch <= 2.0; launch += 2.0)
                for (var exit = 28.0; exit <= 40.0; exit += 2.0)
                {
                    var ball = new BattedBall { ExitVelocityMps = exit, LaunchAngleDeg = launch, BearingDeg = bearing };
                    var play = FieldingResolver.ResolveDetailed(
                        ball, field, aero, BatterAttributes.LeagueAverage, fielders, probe, new Xoshiro256Random(1));
                    if (play.ThroughInfield || play.FielderRole != FieldPosition.SecondBase) continue;
                    if (play.Result is not (BattedBallResult.Out or BattedBallResult.Single)) continue;
                    if (play.ThrowArriveSeconds is not > 0.0 || play.UnassistedPutout) continue;
                    // 逸れた送球を一塁手が拾い直してアウト（issue #1735）になる打球だけ
                    if (kind == ForcedOutcome.RecoveredWildThrowGrounder
                        && (play.RecoveredThrowScatterM is null || play.RecoveredThrowByBackup)) continue;
                    return ball;
                }
        return null;
    }
}

/// <summary>
/// 強制発動「内野手が捕るライナーのアウト」（<see cref="ForcedOutcome.InfieldLineOut"/>・issue #1724 の撮影用）の打球を作る。
/// 乱数は本流から引かず、決まった順に候補を試して、内野手が通り道（<c>TryLineDriveCatch</c>）でノーバウンド捕球する
/// ライナーを返す。見つからなければ null（強制しない）。
/// </summary>
public static class ForcedInfieldLineOutBall
{
    public static BattedBall? For(
        ForcedOutcome kind, FieldGeometry field, Aerodynamics aero,
        IReadOnlyList<Fielder> fielders, FieldingCoefficients coeff)
    {
        if (kind != ForcedOutcome.InfieldLineOut) return null;
        var probe = coeff with { ErrorBaseProb = 0.0, IrregularBounceProb = 0.0, FlyErrorBaseProb = 0.0 };
        // 野手がほとんど動かずに捕る（正面のライナー）打球を選ぶ＝捕った後の体の動きを見るのに、走り込みの動きが混ざらない
        BattedBall? best = null;
        var bestMove = double.PositiveInfinity;
        foreach (var bearing in new[] { -14.0, -10.0, -18.0, -6.0, 6.0, 10.0, 14.0, -22.0, 18.0, 22.0 })
            for (var launch = 0.0; launch <= 12.0; launch += 1.0)
                for (var exit = 46.0; exit >= 30.0; exit -= 1.0)
                {
                    var ball = new BattedBall { ExitVelocityMps = exit, LaunchAngleDeg = launch, BearingDeg = bearing };
                    var play = FieldingResolver.ResolveDetailed(
                        ball, field, aero, BatterAttributes.LeagueAverage, fielders, probe, new Xoshiro256Random(1));
                    if (play.Result != BattedBallResult.Out || !play.IsFly || play.Class != BattedBallClass.Liner) continue;
                    if (play.FielderRole is not { } who || !IsInfielder(who)) continue;
                    var f = fielders.First(x => x.Position == who);
                    var move = Math.Sqrt(Math.Pow(play.LandingX - f.Location.X, 2) + Math.Pow(play.LandingZ - f.Location.Z, 2));
                    if (move >= bestMove - 1e-9) continue;
                    bestMove = move;
                    best = ball;
                }
        return best;
    }

    private static bool IsInfielder(FieldPosition p) =>
        p is FieldPosition.FirstBase or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop;
}
