using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>
/// 本塁打の校正（issue #976・監督決定 案c）の2つの部品:
/// ①芯を食った当たりの角度の上側の締まり（<see cref="BattingCoefficients.UpperAngleFactor"/>）
/// ②フェンス際の捕りにくさ（<see cref="FieldingCoefficients.WallCatchPenalty"/>）。
/// </summary>
public sealed class HomeRunUpperSideTests
{
    private static readonly PitchPlan Plan = new() { Type = PitchType.Fastball, AimX = 0.0, AimY = 0.5, VelocityKmh = 135.0, Stuff = 0.0 };
    private static readonly PitchTrajectoryFeatures Features = new(0.0, 0.0, 0.45);

    [Fact]
    public void UpperAngleFactor_IsIdentityBelowTheKnee_AndTightensHardContact()
    {
        var c = new BattingCoefficients { LaunchAngleUpperHardKnee = 0.83, LaunchAngleUpperHardSlope = 6.0, LaunchAngleUpperHardFloor = 0.2 };
        Assert.Equal(1.0, c.UpperAngleFactor(0.70), 12);
        Assert.Equal(1.0, c.UpperAngleFactor(0.83), 12);
        Assert.Equal(1.0 - 6.0 * 0.07, c.UpperAngleFactor(0.90), 12);
        Assert.Equal(0.2, c.UpperAngleFactor(1.00), 12);   // 下限
        // 膝0以下＝無効（対称）
        Assert.Equal(1.0, (c with { LaunchAngleUpperHardKnee = 0.0 }).UpperAngleFactor(1.00), 12);
    }

    /// <summary>上側の締まりは平均より上へ振れた打球だけを低くし、初速は変えない。乱数の消費も変えない。</summary>
    [Fact]
    public void UpperHardTightening_LowersOnlyUpwardDeviations_KeepsExitVelocity_AndTheRandomStream()
    {
        var sym = new BattingCoefficients { LaunchAngleUpperHardKnee = 0.0, PopupShare = 0.0 };
        var tight = sym with { LaunchAngleUpperHardKnee = 0.83, LaunchAngleUpperHardSlope = 6.0 };
        int lowered = 0, same = 0;
        for (ulong seed = 1; seed <= 2000; seed++)
        {
            var ra = new Xoshiro256Random(seed);
            var rb = new Xoshiro256Random(seed);
            var a = ContactModel.Resolve(BatterAttributes.LeagueAverage, Plan, Features, true, sym, ra);
            var b = ContactModel.Resolve(BatterAttributes.LeagueAverage, Plan, Features, true, tight, rb);
            Assert.Equal(ra.NextUInt64(), rb.NextUInt64());
            if (a.Ball is null) { Assert.Null(b.Ball); continue; }
            Assert.Equal(a.Ball.ExitVelocityMps, b.Ball!.ExitVelocityMps, 12);
            Assert.True(b.Ball.LaunchAngleDeg <= a.Ball.LaunchAngleDeg + 1e-9);
            if (b.Ball.LaunchAngleDeg < a.Ball.LaunchAngleDeg - 1e-9) lowered++;
            else same++;
        }
        Assert.True(lowered > 0 && same > 0, $"強い当たりの上側だけが下がるはず（下がった {lowered} / 同じ {same}）");
    }

    [Fact]
    public void WallCatchPenalty_IsMaximalAtTheFence_AndZeroOutsideTheZone()
    {
        var c = new FieldingCoefficients { WallCatchZoneM = 10.0, WallCatchPenaltySeconds = 3.0 };
        Assert.Equal(3.0, c.WallCatchPenalty(0.0), 12);
        Assert.Equal(3.0, c.WallCatchPenalty(-1.0), 12);   // フェンス直撃（距離0以下）
        Assert.Equal(1.5, c.WallCatchPenalty(5.0), 12);
        Assert.Equal(0.0, c.WallCatchPenalty(10.0), 12);
        Assert.Equal(0.0, c.WallCatchPenalty(40.0), 12);
        Assert.Equal(0.0, new FieldingCoefficients { WallCatchZoneM = 0.0, WallCatchPenaltySeconds = 3.0 }.WallCatchPenalty(0.0), 12);
    }
}
