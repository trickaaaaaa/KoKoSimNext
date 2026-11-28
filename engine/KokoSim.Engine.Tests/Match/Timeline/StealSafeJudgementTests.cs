using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>盗塁が成功したときも判定イベント「セーフ」を積む（issue #1790）。盗塁死の「アウト」は従来どおり。</summary>
public sealed class StealSafeJudgementTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();
    private static readonly Player Runner = new() { Speed = 80, Steal = 75 };
    private static readonly Player Catcher = new() { Position = FieldPosition.Catcher, ArmStrength = 60 };

    [Theory]
    [InlineData(StealTarget.Second)]
    [InlineData(StealTarget.Third)]
    public void SafeSteal_HasSafeJudgementAtRunnerArrival(StealTarget target)
    {
        var tl = TimelineBuilder.BuildSteal(Runner, Catcher, StealResult.Safe, C, Field, target);
        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Safe, j.Kind);
        Assert.Equal("走", j.Label);
        Assert.Equal(tl.Runners.Single().T1, j.T, 6);
        Assert.True(j.BaseCall);
    }

    [Fact]
    public void CaughtStealing_StillHasOutJudgement()
    {
        var tl = TimelineBuilder.BuildSteal(Runner, Catcher, StealResult.CaughtStealing, C, Field);
        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Out, j.Kind);
        Assert.False(j.BaseCall);
    }

    [Fact]
    public void DoubleSteal_DoesNotAddJudgementForScorer()
    {
        var scorer = new Player { Speed = 80, Steal = 70 };
        var tl = TimelineBuilder.BuildSteal(Runner, Catcher, StealResult.Safe, C, Field, doubleStealScorer: scorer);
        var j = Assert.Single(tl.Judgements);
        Assert.Equal("走", j.Label);
    }

    [Fact]
    public void CloseCall_OnlyWhenGapBelowMargin()
    {
        var close = TimelineBuilder.BuildSteal(Runner, Catcher, StealResult.Safe, C, Field,
            runnerSeconds: 3.0, defenseSeconds: 3.05);
        var wide = TimelineBuilder.BuildSteal(Runner, Catcher, StealResult.Safe, C, Field,
            runnerSeconds: 3.0, defenseSeconds: 3.5);
        Assert.True(close.Judgements.Single().CloseCall);
        Assert.False(wide.Judgements.Single().CloseCall);
    }

    [Fact]
    public void SafeJudgement_ReachesPlaybackAndIsShownAtArrival()
    {
        var tl = TimelineBuilder.BuildSteal(Runner, Catcher, StealResult.Safe, C, Field);
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var arrive = tl.Runners.Single().T1;
        var shown = PlaybackEvaluator.JudgementAt(play, arrive + 0.1, 0.6);
        Assert.Equal(JudgementKind.Safe, shown!.Value.Kind);
        Assert.True(play.Judgements.Single().BaseCall);
        Assert.True(PlaybackEvaluator.LastJudgementEndTime(play, 0.6)!.Value <= play.Dur);
    }
}
