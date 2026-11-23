using System;
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
/// フライの読み（issue #1130・設計書12 §4.10）。走者は打球が落ちるか捕られるかを打った瞬間には知らない。
/// 2アウト未満のフライでは、際どさ（捕球の余裕）を見て打った瞬間に走るか待つかを決める。
/// </summary>
public sealed class FlyReadModelTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    private static Player Runner(int baserunning = 50, int speed = 50)
        => new() { Speed = speed - AbilityScale.Delta, Baserunning = baserunning - AbilityScale.Delta };   // 引数は今の物差し（issue #1080）

    // ---- 読みの単調性 ----

    [Fact]
    public void GoProbability_FallsAsTheBallBecomesCatchable()
    {
        var r = Runner();
        var margins = new[] { -2.0, -1.0, -0.5, 0.0, 0.5, 1.0, 2.0 };
        var ps = margins.Select(m => FlyReadModel.GoProbability(m, r, C)).ToList();
        for (var i = 1; i < ps.Count; i++) Assert.True(ps[i] < ps[i - 1], $"捕球の余裕 {margins[i]} で走る確率が下がっていない");
        Assert.True(ps[0] > 0.98, "明らかに落ちる打球では打った瞬間に走る");
        Assert.True(ps[^1] < 0.02, "明らかに捕れる打球では待つ");
    }

    [Fact]
    public void BetterBaserunner_ReadsMoreSharply()
    {
        var poor = Runner(baserunning: 20);
        var good = Runner(baserunning: 80);
        // 落ちる打球では走る確率が高く、捕れる打球では低い＝読み違いが少ない（効きは ability_effect 経由）
        Assert.True(FlyReadModel.GoProbability(-0.4, good, C) > FlyReadModel.GoProbability(-0.4, poor, C));
        Assert.True(FlyReadModel.GoProbability(0.4, good, C) < FlyReadModel.GoProbability(0.4, poor, C));
    }

    [Fact]
    public void TwoOuts_AlwaysGoesOnContact_WithoutDrawing()
    {
        var rng = new ThrowingRandom();
        Assert.True(FlyReadModel.ReadGo(catchMarginSeconds: 3.0, Runner(), outsAtContact: 2, C, rng));
    }

    [Fact]
    public void ClearBalls_DoNotDraw()
    {
        var rng = new ThrowingRandom();
        Assert.True(FlyReadModel.ReadGo(-3.0, Runner(), 0, C, rng));
        Assert.False(FlyReadModel.ReadGo(3.0, Runner(), 0, C, rng));
    }

    // ---- 物理の突き合わせ: 待つ位置から残りの塁間を走る ----

    [Theory]
    [InlineData(9.0, 3.5)]
    [InlineData(0.0, 4.0)]   // 塁に付いて待った（タッチアップの構え）＝リードから塁へ戻って待つ
    [InlineData(12.0, 2.8)]
    public void HoldDelay_MakesArrivalEqualResumePlusRemainingRun(double holdLeadM, double resumeAt)
    {
        var r = Runner(speed: 60);
        var delay = FlyReadModel.HoldExtraDelaySeconds(r, holdLeadM, resumeAt, C);
        var ctx = HomePlayResolver.RunnerContextOf(r, C);
        var v = r.ToFielder(C.AbilityEffect).SprintSpeedMps * ctx.TopSpeedFactor;
        var expected = resumeAt + (Field.BaseDistanceM - holdLeadM) / v;
        var arrival = HomePlayResolver.RunnerTimeSeconds(r, 1, 2, Field, C, delay);
        Assert.Equal(expected, arrival, 6);
    }

    [Fact]
    public void HoldDelay_IsZero_WhenTheBallLandsBeforeTheRunnerReachesHalfway()
        => Assert.Equal(0.0, FlyReadModel.HoldExtraDelaySeconds(Runner(), 9.0, resumeAtSeconds: 0.5, C));

    // ---- 走塁への配線 ----

    /// <summary>右翼前に落ちる際どいフライ（ポテン）。落下点＝回収点のすぐ先。</summary>
    private static HomePlayContext BlooperToRight(double catchMargin = -0.2)
    {
        var landing = new Vector3D(26, 0, 55);
        var situation = new HomePlaySituation(new Vector3D(27, 0, 57), 3.6, new Player { ArmStrength = 60 }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: true,
            FlyRead: new FlyReadSituation(landing, 3.2, catchMargin));
    }

    [Fact]
    public void WaitingRunnerOnSecond_StartsLateOnTheHit()
    {
        // 1球目 0.99＝読みは「待つ」。以降の勝負は既定の乱数。
        var bases = new BaseState { Second = Runner() };
        var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.Single, Runner(), 0, C, new SequenceRandom(0.99), collectMoves: true, BlooperToRight());
        var m = moves.Single(x => x.FromBase == 2);
        // 待った（右翼前の浅い打球は戻し送球が短い＝ハーフウェーまで出られず塁に付いて待つ。位置は 0〜上限）
        Assert.NotNull(m.FlyHoldLeadM);
        Assert.InRange(m.FlyHoldLeadM!.Value, 0.0, C.FlyHalfwayLeadM);
        Assert.True(m.ExtraStartDelaySeconds > 0.5, "ハーフウェーで待った二塁走者が打った瞬間に走っている");
    }

    [Fact]
    public void RunnerWhoReadsHit_HasNoDelay()
    {
        var bases = new BaseState { Second = Runner() };
        var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.Single, Runner(), 0, C, new SequenceRandom(0.0), collectMoves: true, BlooperToRight());
        var m = moves.Single(x => x.FromBase == 2);
        Assert.Null(m.FlyHoldLeadM);
        Assert.Equal(0.0, m.ExtraStartDelaySeconds);
    }

    [Fact]
    public void HalfwayRunnerOnFirst_CanBeForcedAtSecond_AndTheBatterGetsNoHit()
    {
        // 読み＝待つ(0.99)・外野手の二塁送球の勝負＝刺す(0.999)。
        var batter = Runner();
        var bases = new BaseState { First = Runner() };
        var (runs, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.Single, batter, 0, C, new SequenceRandom(0.99, 0.999), collectMoves: true, BlooperToRight());
        Assert.Equal(0, runs);
        Assert.Equal(1, extraOuts);
        Assert.True(batterSafeOnFc, "外野ゴロは打者の安打にしない（野選）");
        Assert.Contains(moves, x => x is { FromBase: 1, ToBase: 2, Out: true, Force: true });
        Assert.Same(batter, bases.First);
        Assert.Null(bases.Second);
    }

    [Fact]
    public void RunnerOnFirstWhoReadsHit_IsNotForcedAtSecond()
    {
        var bases = new BaseState { First = Runner() };
        var (_, extraOuts, _, batterSafeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.Single, Runner(), 0, C, new SequenceRandom(0.0, 0.999), collectMoves: true, BlooperToRight());
        Assert.False(batterSafeOnFc);
        Assert.DoesNotContain(moves, x => x is { FromBase: 1, ToBase: 2, Out: true });
        Assert.Equal(0, extraOuts);
    }

    [Fact]
    public void TwoOuts_FlyReadDoesNotChangeTheResultOrTheDraws()
    {
        PlateAppearanceOutcome Run(HomePlayContext h)
        {
            var rng = new CountingRandom(new Xoshiro256Random(7));
            var bases = new BaseState { First = Runner(), Second = Runner() };
            var (runs, outs, _, fc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.Single, Runner(), 2, C, rng, collectMoves: true, h);
            return new PlateAppearanceOutcome(runs, outs, fc, moves.Select(m => (m.FromBase, m.ToBase, m.Out)).ToList(), rng.DoubleCalls);
        }
        var with = Run(BlooperToRight());
        var without = Run(BlooperToRight() with { FlyRead = null });
        Assert.Equal(without.Runs, with.Runs);
        Assert.Equal(without.Outs, with.Outs);
        Assert.Equal(without.Moves, with.Moves);
        Assert.Equal(without.Draws, with.Draws);
    }

    private sealed record PlateAppearanceOutcome(int Runs, int Outs, bool Fc, List<(int, int, bool)> Moves, int Draws);

    // ---- 捕られたフライ: 読み違いは飛び出しの勝負・3アウトを超えない ----

    /// <summary>ライナー（捕球 <paramref name="catchAt"/> 秒・捕球点 <paramref name="at"/>）。</summary>
    private static HomePlayContext Liner(Vector3D at, double catchAt, double catchMargin)
    {
        var situation = new HomePlaySituation(at, catchAt, new Player { ArmStrength = 60 }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: true,
            FlyRead: new FlyReadSituation(at, catchAt, catchMargin));
    }

    /// <summary>右中間の浅いところへ飛んで外野手が捕るライナー（捕球 1.4 秒・本塁から約52m）。</summary>
    private static HomePlayContext LinerToShallowOutfield(double catchMargin) => Liner(new Vector3D(15, 0, 50), 1.4, catchMargin);

    [Fact]
    public void RunnerWhoMisreadsACaughtLiner_RacesBackToTheBase()
    {
        var bases = new BaseState { First = Runner() };
        var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, Runner(), 0, C, new SequenceRandom(0.0), collectMoves: true, LinerToShallowOutfield(0.1));
        Assert.Contains(moves, x => x is { FromBase: 1, ToBase: 1 } && x.DoubledOffThrowArriveSeconds is not null);
    }

    [Fact]
    public void InfieldLiner_RunnersFreeze_WithoutReading()
    {
        // 遊撃手の正面のライナー（本塁から約36m）: 走者は塁に付いて動かない＝読みの乱数を引かない（読みなしと同じ消費）。
        int Draws(bool withRead)
        {
            var h = Liner(new Vector3D(-10, 0, 35), 0.6, 0.0);
            var rng = new CountingRandom(new Xoshiro256Random(3));
            var bases = new BaseState { First = Runner(), Second = Runner() };
            var (_, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, Runner(), 0, C, rng, collectMoves: true, withRead ? h : h with { FlyRead = null });
            Assert.Equal(0, extraOuts);
            Assert.DoesNotContain(moves, x => x.DoubledOffThrowArriveSeconds is not null);
            return rng.DoubleCalls;
        }
        Assert.Equal(Draws(false), Draws(true));
    }

    [Fact]
    public void CaughtFly_NeverMakesMoreThanThreeOuts()
    {
        var rng = new Xoshiro256Random(11);
        for (var i = 0; i < 3000; i++)
        {
            var outs = i % 2;
            var bases = new BaseState { First = Runner(20), Second = Runner(20), Third = Runner(20) };
            var (_, extraOuts, _, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, Runner(), outs, C, rng, collectMoves: true, LinerToShallowOutfield(0.25));
            Assert.True(outs + 1 + extraOuts <= 3, $"{outs}アウトからのライナーで {1 + extraOuts} 個のアウト");
            Assert.True(extraOuts <= 1, "1球で2か所の走者を刺している（送球は1本）");
        }
    }

    // ---- 乱数のフェイク ----

    private sealed class ThrowingRandom : IRandomSource
    {
        public double NextDouble() => throw new InvalidOperationException("乱数を引いた");
        public ulong NextUInt64() => throw new InvalidOperationException("乱数を引いた");
        public int NextInt(int minInclusive, int maxExclusive) => throw new InvalidOperationException("乱数を引いた");
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) => throw new InvalidOperationException("乱数を引いた");
        public IRandomSource Fork(ulong streamId) => this;
    }

    private sealed class SequenceRandom(params double[] doubles) : IRandomSource
    {
        private readonly Queue<double> _doubles = new(doubles);
        public double NextDouble() => _doubles.Count > 0 ? _doubles.Dequeue() : 0.5;
        public ulong NextUInt64() => 0UL;
        public int NextInt(int minInclusive, int maxExclusive) => minInclusive;
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) => mean;
        public IRandomSource Fork(ulong streamId) => this;
    }

    private sealed class CountingRandom(IRandomSource inner) : IRandomSource
    {
        public int DoubleCalls { get; private set; }
        public ulong NextUInt64() => inner.NextUInt64();
        public double NextDouble() { DoubleCalls++; return inner.NextDouble(); }
        public int NextInt(int minInclusive, int maxExclusive) => inner.NextInt(minInclusive, maxExclusive);
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) => inner.NextGaussian(mean, stdDev);
        public IRandomSource Fork(ulong streamId) => new CountingRandom(inner.Fork(streamId));
    }
}
