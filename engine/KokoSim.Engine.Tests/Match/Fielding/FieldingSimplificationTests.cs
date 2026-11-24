using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 守備・長打の解決の簡略化が規則・他経路と揃っていること（issue #1243）。
/// フェンス直撃の空中捕球／投手の飛球への反応遅延／最速1人だけの判定／転がりの柵到達点／ファンブルとCarom。
/// </summary>
public sealed class FieldingSimplificationTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly FieldingCoefficients Coeff = new();
    private static readonly Aerodynamics Aero = new();

    private static double Budget(double hang, FielderAttributes a)
        => Math.Min(hang * Coeff.CatchReachFactor, Coeff.CatchReachCapSeconds)
           * Math.Max(1.0, 1.0 + (a.Fielding - 50) * Coeff.CatchReachFieldingSlope);

    private static FieldingPlay? FoulFly(BattedBall ball, params Fielder[] fielders)
        => FieldingResolver.ResolveFoulFly(ball, Field, Aero, fielders, Coeff, runnerTime: 4.3);

    private static readonly BattedBall Pop = new() { ExitVelocityMps = 105.0 / 3.6, LaunchAngleDeg = 62.0, BearingDeg = 58.0 };

    /// <summary>落下点から本塁側へ <paramref name="reachSeconds"/> で着く位置（その野手の脚力・反応遅延で逆算）。</summary>
    private static Vector3D SpotWithReach(Vector3D landing, FielderAttributes a, double reachSeconds)
    {
        var d = (reachSeconds - a.ReactionDelaySeconds) * a.SprintSpeedMps;
        return new Vector3D(landing.X, 0, landing.Z - d);
    }

    // ---- 2. 投手の追加反応遅延が飛球・ファウルフライにも乗る ----

    [Fact]
    public void FoulFly_PitcherPaysTheFollowThroughReactionPenalty()
    {
        var ground = BallisticIntegrator.IntegrateToGround(Pop.ContactPoint(), Pop.InitialVelocity(), Pop.SpinVector(), Aero);
        var attrs = FielderAttributes.LeagueAverage;
        var budget = Budget(ground.HangTimeSeconds, attrs);
        // 予算の 0.1 秒手前に着ける位置＝罰則 0.35 秒を足すと間に合わない。
        var spot = SpotWithReach(ground.LandingPosition, attrs, budget - 0.1);

        var asFirst = FoulFly(Pop, new Fielder(FieldPosition.FirstBase, spot, attrs));
        Assert.NotNull(asFirst);
        var asPitcher = FoulFly(Pop, new Fielder(FieldPosition.Pitcher, spot, attrs));
        Assert.Null(asPitcher);
    }

    // ---- 6. 着地点の空中捕球は「最速の1人」でなく「予算内に届く野手の中で最速」 ----

    [Fact]
    public void FoulFly_FastestRunnerOutOfBudget_DoesNotHideASlowerFielderWhoCanCatch()
    {
        var ground = BallisticIntegrator.IntegrateToGround(Pop.ContactPoint(), Pop.InitialVelocity(), Pop.SpinVector(), Aero);
        var hang = ground.HangTimeSeconds;
        // A: 足は速いが守備は平凡（予算は小さい）。B: 足は遅いが守備が良く予算が大きい＋反応が速い。
        var a = new FielderAttributes { Speed = 99, Fielding = 20 };
        var b = new FielderAttributes { Speed = 40, Fielding = 99 };
        var found = false;
        for (var d = 5.0; d <= 60.0 && !found; d += 0.05)
        {
            var land = ground.LandingPosition;
            var spot = new Vector3D(land.X, 0, land.Z - d);
            var reachA = d / a.SprintSpeedMps + a.ReactionDelaySeconds;
            var reachB = d / b.SprintSpeedMps + b.ReactionDelaySeconds;
            if (reachA < reachB && reachA > Budget(hang, a) && reachB <= Budget(hang, b))
            {
                found = true;
                var play = FoulFly(Pop,
                    new Fielder(FieldPosition.LeftField, spot, a),
                    new Fielder(FieldPosition.RightField, spot, b));
                Assert.NotNull(play);
                Assert.Equal(FieldPosition.RightField, play!.FielderRole);
            }
        }
        Assert.True(found, "テストの前提（Aが最速だが予算外・Bは予算内）を満たす距離が無い");
    }

    // ---- 1. フェンス直撃（グラブの届かない高さ）は空中捕球にならない ----

    [Fact]
    public void FenceHitAboveGloveReach_IsNotAnAirCatch()
    {
        var tested = 0;
        foreach (var bearing in new[] { 0.0, 20.0, -25.0 })
        {
            var fenceDist = Field.FenceDistance(bearing * Math.PI / 180.0);
            for (var launch = 22.0; launch <= 38.0; launch += 1.0)
            {
                for (var kmh = 130.0; kmh <= 170.0; kmh += 2.0)
                {
                    var ball = new BattedBall { ExitVelocityMps = kmh / 3.6, LaunchAngleDeg = launch, BearingDeg = bearing };
                    var g = BallisticIntegrator.IntegrateToGround(
                        ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), Aero, fenceDistanceM: fenceDist);
                    if (!g.CrossedFence) continue;
                    var y = g.FenceCrossPosition.Y;
                    if (y <= Coeff.LineDriveReachHeightM + 0.3 || y >= Field.FenceHeightM - 0.1) continue;
                    if (g.FenceCrossSeconds < 4.5) continue;

                    // 柵の真下に外野手を置く（柵際の減点を払っても予算が余る長い滞空）。
                    var wallSpot = new Vector3D(g.FenceCrossPosition.X, 0, g.FenceCrossPosition.Z);
                    var fielders = Field.StandardAlignment()
                        .Select(f => f.Position == FieldPosition.CenterField ? f with { Location = wallSpot } : f)
                        .ToList();
                    var play = FieldingResolver.ResolveDetailed(
                        ball, Field, Aero, BatterAttributes.LeagueAverage, fielders, Coeff with { ErrorBaseProb = 0.0 },
                        new Xoshiro256Random(7));
                    tested++;
                    Assert.True(play.Result is BattedBallResult.Single or BattedBallResult.Double or BattedBallResult.Triple,
                        $"柵に{y:F1}mで当たった打球が {play.Result}（bearing {bearing} launch {launch} {kmh}km/h）");
                }
            }
        }
        Assert.True(tested > 0, "前提を満たす打球が見つからない");
    }

    // ---- 3. 回収は停止点に最速の1人でなく、途中で最初に追いつける野手 ----

    [Fact]
    public void Retrieve_PicksTheFielderWhoInterceptsFirst_NotTheOneNearestTheStopPoint()
    {
        var roll = ExtraBaseResolver.RollFrom(
            new Vector3D(0, 0, 60), new Vector3D(0, 0, 1), speedMps: 22.0, startSeconds: 2.0,
            fenceDistanceM: 118.0, Coeff);
        var stop = roll.StopPosition;
        // 停止点の近くで待つ中堅手。
        var deep = new Fielder(FieldPosition.CenterField, new Vector3D(0, 0, stop.Z - 1.0), FielderAttributes.LeagueAverage);
        // 球筋の途中（停止点よりかなり手前）へ横から詰める左翼手。
        var gap = new Fielder(FieldPosition.LeftField, new Vector3D(-14, 0, 78), FielderAttributes.LeagueAverage);

        var (soloFielder, soloAt) = ExtraBaseResolver.Retrieve(new[] { deep }, roll, Coeff);
        Assert.Equal(FieldPosition.CenterField, soloFielder.Position);
        var (who, at) = ExtraBaseResolver.Retrieve(new[] { deep, gap }, roll, Coeff);
        Assert.Equal(FieldPosition.LeftField, who.Position);
        Assert.True(at < soloAt, $"途中で追いつける野手がいるのに回収が早まらない（{at:F2} ≥ {soloAt:F2}）");
    }

    // ---- 4. 放射方向でない転がりも柵の上で止まる／跳ね返る ----

    [Fact]
    public void RollAlongANonRadialLine_ReachesTheFenceCurve()
    {
        // 右中間寄り（方位約45°）から真っ直ぐ奥へ転がる球。放射方向ではないので、柵までの距離は
        // 「着地点の半径との差」ではなく柵の曲線との交点で決まる。
        var start = new Vector3D(60, 0, 60);
        var roll = ExtraBaseResolver.RollFrom(
            start, new Vector3D(0, 0, 1), speedMps: 40.0, startSeconds: 3.0,
            fenceDistanceM: Field.FenceDistance(Math.PI / 4), Coeff, Field);
        Assert.True(roll.ReachedFence);
        var hit = roll.StopPosition;
        var r = Math.Sqrt(hit.X * hit.X + hit.Z * hit.Z);
        var bearing = Math.Atan2(hit.X, hit.Z);
        Assert.Equal(Field.FenceDistance(bearing), r, 1);
        var carom = ExtraBaseResolver.ComputeFenceCarom(roll, Coeff);
        Assert.NotNull(carom);
        Assert.Equal(hit.X, carom!.Value.HitPoint.X, 6);
        Assert.Equal(hit.Z, carom.Value.HitPoint.Z, 6);
    }

    // ---- 5. ファンブルで遅れた回収には、壁に当たった跳ね返り（Carom）を付けない ----

    [Fact]
    public void FumbledRetrieval_CaromOnlyWhenTheOriginalRetrievalWasAfterTheWallHit()
    {
        var coeff = Coeff with
        {
            OutfieldFumbleBaseProb = 1.0,
            OutfieldFumbleSpeedRefMps = 1.0,
            OutfieldFumbleDelayMeanSeconds = 1.5,
            ErrorMaxProb = 1.0,
        };
        var checkedPlays = 0;
        ulong seed = 1;
        foreach (var bearing in new[] { -30.0, -10.0, 0.0, 12.0, 30.0 })
        {
            for (var launch = 8.0; launch <= 30.0; launch += 2.0)
            {
                for (var kmh = 110.0; kmh <= 160.0; kmh += 5.0)
                {
                    var ball = new BattedBall { ExitVelocityMps = kmh / 3.6, LaunchAngleDeg = launch, BearingDeg = bearing };
                    var play = FieldingResolver.ResolveDetailed(
                        ball, Field, Aero, BatterAttributes.LeagueAverage, Field.StandardAlignment(), coeff,
                        new Xoshiro256Random(seed++));
                    if (play.ErrorKind != FieldingErrorKind.Fumble || play.Carom is null) continue;
                    checkedPlays++;
                    Assert.True(play.ErrorAtSeconds > play.Carom.Value.HitAtSeconds + 1e-9,
                        $"壁に当たる前の回収（{play.ErrorAtSeconds:F2}s）なのに跳ね返りが付いている（衝突 {play.Carom.Value.HitAtSeconds:F2}s）");
                }
            }
        }
        Assert.True(checkedPlays > 0, "前提のプレーが無い");
    }
}
