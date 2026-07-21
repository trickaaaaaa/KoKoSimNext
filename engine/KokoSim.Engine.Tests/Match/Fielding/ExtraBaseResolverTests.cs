using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 塁打数を「着地距離のしきい値」ではなく幾何と走力から決めることの検証（Issue #24）。
/// 同じ打球でも落ちる場所（ギャップ／野手の正面）と打者の走力で塁打数が変わることを固定条件で確認する。
/// </summary>
public sealed class ExtraBaseResolverTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();
    private static readonly FieldingCoefficients Coeff = new() { ErrorBaseProb = 0 };

    // 標準守備位置: 左翼 −28.0°, 中堅 0°, 右翼 +28.0°（FieldGeometry.StandardAlignment）。
    private const double AtLeftFielderDeg = -28.0;
    private const double LeftCenterGapDeg = -14.0;

    private static BattedBallResult Resolve(
        double bearingDeg, double exitMps = 43.0, double launchDeg = 8.0,
        BatterAttributes? batter = null, ulong seed = 12345)
        => FieldingResolver.Resolve(
            new BattedBall { ExitVelocityMps = exitMps, LaunchAngleDeg = launchDeg, BearingDeg = bearingDeg },
            Field, Aero, batter ?? BatterAttributes.LeagueAverage,
            Field.StandardAlignment(), Coeff, new Xoshiro256Random(seed));

    [Fact]
    public void SameCarry_GapFalls_ForExtraBase_ButStraightAtFielder_StaysSingle()
    {
        // 方位角だけが違う同一打球（＝着地距離は同じ）。着地距離しきい値の実装では両者が必ず同じ結果になる。
        var gap = Resolve(LeftCenterGapDeg);
        var atFielder = Resolve(AtLeftFielderDeg);

        Assert.Equal(BattedBallResult.Double, gap);
        Assert.Equal(BattedBallResult.Single, atFielder);
    }

    [Fact]
    public void FasterBatter_TakesAtLeastAsManyBases()
    {
        var slow = Resolve(LeftCenterGapDeg, batter: new BatterAttributes { Speed = 20 });
        var fast = Resolve(LeftCenterGapDeg, batter: new BatterAttributes { Speed = 95 });

        Assert.True(Bases(fast) >= Bases(slow), $"俊足 {fast} が鈍足 {slow} を下回った");
        Assert.True(Bases(fast) > Bases(slow), $"走力が塁打数に効いていない（両方 {fast}）");
    }

    [Fact]
    public void HitResult_DoesNotConsumeRandomness()
    {
        // 安打の塁打数は幾何と時間だけで決まる（乱数を引かない）＝どのシードでも同じ結果。
        var expected = Resolve(LeftCenterGapDeg, seed: 1);
        for (ulong seed = 2; seed < 40; seed++)
        {
            Assert.Equal(expected, Resolve(LeftCenterGapDeg, seed: seed));
        }
    }

    [Fact]
    public void FlatLandingRollsFartherThanSteepLanding()
    {
        // 同じ着地速度の大きさでも、水平に近い打球は滑って伸び、真上から落ちる打球は失速して死ぬ。
        var landing = new Vector3D(0, 0, 60);
        var flat = ExtraBaseResolver.ComputeRoll(landing, new Vector3D(0, -6, 28), 2.0, 118.0, Coeff);
        var steep = ExtraBaseResolver.ComputeRoll(landing, new Vector3D(0, -28, 6), 4.5, 118.0, Coeff);

        Assert.True(flat.RollDistanceM > steep.RollDistanceM * 3,
            $"ライナー {flat.RollDistanceM:F1}m / 大飛球 {steep.RollDistanceM:F1}m");
    }

    [Fact]
    public void RollStopsAtFence()
    {
        var landing = new Vector3D(0, 0, 100);
        var roll = ExtraBaseResolver.ComputeRoll(landing, new Vector3D(0, -4, 35), 2.5, 118.0, Coeff);

        Assert.True(roll.ReachedFence);
        Assert.Equal(118.0, roll.StopPosition.Length, 3);
    }

    [Fact]
    public void RollPath_PositionIsMonotoneAndStopsAtStopPosition()
    {
        var roll = ExtraBaseResolver.ComputeRoll(
            new Vector3D(0, 0, 55), new Vector3D(0, -8, 30), 1.8, 118.0, Coeff);

        var prev = 0.0;
        for (var t = 1.8; t < 20.0; t += 0.25)
        {
            var d = roll.PositionAt(t).Length;
            Assert.True(d >= prev - 1e-9, $"t={t} で後退した");
            prev = d;
        }
        Assert.Equal(roll.StopPosition.Length, roll.PositionAt(30.0).Length, 6);
    }

    private static int Bases(BattedBallResult r) => r switch
    {
        BattedBallResult.Single => 1,
        BattedBallResult.Double => 2,
        BattedBallResult.Triple => 3,
        _ => 0,
    };
}
