using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// フライ落球の難度連動（issue #532）。フライ捕球に「捕球余裕(catchMargin)」由来の難度を入れ、平凡な飛球
/// （捕球余裕が大きい）ほど落球確率が下限へ、際どい飛球（余裕≈0）ほど基準確率フルへ伸縮することを固定する。
/// 修正前は内野ゴロと同じ基準確率(ErrorBaseProb)を余裕に関わらず一律で引いていた（＝全飛球で同じ落球率）。
/// </summary>
public sealed class FlyCatchDifficultyTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();
    private static readonly FieldingCoefficients Coeff = new();

    // 中堅方向への打ち上げた飛球（apex≥2.5m のフライ）。着地点・滞空は野手配置に依らず打球だけで決まる。
    private static readonly BattedBall Fly =
        new() { ExitVelocityMps = 30.0, LaunchAngleDeg = 34.0, BearingDeg = 0.0 };

    /// <summary>捕手＋中堅手1人の最小配置（捕手は空中捕球の候補から除外される）。中堅手の立ち位置で捕球余裕を作る。</summary>
    private static IReadOnlyList<Fielder> WithCenterFielderAt(Vector3D loc)
    {
        var attr = new FielderAttributes { Fielding = 50, Catching = 50 };
        return new[]
        {
            new Fielder(FieldPosition.Catcher, new Vector3D(0, 0, -1.0), attr),
            new Fielder(FieldPosition.CenterField, loc, attr),
        };
    }

    private static (Vector3D Landing, double HangTime, double ApexHeight) ProbeFly()
    {
        // 着地点・滞空・頂点は打球の弾道積分だけで決まる（野手に依らない）。標準配置で1回だけ解いて読む。
        var p = FieldingResolver.ResolveDetailed(
            Fly, Field, Aero, BatterAttributes.LeagueAverage, Field.StandardAlignment(), Coeff,
            new Xoshiro256Random(1));
        return (new Vector3D(p.LandingX, 0, p.LandingZ), p.HangTimeSeconds, p.ApexHeightM);
    }

    private static double DropRate(Vector3D centerFielderLoc, int trials = 8000)
    {
        var fielders = WithCenterFielderAt(centerFielderLoc);
        var drops = 0;
        var catches = 0;
        for (ulong seed = 1; seed <= (ulong)trials; seed++)
        {
            var r = FieldingResolver.ResolveDetailed(
                Fly, Field, Aero, BatterAttributes.LeagueAverage, fielders, Coeff, new Xoshiro256Random(seed));
            if (r.Result == BattedBallResult.Error) drops++;
            if (r is { IsFly: true, Result: BattedBallResult.Out or BattedBallResult.Error }) catches++;
        }
        // 際どい配置でも「捕球を試みている（＝安打で抜けていない）」ことを担保してから率を返す。
        Assert.True(catches >= trials * 9 / 10, $"捕球機会が少なすぎる（{catches}/{trials}）");
        return (double)drops / trials;
    }

    [Fact]
    public void RoutineFly_DropsFarLessThanTightFly()
    {
        var (landing, hang, apex) = ProbeFly();
        Assert.True(apex >= Coeff.FlyApexThresholdM, $"フライになっていない（apex {apex:F1}m）");

        // 平凡: 中堅手が着地点の真下で待つ＝ほぼ移動なし＝捕球余裕が大きい（難度0）。
        var routine = DropRate(landing);

        // 際どい: 捕球予算ぎりぎりの距離に立たせる＝到達所要≈予算＝捕球余裕≈0（難度≈1）。
        var attr = new FielderAttributes { Fielding = 50 };
        var budget = Math.Min(hang * Coeff.CatchReachFactor, Coeff.CatchReachCapSeconds); // Fielding50→routeFactor 1.0
        var travel = (budget - attr.ReactionDelaySeconds - 0.05) * attr.SprintSpeedMps;   // 到達所要が予算−0.05秒になる距離
        Assert.True(travel > 0, "際どい配置を作れる滞空がない");
        var tight = DropRate(new Vector3D(landing.X, 0, landing.Z - travel));

        Assert.True(routine < tight,
            $"平凡フライの落球率 {routine:P2} が際どいフライ {tight:P2} 以上（難度連動していない）");
        // 平凡フライは下限（ErrorMinProb=0.001）近くまで落ちる＝内野ゴロ基準(0.0384)を余裕に関わらず引いていた
        // 修正前より明確に低い。
        Assert.True(routine < 0.01, $"平凡フライの落球率 {routine:P2} が高すぎる（難度0で下限へ落ちていない）");
        // 際どいフライは有意に落とす（フライ専用基準確率が効いている）。
        Assert.True(tight > 0.05, $"際どいフライの落球率 {tight:P2} が低すぎる（フライ基準確率が効いていない）");
    }

    [Fact]
    public void TightFly_BetterCatcherDropsLess()
    {
        // 際どい飛球（難度が立つ場面）では守備力（Catching）が高いほど落球が減る＝守備力連動が壊れていない。
        var (landing, hang, _) = ProbeFly();
        var attr = new FielderAttributes { Fielding = 50 };
        var budget = Math.Min(hang * Coeff.CatchReachFactor, Coeff.CatchReachCapSeconds);
        var travel = (budget - attr.ReactionDelaySeconds - 0.05) * attr.SprintSpeedMps;
        var loc = new Vector3D(landing.X, 0, landing.Z - travel);

        double DropWithCatching(int catching)
        {
            // 中堅手の到達は Fielding で決まる（走路取り）。ここは Catching だけを振り、到達所要を保つため
            // Fielding=50 に固定し Catching のみ変える。
            var fielders = new[]
            {
                new Fielder(FieldPosition.Catcher, new Vector3D(0, 0, -1.0), new FielderAttributes()),
                new Fielder(FieldPosition.CenterField, loc, new FielderAttributes { Fielding = 50, Catching = catching }),
            };
            var drops = 0;
            for (ulong seed = 1; seed <= 8000; seed++)
            {
                var r = FieldingResolver.ResolveDetailed(
                    Fly, Field, Aero, BatterAttributes.LeagueAverage, fielders, Coeff, new Xoshiro256Random(seed));
                if (r.Result == BattedBallResult.Error) drops++;
            }
            return drops / 8000.0;
        }

        var poor = DropWithCatching(20);
        var elite = DropWithCatching(90);
        Assert.True(poor > elite, $"守備下手 {poor:P2} が守備上手 {elite:P2} 以下（守備力連動が効いていない）");
    }
}
