using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 捕球ミスの失策（拾い直せず確定）で、非フォースの二塁走者が三塁を狙うかを時間勝負で決める（issue #903・
/// design-14 P1-6）。以前は単打と同じ経路で<b>毎回自動で三塁へ</b>進んでいた。走者・守備の秒は既存の
/// <see cref="HomePlayResolver"/>（<see cref="HomePlayResolver.AdvanceParams"/>）、送り判断は
/// <see cref="HomeSendDecision.ShouldSendThird"/>、守備の時計は拾い直し完了時刻（GameEngine が組む Situation）。
/// </summary>
public sealed class CatchErrorSecondToThirdTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Coeff = new();
    private static readonly TacticsCoefficients Tactics = new();

    /// <summary>遊撃手の定位置付近で弾いた球。拾い直し完了時刻 <paramref name="readyAt"/> から三塁へ投げる。</summary>
    private static HomePlaySituation ShortstopRecovery(double readyAt, double armMps = 30.0)
        => new(new Vector3D(-9.0, 0, 30.0), readyAt, armMps);

    private static HomePlayContext Context(HomePlaySituation s, bool unrecoveredCatchError = true)
        => new(Field, s, Tactics, 0.5, DefenseDepth.Normal, IsFly: false, UnrecoveredCatchError: unrecoveredCatchError);

    private static double Prob(Player runner, HomePlaySituation s)
        => HomePlayResolver.SuccessProbability(runner, 2, s, Field, Coeff,
            HomePlayResolver.AdvanceParams(Field, Coeff), Coeff.GrounderAdvanceStartDelaySeconds);

    /// <summary>三塁到達見込みが <paramref name="target"/> 近傍になる拾い直し完了時刻を探す（決定論の走査）。</summary>
    private static double ReadyAtForProbability(Player runner, double target)
    {
        for (var t = 0.0; t < 8.0; t += 0.01)
        {
            if (Prob(runner, ShortstopRecovery(t)) >= target) return t;
        }
        throw new Xunit.Sdk.XunitException($"到達見込み {target} に届く時刻が見つからない");
    }

    private static (int Runs, int ExtraOuts, BaseState Bases, System.Collections.Generic.IReadOnlyList<RunnerMove> Moves) Apply(
        BaseState bases, HomePlayContext home, IRandomSource rng, int outs = 0)
    {
        var (runs, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.ReachedOnError, new Player(), outs, Coeff, rng,
            collectMoves: true, home, errorKind: FieldingErrorKind.Catch);
        return (runs, extraOuts, bases, moves);
    }

    /// <summary>拾い直しが速く三塁が近い＝見込みが送り閾値に届かない → 二塁に留まる。乱数は1つも引かない。</summary>
    [Fact]
    public void QuickRecovery_RunnerHoldsAtSecond_WithoutConsumingRng()
    {
        var runner = new Player();
        var s = ShortstopRecovery(readyAt: 1.0);
        Assert.True(Prob(runner, s) < HomeSendDecision.SendThirdThreshold(0, 0.5, Tactics), "前提: 送り閾値未満");

        var rng = new Xoshiro256Random(903);
        var (runs, outs, bases, moves) = Apply(new BaseState { Second = runner }, Context(s), rng);
        // 比較対象: 勝負を持たない従来経路（失策連鎖の抽選など、勝負以外の消費は同じ）。
        var legacyRng = new Xoshiro256Random(903);
        Apply(new BaseState { Second = new Player() }, Context(s, unrecoveredCatchError: false), legacyRng);

        Assert.Equal(0, runs);
        Assert.Equal(0, outs);
        Assert.Same(runner, bases.Second);
        Assert.Null(bases.Third);
        Assert.DoesNotContain(moves, m => ReferenceEquals(m.Runner, runner));
        // 自重は乱数を消費しない（本塁・三塁レースの流儀）＝従来経路と同じ位置に乱数列が残る。
        Assert.Equal(legacyRng.NextDouble(), rng.NextDouble());
    }

    /// <summary>
    /// 拾い直しに手こずった＝見込みが高い → 狙って三塁に生きるのが大半。ただし本塁までは送らない
    /// （本塁を狙う判断は従来の TryHome のまま）ぎりぎりの遅さで測る。生きた走者はゴロを読む遅れを
    /// 引き受けて走る（判定と同じ秒を絵へ渡す）。
    /// </summary>
    [Fact]
    public void SlowRecovery_RunnerMostlyTakesThird()
    {
        var runner = new Player();
        var sendHome = HomeSendDecision.SendThreshold(0, 0.5, Tactics);
        var readyAt = 0.0;
        for (var t = 0.0; t < 8.0; t += 0.01)
        {
            if (HomePlayResolver.SuccessProbability(runner, 2, ShortstopRecovery(t), Field, Coeff) >= sendHome) break;
            readyAt = t;
        }
        var s = ShortstopRecovery(readyAt);
        Assert.True(Prob(runner, s) > 0.75, $"前提: 三塁は高い見込み（{Prob(runner, s):F2}）");

        var rng = new Xoshiro256Random(1);
        var safe = 0;
        for (var i = 0; i < 500; i++)
        {
            var (_, outs, bases, moves) = Apply(new BaseState { Second = runner }, Context(s), rng);
            Assert.Null(bases.Second); // 送った以上、二塁には残らない
            if (outs != 0) continue;
            safe++;
            Assert.Same(runner, bases.Third);
            var m = Assert.Single(moves, x => ReferenceEquals(x.Runner, runner));
            Assert.Equal((2, 3, false), (m.FromBase, m.ToBase, m.Out));
            Assert.Equal(Coeff.GrounderAdvanceStartDelaySeconds, m.ExtraStartDelaySeconds, 9);
        }
        Assert.True(safe > 350, $"三塁に生きたのが {safe}/500");
    }

    /// <summary>
    /// 際どい場面では「狙って生きる」も「狙って刺される」も両方起きる（時間勝負）。刺されたら三塁憤死として
    /// 追加アウト・三塁憤死の内訳に計上され、走者は塁上に残らない。
    /// </summary>
    [Fact]
    public void CloseRace_ProducesBothSafeAndOutAtThird()
    {
        var runner = new Player();
        var readyAt = ReadyAtForProbability(runner, 0.62);
        var home = Context(ShortstopRecovery(readyAt));
        var rng = new Xoshiro256Random(20261114);

        int safe = 0, outAtThird = 0;
        for (var i = 0; i < 1000; i++)
        {
            var bases = new BaseState { Second = runner };
            var (_, extraOuts, baseOuts, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.ReachedOnError, new Player(), 0, Coeff, rng,
                collectMoves: true, home, errorKind: FieldingErrorKind.Catch);
            if (extraOuts == 1)
            {
                outAtThird++;
                Assert.Equal(1, baseOuts.Third);
                Assert.Null(bases.Second);
                Assert.Null(bases.Third);
                Assert.Contains(moves, m => ReferenceEquals(m.Runner, runner) && m.ToBase == 3 && m.Out);
            }
            else
            {
                safe++;
                Assert.Same(runner, bases.Third);
            }
        }
        Assert.InRange(outAtThird, 200, 550); // 見込み0.62前後（送り閾値すれすれ）＝4割前後が刺される
        Assert.True(safe > 0);
    }

    /// <summary>フォース（一塁走者あり）の二塁走者は従来どおり自重できず三塁へ進む（勝負しない）。</summary>
    [Fact]
    public void ForcedSecondRunner_StillAdvancesToThird()
    {
        var r1 = new Player();
        var r2 = new Player();
        var s = ShortstopRecovery(readyAt: 1.0); // 自由なら自重する速さ
        var (_, outs, bases, _) = Apply(new BaseState { First = r1, Second = r2 }, Context(s), new Xoshiro256Random(2));

        Assert.Equal(0, outs);
        Assert.Same(r2, bases.Third);
        Assert.Same(r1, bases.Second);
    }

    /// <summary>捕球ミス以外（拾い直しの時計が無い失策）は従来どおり二塁走者が三塁へ進む。</summary>
    [Fact]
    public void WithoutUnrecoveredCatchError_KeepsLegacyAutoAdvance()
    {
        var runner = new Player();
        var s = ShortstopRecovery(readyAt: 1.0);
        var (_, outs, bases, _) = Apply(new BaseState { Second = runner }, Context(s, unrecoveredCatchError: false),
            new Xoshiro256Random(3));

        Assert.Equal(0, outs);
        Assert.Same(runner, bases.Third);
    }

    /// <summary>決定論: 同じシード・同じ場面は同じ結末になる。</summary>
    [Fact]
    public void SameSeed_SameOutcome()
    {
        var runner = new Player();
        var home = Context(ShortstopRecovery(ReadyAtForProbability(runner, 0.70)));
        string Run(ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 200; i++)
            {
                var (_, outs, bases, _) = Apply(new BaseState { Second = runner }, home, rng);
                sb.Append(outs).Append(bases.Third is null ? '-' : '3');
            }
            return sb.ToString();
        }
        Assert.Equal(Run(42), Run(42));
    }

    /// <summary>
    /// タイムライン: 三塁で刺された二塁走者には、拾い直した野手から三塁への送球が描かれ、球は走者より先に着く
    /// （GameEngine と同じ組み立て: 走者レッグ→刺した塁を <see cref="TimelineBuilder.ResolveOutfieldReturnTarget"/> で
    /// 引いて <see cref="TimelineBuilder.AppendErrorReturnThrow"/>）。
    /// </summary>
    [Fact]
    public void Timeline_OutAtThird_DrawsThrowToThirdArrivingBeforeRunner()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var runner = new Player();
        var moves = new[]
        {
            new RunnerMove(runner, 2, 3, Out: true) { ExtraStartDelaySeconds = Coeff.GrounderAdvanceStartDelaySeconds },
            new RunnerMove(new Player(), 0, 1, false),
        };
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        tl = TimelineBuilder.AppendRunnerLegs(tl, moves, Field, Coeff, new FieldingCoefficients());

        var target = TimelineBuilder.ResolveOutfieldReturnTarget(moves, play.Result);
        Assert.True(target.RunnerOut);
        Assert.Equal("third", target.BaseName);
        var runnerArrive = tl.Runners.Where(l => l.Label == target.AnchorLabel).Max(l => l.T1);

        var before = tl.Ball.Count;
        tl = TimelineBuilder.AppendErrorReturnThrow(tl, play, Field, target.BaseName, runnerArrive);
        var toThird = Assert.Single(tl.Ball.Skip(before));
        Assert.Equal("third", toThird.ToBase);
        Assert.True(toThird.T1 > toThird.T0, "送球の飛翔が0秒（瞬間移動）");
        tl = TimelineBuilder.AlignTaggedRunnerToThrow(tl, target.BaseName, target.AnchorLabel);
        var taggedAt = tl.Runners.Where(l => l.Label == target.AnchorLabel).Max(l => l.T1);
        Assert.True(toThird.T1 <= taggedAt + 1e-9, $"球({toThird.T1:F2}s)が刺された走者({taggedAt:F2}s)より後に着いている");
    }
}
