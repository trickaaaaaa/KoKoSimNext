using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 2アウト三塁の外野フライアウトで、捕った外野手が本塁へ送球しない（issue #780）。
/// <para>2アウトのフライでは塁上の走者を「打った瞬間にスタートし捕球で止まる」レッグで描く（#299）。
/// 滞空が長いと三塁走者のレッグが捕球前に本塁へ届くが、3アウトで得点は入っていない。
/// バックホームの相手は**判定で得点が記録された走者**に限る。表示専用（判定・乱数順には触れない）。</para>
/// </summary>
public sealed class TwoOutFlyNoBackHomeTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();

    /// <summary>滞空の長い中堅フライ（三塁走者が捕球前に本塁へ着く長さ）。</summary>
    private static FieldingPlay LongFlyOut() => new()
    {
        Result = BattedBallResult.Out,
        LandingX = 0.0, LandingZ = 95.0, HangTimeSeconds = 6.0, ApexHeightM = 45.0,
        RangeM = 95.0, BearingDeg = 0.0, FielderRole = FieldPosition.CenterField,
        FieldedAtSeconds = 6.0, BatterToFirstSeconds = 4.2, IsFly = true, FielderThrowSpeedMps = 35.0,
    };

    private static PlayTimeline TwoOutRunnerOnThird(FieldingPlay play, IReadOnlyList<RunnerMove> moves, int outsBefore)
    {
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        return TimelineBuilder.AppendHeldRunners(
            tl, first: false, second: false, third: true,
            null, null, new Player { Name = "三塁走者", Speed = 70 },
            moves, Field, outsBefore, play, Baserunning);
    }

    private static bool EndsAtHome(RunnerLeg leg) => Math.Abs(leg.To.X) < 0.5 && Math.Abs(leg.To.Z) < 0.5;

    [Fact]
    public void TwoOuts_LongFlyOut_RunnerLegReachesHome_ButNoBackHomeTarget()
    {
        // 判定: 3アウト目なので進塁なし＝得点の記録は無い（BaserunningModel）
        var tl = TwoOutRunnerOnThird(LongFlyOut(), new List<RunnerMove>(), outsBefore: 2);

        // 前提: 走者レッグは捕球前に本塁へ届いている（これが誤ってバックホームを呼んでいた）
        Assert.Contains(tl.Runners, l => EndsAtHome(l) && !l.OutAtEnd);

        var (outAtHome, safeAtHome) = GameEngine.FindHomeArrivals(tl, new List<RunnerMove>());
        Assert.Null(outAtHome);
        Assert.Null(safeAtHome);
    }

    [Fact]
    public void ScoredRun_StillPicksHomeArrival_ForBackHomeThrow()
    {
        // 外野安打・犠飛で実際に生還したときは従来どおり返球の相手になる（#7）
        var runner = new Player { Name = "三塁走者", Speed = 70 };
        var moves = new List<RunnerMove> { new(runner, FromBase: 3, ToBase: 4, Out: false) };
        var play = LongFlyOut();
        var tl = TimelineBuilder.AppendRunnerLegs(
            TimelineBuilder.BuildBattedBall(play, Field.StandardAlignment(), Field, null, runnersOn: true,
                TimelineBuilder.DescribeResult(play)),
            moves, Field, Baserunning, new FieldingCoefficients());

        var (outAtHome, safeAtHome) = GameEngine.FindHomeArrivals(tl, moves);
        Assert.Null(outAtHome);
        Assert.NotNull(safeAtHome);
    }

    [Fact]
    public void OutAtHome_IsPickedRegardlessOfScoring()
    {
        var runner = new Player { Name = "三塁走者", Speed = 40 };
        var moves = new List<RunnerMove> { new(runner, FromBase: 3, ToBase: 4, Out: true) };
        var play = LongFlyOut();
        var tl = TimelineBuilder.AppendRunnerLegs(
            TimelineBuilder.BuildBattedBall(play, Field.StandardAlignment(), Field, null, runnersOn: true,
                TimelineBuilder.DescribeResult(play)),
            moves, Field, Baserunning, new FieldingCoefficients());

        var (outAtHome, safeAtHome) = GameEngine.FindHomeArrivals(tl, moves);
        Assert.NotNull(outAtHome);
        Assert.Null(safeAtHome);
        Assert.True(tl.Runners.Count(l => l.OutAtEnd) >= 1);
    }

    [Fact]
    public void FullGames_TwoOutsRunnerOnThird_FlyOut_NeverThrowsHome()
    {
        // 実試合の経路（GameEngine の合成順）で固定する: 2アウト三塁の外野フライアウトの打席に
        // 本塁への送球が1本も無いこと。該当打席が十分に出ることも確かめる（空の合格を防ぐ）。
        const int games = 200;
        var hits = 0; var bad = 0;
        Parallel.For(0, games, g =>
        {
            var seed = (ulong)(g + 1);
            var away = StrengthTeamFactory.Create(55, "先攻", new Xoshiro256Random(seed * 2));
            var home = StrengthTeamFactory.Create(55, "後攻", new Xoshiro256Random(seed * 2 + 1));
            var result = GameEngine.Play(away, home, new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in result.Log)
            {
                if (e.OutsBefore != 2 || !e.BaseThirdBefore || !e.BattedIsFly || e.Timeline is null) continue;
                if (e.OutsThisPa < 1 || e.RunsScored > 0) continue;
                // フライアウトだけを見る（安打で三塁走者が本塁で刺された打席は別の話＝本塁送球があって正しい）
                if (e.Result != KokoSim.Engine.Match.AtBat.PlateAppearanceResult.InPlayOut) continue;
                if (e.FieldedBy is not (FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField)) continue;
                Interlocked.Increment(ref hits);
                if (e.Timeline.Ball.Any(b => b.Kind == BallSegmentKind.Throw
                        && Math.Abs(b.To.X) < 0.5 && Math.Abs(b.To.Z) < 0.5))
                    Interlocked.Increment(ref bad);
            }
        });
        Assert.True(hits >= 20, $"該当打席が少なすぎる（{hits}件）");
        Assert.Equal(0, bad);
    }
}
