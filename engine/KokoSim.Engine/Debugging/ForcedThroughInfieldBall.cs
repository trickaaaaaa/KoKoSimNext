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
