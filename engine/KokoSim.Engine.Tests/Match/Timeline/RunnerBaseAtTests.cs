using System;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 再生時刻 t で走者が占めている塁（<see cref="PlaybackEvaluator.RunnerBaseAt"/>・issue #1534 項目8）。
/// タイムのパネルの塁を、小ダイヤモンドと同じ走者・同じ時刻から引くための口。
/// </summary>
public sealed class RunnerBaseAtTests
{
    private static readonly PlaybackVec Home = new(0, 0);
    private static readonly PlaybackVec First = new(19.37, 19.37);
    private static readonly PlaybackVec Second = new(0, 38.75);
    private static readonly PlaybackVec Third = new(-19.37, 19.37);

    private static PlaybackPlay Play(PlaybackRunner r, params PlaybackJudgement[] judgements) => new()
    {
        Name = "test", Dur = 8.0, Result = "test", ResAt = 4.0,
        Ball = Array.Empty<PlaybackBallSegment>(),
        Runners = new[] { r },
        Judgements = judgements,
    };

    [Fact]
    public void BatterRunner_IsOnFirstOnlyAfterReachingIt()
    {
        var r = new PlaybackRunner { Label = "打", Segs = new[] { new PlaybackRun(0.5, 4.0, Home, First) } };
        var play = Play(r);

        Assert.Null(PlaybackEvaluator.RunnerBaseAt(play, r, 0.0));
        Assert.Null(PlaybackEvaluator.RunnerBaseAt(play, r, 3.9));
        Assert.Equal(1, PlaybackEvaluator.RunnerBaseAt(play, r, 4.0));
        Assert.Equal(1, PlaybackEvaluator.RunnerBaseAt(play, r, 7.0));
    }

    [Fact]
    public void Runner_StaysOnTheBaseHeLeftUntilHeReachesTheNext()
    {
        var r = new PlaybackRunner
        {
            Label = "走一",
            Segs = new[] { new PlaybackRun(0.5, 3.5, First, Second), new PlaybackRun(3.5, 6.5, Second, Third) },
        };
        var play = Play(r);

        Assert.Equal(1, PlaybackEvaluator.RunnerBaseAt(play, r, 0.0));
        Assert.Equal(1, PlaybackEvaluator.RunnerBaseAt(play, r, 2.0));
        Assert.Equal(2, PlaybackEvaluator.RunnerBaseAt(play, r, 4.0));
        Assert.Equal(3, PlaybackEvaluator.RunnerBaseAt(play, r, 7.0));
    }

    [Fact]
    public void RunThroughPastFirst_StillCountsAsFirst()
    {
        // 一塁の駆け抜け（終端が塁から外れる）は塁を変えない。
        var past = new PlaybackVec(23.0, 23.0);
        var r = new PlaybackRunner
        {
            Label = "打",
            Segs = new[] { new PlaybackRun(0.5, 4.0, Home, First), new PlaybackRun(4.0, 4.8, First, past) },
        };
        Assert.Equal(1, PlaybackEvaluator.RunnerBaseAt(Play(r), r, 6.0));
    }

    [Fact]
    public void RunnerWhoScored_OccupiesNoBase()
    {
        var r = new PlaybackRunner { Label = "走三", Segs = new[] { new PlaybackRun(0.5, 3.0, Third, Home) } };
        var play = Play(r);

        Assert.Equal(3, PlaybackEvaluator.RunnerBaseAt(play, r, 1.0));
        Assert.Null(PlaybackEvaluator.RunnerBaseAt(play, r, 3.5));
    }

    [Fact]
    public void RetiredRunner_OccupiesNoBase()
    {
        // 盗塁死: 二塁で憤死（HideAt）＝消えた後は塁に居ない。小ダイヤの消灯（IsRunnerOut）と同じ口。
        var r = new PlaybackRunner
        {
            Label = "走一",
            Segs = new[] { new PlaybackRun(0.2, 3.3, First, Second) { OutAtEnd = true } },
            HideAt = 3.3,
        };
        var play = Play(r);

        Assert.Equal(1, PlaybackEvaluator.RunnerBaseAt(play, r, 2.0));
        Assert.Null(PlaybackEvaluator.RunnerBaseAt(play, r, 3.4));
    }

    [Fact]
    public void FlyOutBatter_OccupiesNoBaseAfterTheCatch()
    {
        var r = new PlaybackRunner { Label = "打", Segs = new[] { new PlaybackRun(0.5, 4.0, Home, First) } };
        var play = Play(r, new PlaybackJudgement(3.0, JudgementKind.Out, false, "打"));

        Assert.Null(PlaybackEvaluator.RunnerBaseAt(play, r, 5.0));
    }
}
