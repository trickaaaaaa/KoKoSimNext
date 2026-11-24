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
/// 一塁走者が投球と同時に走っていた（エンドラン＝Contact／ギャンブル始動／盗塁の巻き戻し）ゴロ併殺は、
/// 打球を読んでから走る遅れ（<see cref="BaserunningCoefficients.DoublePlayR1StartDelaySeconds"/>）が無い
/// （設計書12 §4.7.1・issue #1241）。エンドランの主な利点＝併殺回避が出ること。
/// </summary>
public sealed class DoublePlayStartTypeTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    private static HomePlayContext Grounder(double fieldedAt)
    {
        var situation = new HomePlaySituation(
            new Vector3D(-6, 0, 37), fieldedAt, new Player { ArmStrength = 55 }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(
            Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false,
            ThrowArriveSeconds: null, BatterToFirstSeconds: 5.40);
    }

    private static double PR1Safe(HomePlayContext home, Player r1, double delay)
        => HomePlayResolver.SuccessProbability(
            r1, 1, home.Situation, home.Field, C, HomePlayResolver.SecondForceParams(home.Field, C), delay);

    /// <summary>通常始動なら二塁封殺が間に合い（P_r1_safe &lt; 閾値）、遅れ無しなら間に合わない（&gt; 閾値）境目の処理時刻を探す。</summary>
    private static HomePlayContext BorderlineGrounder()
    {
        var r1 = new Player { Speed = 50, Baserunning = 50 };
        for (var at = 1.2; at <= 3.0; at += 0.01)
        {
            var home = Grounder(at);
            if (PR1Safe(home, r1, C.DoublePlayR1StartDelaySeconds) < C.DoublePlayForceMaxRunnerSuccess
                && PR1Safe(home, r1, C.StealBreakGrounderStartDelaySeconds) > C.DoublePlayForceMaxRunnerSuccess)
                return home;
        }
        throw new Xunit.Sdk.XunitException("境目の処理時刻が見つからない");
    }

    private static int ForceOutsAtSecond(StartType start, bool stealBreak, ulong seed = 41, int trials = 400)
    {
        var rng = new Xoshiro256Random(seed);
        var home = BorderlineGrounder();
        var n = 0;
        for (var i = 0; i < trials; i++)
        {
            var bases = new BaseState { First = new Player { Speed = 50, Baserunning = 50 } };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 1, C, rng, collectMoves: true, home,
                start, r1StealBreak: stealBreak);
            if (moves.Any(m => m is { FromBase: 1, ToBase: 2, Out: true })) n++;
        }
        return n;
    }

    [Fact]
    public void NormalStart_RunnerOnFirst_BorderlineGrounder_CanBeForcedOutAtSecond()
    {
        Assert.True(ForceOutsAtSecond(StartType.Normal, stealBreak: false) > 0);
    }

    [Fact]
    public void HitAndRun_RunnerOnFirst_BorderlineGrounder_IsNeverForcedOutAtSecond()
    {
        Assert.Equal(0, ForceOutsAtSecond(StartType.Contact, stealBreak: false));
    }

    [Fact]
    public void GambleStart_RunnerOnFirst_BorderlineGrounder_IsNeverForcedOutAtSecond()
    {
        Assert.Equal(0, ForceOutsAtSecond(StartType.Gamble, stealBreak: false));
    }

    [Fact]
    public void StealBreak_RunnerOnFirst_BorderlineGrounder_IsNeverForcedOutAtSecond()
    {
        Assert.Equal(0, ForceOutsAtSecond(StartType.Normal, stealBreak: true));
    }
}
