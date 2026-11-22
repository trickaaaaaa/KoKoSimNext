using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #1258: スクイズの1つの処理は送球1本＝アウトは多くて1つ（ボールが実際にプレーされたとき）。
/// 守備は本塁へ投げるか一塁側（一塁／封殺）だけを処理するかを先に選ぶ。
/// </summary>
public sealed class SqueezeSingleThrowTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();
    private static readonly KokoSim.Engine.Match.Fielding.FieldingCoefficients FieldingCoeff = new();
    private static readonly IReadOnlyList<Fielder> Fielders = Field.StandardAlignment();
    private static readonly PitcherAttributes Pitcher = PitcherAttributes.LeagueAverage;

    private static int BallInPlayOuts(SqueezeOutcome o)
        => (o.BatterOut ? 1 : 0)
           + (o.Bunt is BuntResult.ForceOutAtSecond or BuntResult.ForceOutAtThird ? 1 : 0)
           + (o.RunnerOut ? 1 : 0);

    private static bool IsBallInPlay(SqueezeOutcome o)
        => o.Bunt is not (BuntResult.Foul or BuntResult.MissedBunt or BuntResult.PopOut or BuntResult.PopOutDoubledOff);

    public static IEnumerable<object[]> Setups()
    {
        foreach (var runnerLevel in new[] { 1, 50 })
        foreach (var onFirst in new[] { false, true })
            yield return new object[] { runnerLevel, onFirst, onFirst };
    }

    [Theory]
    [MemberData(nameof(Setups))]
    public void BallInPlay_NeverProducesTwoOuts_AndBatterAndRunnerOutsAreExclusive(int runnerLevel, bool onFirst, bool onSecond)
    {
        var rng = new Xoshiro256Random(11);
        var batter = new Player { Bunt = 70, Speed = 50 };
        var runner = new Player { Baserunning = runnerLevel, Speed = runnerLevel };
        Player? first = onFirst ? new Player { Speed = 40, Baserunning = 40 } : null;
        Player? second = onSecond ? new Player { Speed = 40, Baserunning = 40 } : null;
        var played = 0;
        for (var i = 0; i < 6000; i++)
        {
            var o = SqueezeResolver.Resolve(batter, runner, Pitcher, 0.0, 0.0, first, second, Field, Fielders, FieldingCoeff, 0.0, C, rng);
            if (!IsBallInPlay(o)) continue;
            played++;
            Assert.True(BallInPlayOuts(o) <= 1, $"1つの処理で{BallInPlayOuts(o)}アウト: {o.Bunt} batterOut={o.BatterOut} runnerOut={o.RunnerOut}");
            if (o.RunnerOut)
            {
                // 本塁へ投げた＝一塁側は処理していない（打者は生存・安打にもならない）。
                Assert.Equal(BuntResult.ForceOutAtHome, o.Bunt);
                Assert.False(o.BatterOut);
                Assert.Equal(0, o.Runs);
            }
            else
            {
                Assert.Equal(1, o.Runs);
            }
        }
        Assert.True(played > 1000);
    }

    [Fact]
    public void SlowRunner_SometimesDrawsAHomeThrow_FastRunnerNever()
    {
        int HomeThrowOuts(int level)
        {
            var rng = new Xoshiro256Random(5);
            var n = 0;
            for (var i = 0; i < 6000; i++)
            {
                var o = SqueezeResolver.Resolve(new Player { Bunt = 70 }, new Player { Baserunning = level, Speed = level },
                    Pitcher, 0.0, 0.0, null, null, Field, Fielders, FieldingCoeff, 0.0, C, rng);
                if (o.RunnerOut && IsBallInPlay(o)) n++;
            }
            return n;
        }
        Assert.True(HomeThrowOuts(1) > 0, "遅い走者が一度も本塁で刺されない");
        Assert.Equal(0, HomeThrowOuts(99));
    }

    [Fact]
    public void HomeThrowThatFindsRunnerSafe_NeverGivesTheDefenseABatterOut()
    {
        var rng = new Xoshiro256Random(21);
        var seenSafeAtHome = false;
        for (var i = 0; i < 20000 && !seenSafeAtHome; i++)
        {
            var o = SqueezeResolver.Resolve(new Player { Bunt = 70 }, new Player { Baserunning = 1, Speed = 1 },
                Pitcher, 0.0, 0.0, null, null, Field, Fielders, FieldingCoeff, 0.0, C, rng);
            if (!IsBallInPlay(o) || o.RunnerOut || o.Runs != 1 || o.Bunt == BuntResult.SacrificeSuccess) continue;
            // 本塁へ投げて間に合われた形: 打者は一塁で生きる（安打か野選）。
            Assert.False(o.BatterOut);
            Assert.True(o.Bunt is BuntResult.InfieldHit or BuntResult.FieldersChoice);
            seenSafeAtHome = true;
        }
        Assert.True(seenSafeAtHome);
    }

    [Fact]
    public void AtBatSession_HomeThrowOut_IsNotCreditedAsAHit_AndInfieldHitNeverHasARunnerOut()
    {
        var batter = new BatterAttributes { Contact = 55 };
        var bunter = new Player { Bunt = 70, Speed = 50 };
        var slow = new Player { Baserunning = 1, Speed = 1 };
        var sawHomeOut = false;
        for (ulong seed = 1; seed <= 6000; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var s = AtBatSession.Begin(batter, Pitcher, new AtBatContext { Fielders = Field.StandardAlignment() },
                batterPlayer: bunter, thirdBaseRunner: slow);
            s.ThrowNextPitch(rng, PitchBattingOverride.Squeeze);
            if (!s.IsComplete || s.Result.Squeeze is not { } sq) continue;
            if (sq.Bunt == BuntResult.InfieldHit) Assert.False(sq.RunnerOut);
            if (!sq.RunnerOut) continue;
            Assert.Equal(PlateAppearanceResult.InPlayOut, s.Result.Result);
            sawHomeOut = true;
        }
        Assert.True(sawHomeOut);
    }

    [Fact]
    public void FieldersChoice_PutsTheBatterOnFirst_WithoutAnOut()
    {
        var r1 = new Player { Name = "r1" };
        var b = new Player { Name = "b" };
        var bases = new BaseState { First = r1 };
        var (scorer, _) = BaserunningModel.ApplyBunt(bases, b, BuntResult.FieldersChoice);
        Assert.Null(scorer);
        Assert.Same(b, bases.First);
        Assert.Same(r1, bases.Second);
    }
}
