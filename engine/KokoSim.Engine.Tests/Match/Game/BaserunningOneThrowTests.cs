using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 1打球の送球は1本（design-12 §3.5）と、本塁へ投げた場面の打者の扱い（issue #1240）。
/// 本塁へ投げたら打者は野選で一塁セーフ（本塁で刺したときだけ捕手の一塁転送と競う）。
/// </summary>
public sealed class BaserunningOneThrowTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();
    private static readonly Player Fast = new() { Speed = 85, Baserunning = 80 };

    private static HomePlayContext InfieldIn(
        double? batterToFirst = 4.3, FieldPosition? role = null, double aggression = 0.9)
        => new(Field,
            new HomePlaySituation(new Vector3D(0, 0, 35), 1.3, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), aggression, DefenseDepth.In, IsFly: false,
            ThrowArriveSeconds: 2.0, BatterToFirstSeconds: batterToFirst, FielderRole: role);

    private static HomePlayContext Fly(double distanceM, double fieldedAt, int arm)
        => new(Field,
            new HomePlaySituation(new Vector3D(0, 0, distanceM), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.9, DefenseDepth.Normal, IsFly: true);

    [Fact]
    public void InfieldIn_SafeAtHome_BatterReachesFirstOnFieldersChoice()
    {
        var rng = new Xoshiro256Random(11);
        var batter = new Player();
        var safe = 0;
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { Third = Fast };
            var (runs, extraOuts, _, fc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, batter, 0, C, rng, collectMoves: true, InfieldIn());
            if (runs == 0) continue;
            safe++;
            Assert.True(fc, "本塁へ投げてセーフ＝打者は野選で生きる");
            Assert.Equal(0, extraOuts);
            Assert.Same(batter, bases.First);
            Assert.Contains(moves, m => ReferenceEquals(m.Runner, batter) && m is { FromBase: 0, ToBase: 1, Out: false });
        }
        Assert.True(safe > 0, "一度も生還しない");
    }

    [Fact]
    public void InfieldIn_OutAtHome_BatterOnlyOutIfRelayCatchesHim()
    {
        var rng = new Xoshiro256Random(12);
        int out0 = 0, batterSafe = 0, batterOut = 0;
        for (var i = 0; i < 4000; i++)
        {
            var bases = new BaseState { Third = Fast };
            var (runs, extraOuts, _, fc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, InfieldIn());
            if (!moves.Any(m => m is { FromBase: 3, ToBase: 4, Out: true })) continue;
            out0++;
            Assert.Equal(0, runs);
            Assert.Equal(1, extraOuts);
            Assert.Contains(moves, m => m.HomeForceRelay); // 捕手の一塁転送を描く目印
            if (fc) batterSafe++; else batterOut++;
        }
        Assert.True(out0 > 0, "本塁憤死が一度も出ない");
        Assert.True(batterSafe > 0 && batterOut > 0, $"転送の勝負が片側に倒れている: safe={batterSafe} out={batterOut}");
    }

    [Fact]
    public void InfieldIn_R2R3_SafeAtHome_SecondRunnerAdvancesWithoutASecondThrow()
    {
        var rng = new Xoshiro256Random(13);
        var r2 = new Player { Speed = 50 };
        var sent = 0;
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { Second = r2, Third = Fast };
            var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, InfieldIn());
            if (runs == 0) continue;
            sent++;
            Assert.Equal(0, extraOuts);
            Assert.DoesNotContain(moves, m => m.Out);
            Assert.Same(r2, bases.Third);
        }
        Assert.True(sent > 0, "一度も生還しない");
    }

    [Theory]
    [InlineData(70.0, 3.2, 50)]
    [InlineData(85.0, 3.8, 50)]
    [InlineData(95.0, 4.5, 50)]
    [InlineData(95.0, 3.6, 70)]
    [InlineData(110.0, 4.2, 80)]
    public void SacFly_R2R3_ScoringThrowIsTheOnlyThrow_NoThirdOutAfterTheRun(double distanceM, double fieldedAt, int arm)
    {
        var rng = new Xoshiro256Random(14);
        var r2 = new Player { Speed = 60 };
        var scored = 0;
        for (var i = 0; i < 4000; i++)
        {
            var bases = new BaseState { Second = r2, Third = Fast };
            var (runs, extraOuts, _, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, Fly(distanceM, fieldedAt, arm));
            if (runs == 0) continue;
            scored++;
            Assert.Equal(0, extraOuts); // 得点した打球で別の走者がもう1本の送球で刺されて3アウト目＝送球2本
        }
        Assert.True(scored > 0, "一度も生還しない");
    }

    // 安打で本塁へ投げてセーフだった打球は、もう三塁へは投げられない（issue #1257）。
    // 修正前は本塁セーフで送球使用済みにならず、後続の一塁走者の三塁レースが2本目の送球として走り、
    // 2アウトでは3アウト目の後の得点が残った（runs=1 かつ三塁で憤死）。
    [Theory]
    [InlineData(34.0, 70.0, 3.0, 70)]
    [InlineData(20.0, 55.0, 2.4, 60)]
    [InlineData(-10.0, 80.0, 3.4, 50)]
    [InlineData(40.0, 78.0, 3.6, 40)]
    public void Single_HomeThrowSafe_NoSecondThrowToThird(double x, double z, double fieldedAt, int arm)
    {
        var home = new HomePlayContext(Field,
            new HomePlaySituation(new Vector3D(x, 0, z), fieldedAt, new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.9, DefenseDepth.Normal, IsFly: false);
        var rng = new Xoshiro256Random(21);
        var scoredWithThirdAttempt = 0;
        foreach (var outs in new[] { 0, 1, 2 })
        {
            for (var i = 0; i < 4000; i++)
            {
                var r1 = new Player { Speed = 50, Baserunning = 50 };
                var bases = new BaseState { First = r1, Second = new Player { Speed = 70, Baserunning = 60 } };
                var (runs, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                    bases, PlateAppearanceResult.Single, new Player(), outs, C, rng, collectMoves: true, home);
                if (runs == 0) continue;
                scoredWithThirdAttempt++;
                Assert.False(moves.Any(m => m is { ToBase: 3, Out: true }),
                    $"本塁へ投げて生還した打球で三塁へも投げて刺した（outs={outs}, runs={runs}）");
            }
        }
        Assert.True(scoredWithThirdAttempt > 0, "一度も生還しない");
    }

    [Fact]
    public void Single_R1R3_ThirdRunnerSafeAtHome_FirstRunnerDoesNotRaceToThird()
    {
        var home = new HomePlayContext(Field,
            new HomePlaySituation(new Vector3D(34, 0, 70), 3.0, new Player { ArmStrength = 70 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.9, DefenseDepth.Normal, IsFly: false);
        var rng = new Xoshiro256Random(22);
        var scored = 0;
        for (var i = 0; i < 6000; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 45, Baserunning = 50 }, Third = Fast };
            var (runs, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, new Player(), 2, C, rng, collectMoves: true, home);
            if (runs == 0) continue;
            scored++;
            Assert.DoesNotContain(moves, m => m is { FromBase: 1, ToBase: 3, Out: true });
        }
        Assert.True(scored > 0, "一度も生還しない");
    }

    [Fact]
    public void InfieldIn_R1R3_FirstBaseman_DoesNotTagSecondThenRaceHome()
    {
        // 一塁線寄りの一塁ゴロ（一塁自己フォース→二塁タッチが選ばれる捕球点＝DoublePlayRaceTests と同じ）。
        // 前進守備で三塁走者がいるなら本塁の勝負が続く＝二塁のタッチ失敗のあとにもう1本投げることになるので選ばない。
        var rng = new Xoshiro256Random(15);
        HomePlayContext Ctx(DefenseDepth depth) => new(Field,
            new HomePlaySituation(new Vector3D(26, 0, 10), 1.3, new Player { ArmStrength = 55 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.9, depth, IsFly: false,
            ThrowArriveSeconds: 2.0, BatterToFirstSeconds: 4.60, FielderRole: FieldPosition.FirstBase);
        int tagsNormal = 0;
        for (var i = 0; i < 2000; i++)
        {
            var r1 = new Player { Speed = 50, Baserunning = 50 };
            var inBases = new BaseState { First = r1, Third = Fast };
            var (_, _, _, _, _, inMoves) = BaserunningModel.ApplyDetailed(
                inBases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, Ctx(DefenseDepth.In));
            Assert.DoesNotContain(inMoves, m => m.FirstBaseSelfForceTag);

            var nBases = new BaseState { First = r1, Third = Fast };
            var (_, _, _, _, _, nMoves) = BaserunningModel.ApplyDetailed(
                nBases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, Ctx(DefenseDepth.Normal));
            if (nMoves.Any(m => m.FirstBaseSelfForceTag)) tagsNormal++;
        }
        Assert.True(tagsNormal > 0, "対照（通常守備）で一塁自己フォース代替が選ばれていない＝この検査が空振り");
    }
}
