using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 送球トランスファー（握り替え）の能力接続（Issue #36）。守備(Fielding)50で現行固定値と恒等、
/// 守備差で握り替え所要（＝一塁送球到達時刻）が単調に変化することを確認する。
/// </summary>
public sealed class TransferFactorTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();
    // 失策乱数を止めて判定を送球到達時刻だけに絞る。
    private static readonly FieldingCoefficients Coeff = new() { ErrorBaseProb = 0 };

    [Fact]
    public void TransferFactor_IsIdentityAtFifty()
    {
        var avg = FielderAttributes.LeagueAverage;
        Assert.Equal(1.0, avg.TransferFactor(Coeff.ThrowTransferFieldingSlope, Coeff.ThrowTransferFactorMin), 12);
    }

    [Fact]
    public void TransferFactor_DecreasesMonotonicallyWithFielding()
    {
        var prev = double.MaxValue;
        for (var fielding = 1; fielding <= 100; fielding++)
        {
            var f = new FielderAttributes { Fielding = fielding };
            var factor = f.TransferFactor(Coeff.ThrowTransferFieldingSlope, Coeff.ThrowTransferFactorMin);
            Assert.True(factor <= prev + 1e-12, $"守備{fielding} で倍率が増加した");
            prev = factor;
        }
    }

    [Fact]
    public void TransferFactor_ClampsAtFloor()
    {
        // 下限倍率より速くならない（守備が理論上限を超えても最速側でクランプ）。
        var elite = new FielderAttributes { Fielding = 100 };
        var factor = elite.TransferFactor(slopePerPoint: 0.02, minFactor: 0.70);
        Assert.Equal(0.70, factor, 12);
    }

    // 内野ゴロ（三遊間寄りの低い打球）。range<=InfieldDepth で一塁送球が発生する。
    private static FieldingPlay Grounder(int teamFielding, FieldingCoefficients coeff)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 26.0, LaunchAngleDeg = 4.0, BearingDeg = -12.0 },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = teamFielding }),
            coeff, new Xoshiro256Random(777));

    [Fact]
    public void InfieldGrounder_ThrowArrivesEarlierForBetterFielder()
    {
        var poor = Grounder(20, Coeff);
        var good = Grounder(80, Coeff);

        Assert.NotNull(poor.ThrowArriveSeconds);
        Assert.NotNull(good.ThrowArriveSeconds);
        Assert.True(good.ThrowArriveSeconds < poor.ThrowArriveSeconds,
            $"守備が高い野手の送球到達 {good.ThrowArriveSeconds:F3}s が低い野手 {poor.ThrowArriveSeconds:F3}s 以上だった");
    }

    [Fact]
    public void Transfer_ContributesIndependentlyOfReaction()
    {
        // 反応遅延(Fielding)由来の差を除いても、握り替え倍率だけで送球到達に差が出ることを確認。
        // 傾き0（トランスファー無効）と現行傾きを同じ守備値で比べる。
        var fixedTransfer = Coeff with { ThrowTransferFieldingSlope = 0.0 };
        var elite = Grounder(90, fixedTransfer).ThrowArriveSeconds;
        var eliteWithTransfer = Grounder(90, Coeff).ThrowArriveSeconds;

        Assert.NotNull(elite);
        Assert.NotNull(eliteWithTransfer);
        // 守備90 → 倍率<1 → 握り替えが短縮 → 到達が早い。
        Assert.True(eliteWithTransfer < elite,
            $"トランスファー適用 {eliteWithTransfer:F3}s が無効時 {elite:F3}s 以上（握り替えが効いていない）");
    }

    // === 走塁系トランスファー（捕手ポップ / 外野返球・中継 / 帰塁）===

    private static readonly BaserunningCoefficients Br = new();

    [Fact]
    public void CatcherPop_FasterTransferForBetterFieldingCatcher()
    {
        double PopDefense(int fielding) => StealResolver.DefenseTimeSeconds(
            new Player { ArmStrength = 50, Fielding = fielding }, Br);

        Assert.Equal(PopDefense(50), StealResolver.DefenseTimeSeconds(new Player { ArmStrength = 50 }, Br), 12); // 守備50で恒等
        Assert.True(PopDefense(90) < PopDefense(50), "守備の高い捕手のポップが速くない");
        Assert.True(PopDefense(50) < PopDefense(20), "守備の低い捕手のポップが遅くない");
    }

    [Fact]
    public void HomeThrow_FasterTransferForBetterOutfielder_DirectAndRelay()
    {
        var field = new FieldGeometry();
        // 直接返球（近い）と中継（遠い＝カットオフ閾値超）の両経路で守備が効くこと。
        HomePlaySituation Sit(double z, int fielding) => new(
            new Vector3D(0, 0, z), 2.0, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps, fielding);

        foreach (var z in new[] { 40.0, 95.0 })
        {
            var avg = HomePlayResolver.DefenseTimeSeconds(Sit(z, 50), Br);
            var good = HomePlayResolver.DefenseTimeSeconds(Sit(z, 90), Br);
            var poor = HomePlayResolver.DefenseTimeSeconds(Sit(z, 20), Br);
            Assert.Equal(avg, HomePlayResolver.DefenseTimeSeconds(Sit(z, 50), Br), 12); // 恒等
            Assert.True(good < avg, $"z={z}: 守備90の返球が速くない");
            Assert.True(avg < poor, $"z={z}: 守備20の返球が遅くない");
        }
    }

    [Fact]
    public void DoubledOff_FasterReturnThrowForBetterFielder()
    {
        double Def(int fielding) => DoubledOffResolver.DefenseReturnSeconds(30.0, 32.0, Br, fielding);

        Assert.Equal(Def(50), DoubledOffResolver.DefenseReturnSeconds(30.0, 32.0, Br), 12); // 既定50で恒等
        Assert.True(Def(90) < Def(50), "守備の高い野手の帰塁送球が速くない");
        Assert.True(Def(50) < Def(20), "守備の低い野手の帰塁送球が遅くない");
    }
}
