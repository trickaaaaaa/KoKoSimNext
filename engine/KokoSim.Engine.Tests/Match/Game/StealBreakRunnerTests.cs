using System.Collections.Generic;
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
/// 三盗（二塁走者）・重盗で本塁へ走った三塁走者が、盗塁の球を打球にされたとき（issue #626 で盗塁は取り消される）、
/// 投球と同時に走っていた分が走塁に入る（issue #1138）。一塁走者の二盗（r1StealBreak）と同じ考え方:
/// ゴロなら打球を読んでから走る遅れが無く、空中で捕られれば元の塁へ戻れるかの飛び出しの勝負になる。
/// </summary>
public sealed class StealBreakRunnerTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // 一塁手がベースを踏んで打者をアウトにしたあと三塁へ転送する形（一塁送球到達 2.0s）の二塁ゴロ。
    private static HomePlayContext FirstBaseGrounder(double throwArriveSeconds = 2.0)
        => new(Field,
            new HomePlaySituation(new Vector3D(9, 0, 40), 1.4, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false, ThrowArriveSeconds: throwArriveSeconds);

    // 接戦帯のライナー捕球（処理点15m・肩30・滞空0.5s）。
    private static HomePlayContext LinerCatch()
        => new(Field,
            new HomePlaySituation(new Vector3D(0, 0, 15), 0.5, new Player { ArmStrength = 30 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: true);

    private static (int Reached, int Out, int Held) SecondRunnerOnGrounder(
        HomePlayContext home, bool stealBreak, int trials, ulong seed, int speed = 90)
    {
        var rng = new Xoshiro256Random(seed);
        int reached = 0, outs = 0, held = 0;
        for (var i = 0; i < trials; i++)
        {
            var r2 = new Player { Speed = speed, Baserunning = speed, Steal = speed };
            var bases = new BaseState { Second = r2 };
            var (_, extraOuts, _, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, home,
                r2StealBreak: stealBreak);
            if (bases.Third == r2) reached++;
            else if (bases.Second == r2) held++;
            else if (extraOuts > 0) outs++;
        }
        return (reached, outs, held);
    }

    [Fact]
    public void ThirdSteal_GroundBall_RunnerReachesThirdMoreOftenThanNormalStart()
    {
        // 再現（issue #1138）: 1アウト二塁・足の速い二塁走者・打者は一塁手へのゴロ。通常始動（打球を見てから走る）は
        // 三塁での走塁死や自重が混ざるが、投球と同時に走っていた走者は一塁を踏んでからの三塁転送に勝つ。
        var home = FirstBaseGrounder();
        const int trials = 4000;
        var normal = SecondRunnerOnGrounder(home, stealBreak: false, trials, seed: 7);
        var broke = SecondRunnerOnGrounder(home, stealBreak: true, trials, seed: 7);

        Assert.True(broke.Reached > normal.Reached, $"到達: 盗塁始動={broke.Reached} 通常={normal.Reached}");
        Assert.True(broke.Out * 2 < normal.Out, $"三塁での走塁死: 盗塁始動={broke.Out} 通常={normal.Out}");
        Assert.True(broke.Reached > trials * 0.8,
            $"スタートを切っていた走者はふつう三塁でセーフ（到達={broke.Reached} 走塁死={broke.Out} 自重={broke.Held} /{trials}; 通常 到達={normal.Reached} 走塁死={normal.Out} 自重={normal.Held}）");
    }

    [Fact]
    public void ThirdSteal_GroundBall_UsesStealBreakStartDelay_NotGrounderReadDelay()
    {
        // 走者所要の起点が係数どおり: 盗塁始動の到達確率は読む遅れ分だけ通常より高い（純関数で固定）。
        var home = FirstBaseGrounder();
        var runner = new Player { Speed = 70, Baserunning = 70 };
        var advance = HomePlayResolver.AdvanceParams(Field, C);
        var relay = new HomePlaySituation(
            Field.FirstBase, home.ThrowArriveSeconds!.Value, C.RelayThrowSpeedMps, home.Situation.OutfielderFielding);
        double P(double delay) => HomePlayResolver.SuccessProbability(
            runner, 2, relay, Field, C, advance, delay, transferSecondsOverride: C.AdvanceRelayTransferSeconds);
        Assert.True(P(C.StealBreakGrounderStartDelaySeconds) > P(C.GrounderAdvanceStartDelaySeconds));
        Assert.True(C.StealBreakGrounderStartDelaySeconds < C.GrounderAdvanceStartDelaySeconds);
    }

    [Fact]
    public void ThirdSteal_GroundBall_MoveCarriesNoReadDelay()
    {
        // 絵（走者レッグ）も判定と同じ起点: 追加スタート遅延が打球を読む分（0.90s）でなく盗塁始動の値。
        var home = FirstBaseGrounder(throwArriveSeconds: 3.0);
        var rng = new Xoshiro256Random(3);
        var r2 = new Player { Speed = 60, Baserunning = 60 };
        var bases = new BaseState { Second = r2 };
        var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, home, r2StealBreak: true);
        var mv = Assert.Single(moves, m => m is { FromBase: 2, ToBase: 3 });
        Assert.Equal(C.StealBreakGrounderStartDelaySeconds, mv.ExtraStartDelaySeconds, 9);
    }

    private static (int DoubledOff, int ReturnedSafe) LinerWithSecondRunner(bool stealBreak, int trials = 3000, ulong seed = 5)
    {
        var rng = new Xoshiro256Random(seed);
        int doubledOff = 0, safe = 0;
        for (var i = 0; i < trials; i++)
        {
            var r2 = new Player { Speed = 50 };
            var bases = new BaseState { Second = r2 };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, LinerCatch(),
                r2StealBreak: stealBreak);
            doubledOff += moves.Count(m => m is { FromBase: 2, ToBase: 2, Out: true });
            safe += moves.Count(m => m is { FromBase: 2, ToBase: 2, Out: false });
        }
        return (doubledOff, safe);
    }

    [Fact]
    public void ThirdSteal_CaughtLiner_RunnerCanBeDoubledOffAtSecond()
    {
        var (doubledOff, safe) = LinerWithSecondRunner(stealBreak: true);
        Assert.True(doubledOff > 0, "三盗のスタートを切った二塁走者がライナー併殺にならない");
        Assert.True(safe > 0, "二塁へ戻れる場合が無い（勝負になっていない）");
    }

    [Fact]
    public void ThirdSteal_CaughtLiner_NormalStartRunnerIsNeverDoubledOff()
    {
        var (doubledOff, safe) = LinerWithSecondRunner(stealBreak: false);
        Assert.Equal(0, doubledOff);
        Assert.Equal(0, safe);
    }

    [Fact]
    public void ThirdSteal_CaughtLiner_DoubledOffRemovesRunnerAndAddsOut()
    {
        var rng = new Xoshiro256Random(11);
        for (var i = 0; i < 2000; i++)
        {
            var r2 = new Player { Speed = 50 };
            var bases = new BaseState { Second = r2 };
            var (_, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, LinerCatch(),
                r2StealBreak: true);
            var outMove = moves.Any(m => m is { FromBase: 2, ToBase: 2, Out: true });
            Assert.Equal(outMove, extraOuts == 1);
            Assert.Equal(outMove, bases.Second is null);
            // 帰塁セーフの走者は二塁に残り、タッチアップで三塁へは進まない（戻るのが捕球の後）。
            if (!outMove) { Assert.Same(r2, bases.Second); Assert.Null(bases.Third); }
        }
    }

    [Fact]
    public void DoubleSteal_ThirdRunnerBreak_CaughtLiner_CanBeDoubledOffAtThird()
    {
        var rng = new Xoshiro256Random(21);
        int doubledOff = 0;
        for (var i = 0; i < 3000; i++)
        {
            var r3 = new Player { Speed = 50 };
            var bases = new BaseState { Third = r3 };
            var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: true, LinerCatch(),
                r3StealBreak: true);
            if (moves.Any(m => m is { FromBase: 3, ToBase: 3, Out: true }))
            {
                doubledOff++;
                Assert.Equal(1, extraOuts);
                Assert.Null(bases.Third);
            }
            Assert.Equal(0, runs); // 帰塁した走者は捕球でのタッチアップ生還をしない
        }
        Assert.True(doubledOff > 0, "本塁突入のスタートを切った三塁走者がライナー併殺にならない");
    }

    [Fact]
    public void DoubleSteal_ThirdRunnerBreak_InfieldIn_GrounderRaceUsesNoReadDelay()
    {
        // 内野前進のゴロ: 本塁へ走っていた三塁走者は読む遅れが無く、通常始動より生還しやすい。
        var home = new HomePlayContext(
            Field,
            new HomePlaySituation(new Vector3D(8, 0, 33), 1.5, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 1.0, DefenseDepth.In, IsFly: false, ThrowArriveSeconds: 1.9);

        int Scores(bool stealBreak)
        {
            var rng = new Xoshiro256Random(9);
            var total = 0;
            for (var i = 0; i < 3000; i++)
            {
                var r3 = new Player { Speed = 70, Baserunning = 70 };
                var bases = new BaseState { Third = r3 };
                var (runs, _, _, _, _, _) = BaserunningModel.ApplyDetailed(
                    bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, home,
                    r3StealBreak: stealBreak);
                total += runs;
            }
            return total;
        }

        Assert.True(Scores(true) > Scores(false));
    }
}
