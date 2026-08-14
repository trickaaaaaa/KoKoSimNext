using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 外野安打の返球先は**アウトも含めた先頭走者の到達塁**（issue #627）。
///
/// <para>従来は打者の塁打数だけで決めていた（単打・二塁打は無条件で二塁）ため、単打で一塁走者が
/// 三塁まで進んでも中堅手は二塁へ返し、三塁の走者が無警戒で到達していた。#89 の物理レースは
/// 三塁での走塁死まで解いているので、憤死の場合は「ボールは二塁へ行ったのに三塁でアウト」に
/// なっていた。</para>
///
/// <para>表示専用の契約テスト（判定・乱数順には触れない）。</para>
/// </summary>
public sealed class OutfieldReturnLeadBaseTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();
    private static readonly FieldingCoefficients Fielding = new();

    /// <summary>中堅へ抜けた単打（外野へ抜けた低い当たり＝返球の対象）。</summary>
    private static FieldingPlay CenterSingle(double landingZ = 62.0) => FieldingPlayFixtures.OutfieldHit(
        FieldPosition.CenterField, landingX: 0, landingZ: landingZ, fieldedAtSeconds: 3.2,
        result: BattedBallResult.Single);

    private static Player Runner(string name) => new() { Name = name, Speed = 55 };

    private static PlayTimeline BaseTimeline(FieldingPlay play) => TimelineBuilder.BuildBattedBall(
        play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

    /// <summary>打席まるごとの合成（GameEngine と同じ順序: 走者レッグ → 返球）。</summary>
    private static PlayTimeline Compose(FieldingPlay play, IReadOnlyList<RunnerMove> moves)
    {
        var tl = TimelineBuilder.AppendRunnerLegs(BaseTimeline(play), moves, Field, Baserunning, Fielding);
        var to = TimelineBuilder.ResolveOutfieldReturnTarget(moves, play.Result);
        var anchor = 0.0;
        foreach (var leg in tl.Runners)
            if (leg.Label == to.AnchorLabel && leg.T1 > anchor) anchor = leg.T1;
        Assert.True(anchor > 0.0, $"アンカーのレッグ({to.AnchorLabel})がタイムラインに無い");
        return TimelineBuilder.AppendOutfieldReturnThrow(
            tl, play, Field, to.BaseName, anchor, Fielding, Baserunning, to.RunnerOut);
    }

    private static bool Near(TimelinePoint p, Vector3D b)
        => Math.Abs(p.X - b.X) < 0.5 && Math.Abs(p.Z - b.Z) < 0.5;

    /// <summary>最後に着く送球区間（中継が入れば2本目）。</summary>
    private static BallSegment FinalThrow(PlayTimeline tl)
    {
        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.True(throws.Count > 0, "返球が描かれていない（ボールが外野で死ぬ）");
        return throws.OrderBy(b => b.T1).Last();
    }

    private static double LegEnd(PlayTimeline tl, string label)
        => tl.Runners.Where(l => l.Label == label).Select(l => l.T1).DefaultIfEmpty(0.0).Max();

    // --- 本題: 単打で一塁走者が三塁へ進んだら三塁へ返球する ---

    [Fact]
    public void Single_RunnerFirstToThird_ReturnThrowTargetsThird()
    {
        var play = CenterSingle();
        var moves = new List<RunnerMove>
        {
            new(Runner("走者"), FromBase: 1, ToBase: 3, Out: false),
            new(Runner("打者"), FromBase: 0, ToBase: 1, Out: false),
        };

        var tl = Compose(play, moves);
        var last = FinalThrow(tl);

        Assert.True(Near(last.To, Field.ThirdBase),
            $"返球先が三塁でない（({last.To.X:F1},{last.To.Z:F1})）＝先頭走者の到達塁を見ていない");
        // アンカーも先頭走者に紐付く＝「三塁へ投げるが打者の一塁到達に合わせて着く」を防ぐ。
        Assert.True(last.T1 >= LegEnd(tl, "走1"), "返球がセーフの走者より先に三塁へ着いている");
        Assert.True(last.T1 > LegEnd(tl, "打") + 1.0, "返球到達が打者走者の一塁到達に張り付いている");
    }

    /// <summary>三塁で憤死したら送球は走者より**先に**着く（判定表示と矛盾しない）。</summary>
    [Fact]
    public void Single_RunnerOutAtThird_ThrowArrivesBeforeRunner()
    {
        var play = CenterSingle();
        var runner = Runner("走者");
        var outMove = new List<RunnerMove>
        {
            new(runner, FromBase: 1, ToBase: 3, Out: true),
            new(Runner("打者"), FromBase: 0, ToBase: 1, Out: false),
        };
        var safeMove = new List<RunnerMove>
        {
            new(runner, FromBase: 1, ToBase: 3, Out: false),
            new(Runner("打者"), FromBase: 0, ToBase: 1, Out: false),
        };

        var outTl = Compose(play, outMove);
        var safeTl = Compose(play, safeMove);
        var runnerArrive = LegEnd(outTl, "走1");

        Assert.True(Near(FinalThrow(outTl).To, Field.ThirdBase), "憤死なのに返球先が三塁でない");
        Assert.True(FinalThrow(outTl).T1 <= runnerArrive + 1e-6,
            $"アウトなのに送球が走者より遅れて着く（送球{FinalThrow(outTl).T1:F2} > 走者{runnerArrive:F2}）");
        Assert.True(FinalThrow(outTl).T1 < FinalThrow(safeTl).T1, "セーフと同じ到達＝憤死の分岐を通っていない");
        // 走者側の判定表示（#260）はそのまま＝絵と判定が三塁で揃う。
        Assert.Contains(outTl.Judgements, j => j.Kind == JudgementKind.Out);
    }

    /// <summary>一塁走者が二塁止まりなら従来どおり二塁（回帰）。</summary>
    [Fact]
    public void Single_RunnerFirstToSecond_StillTargetsSecond()
    {
        var play = CenterSingle();
        var moves = new List<RunnerMove>
        {
            new(Runner("走者"), FromBase: 1, ToBase: 2, Out: false),
            new(Runner("打者"), FromBase: 0, ToBase: 1, Out: false),
        };

        Assert.True(Near(FinalThrow(Compose(play, moves)).To, Field.SecondBase), "返球先が二塁でない");
    }

    /// <summary>一・二塁からの単打で二塁走者が三塁へ＝先頭は後ろの走者ではなく前の走者。</summary>
    [Fact]
    public void Single_TwoRunners_TargetsTheLeadRunnerNotTheTrailer()
    {
        var play = CenterSingle();
        var moves = new List<RunnerMove>
        {
            new(Runner("二塁走者"), FromBase: 2, ToBase: 3, Out: false),
            new(Runner("一塁走者"), FromBase: 1, ToBase: 2, Out: false),
            new(Runner("打者"), FromBase: 0, ToBase: 1, Out: false),
        };

        var to = TimelineBuilder.ResolveOutfieldReturnTarget(moves, play.Result);
        Assert.Equal("third", to.BaseName);
        Assert.Equal("走1", to.AnchorLabel);   // AppendRunnerLegs の採番（塁上の走者を moves 順に数える）
        Assert.True(Near(FinalThrow(Compose(play, moves)).To, Field.ThirdBase), "返球先が三塁でない");
    }

    /// <summary>走者なしは従来どおり打者の到達塁（回帰）。</summary>
    [Theory]
    [InlineData(BattedBallResult.Single, "second")]
    [InlineData(BattedBallResult.Double, "second")]
    [InlineData(BattedBallResult.Triple, "third")]
    public void NoRunners_KeepsBatterBasedTarget(BattedBallResult result, string expected)
    {
        var moves = new List<RunnerMove> { new(Runner("打者"), FromBase: 0, ToBase: 1, Out: false) };
        var to = TimelineBuilder.ResolveOutfieldReturnTarget(moves, result);

        Assert.Equal(expected, to.BaseName);
        Assert.Equal("打", to.AnchorLabel);
        Assert.False(to.RunnerOut);
    }

    /// <summary>本塁到達は AppendBackHomeThrows の持ち場＝ここでは狙わない（既定へ落ちる）。</summary>
    [Fact]
    public void RunnerScoring_IsNotTargeted_HomeIsBackHomeThrowsJob()
    {
        var moves = new List<RunnerMove>
        {
            new(Runner("走者"), FromBase: 2, ToBase: 4, Out: false),
            new(Runner("打者"), FromBase: 0, ToBase: 1, Out: false),
        };

        Assert.Equal("second", TimelineBuilder.ResolveOutfieldReturnTarget(moves, BattedBallResult.Single).BaseName);
    }

    /// <summary>本塁クロスプレーのある打席で二重返球しない（回帰）。</summary>
    [Fact]
    public void BackHomeThrowAlreadyDrawn_DoesNotAddSecondReturn()
    {
        var play = CenterSingle(75.0);
        var withHome = TimelineBuilder.AppendBackHomeThrows(
            BaseTimeline(play), play, Field, homeArriveAnchor: 7.5, runnerOut: true, Baserunning);
        var before = withHome.Ball.Count(b => b.Kind == BallSegmentKind.Throw);

        var after = TimelineBuilder.AppendOutfieldReturnThrow(
            withHome, play, Field, "third", runnerArriveAnchor: 7.5, Fielding, Baserunning, runnerOut: true);

        Assert.Equal(before, after.Ball.Count(b => b.Kind == BallSegmentKind.Throw));
    }
}
