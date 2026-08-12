using System;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// アウトになった走者を盤面から消す判定（<see cref="PlaybackEvaluator.IsRunnerOut"/>・issue #595）。
///
/// <para><b>なぜ HideAt だけでは足りないか</b>: <c>HideAt</c> は「塁で決着したアウト」しか持たない。
/// 飛球の捕球アウトは打者走者のレッグに載らない（<c>TimelineBuilder</c> の
/// <c>batterOut = Out &amp;&amp; !IsFly</c>）ので、HideAt だけで消すと<b>フライアウトの打者走者が
/// 一塁に残り続ける</b>＝生きている走者と区別が付かない。</para>
/// </summary>
public sealed class RunnerRetiredTests
{
    private static PlaybackVec V(double x, double y) => new(x, y);

    private static PlaybackRunner Runner(string label, double? hideAt)
        => new()
        {
            Label = label,
            Segs = new[] { new PlaybackRun(0.1, 4.0, V(0, 0), V(19.4, 19.4)) },
            HideAt = hideAt,
        };

    private static PlaybackPlay Play(PlaybackRunner runner, params PlaybackJudgement[] judgements)
        => new()
        {
            Name = "test", Dur = 6.0, Result = "test", ResAt = 4.0,
            Ball = Array.Empty<PlaybackBallSegment>(),
            Runners = new[] { runner },
            Judgements = judgements,
        };

    [Fact]
    public void RunnerWithHideAt_DisappearsAtHideAt()
    {
        var r = Runner("打", hideAt: 4.0);
        var play = Play(r);

        Assert.False(PlaybackEvaluator.IsRunnerOut(play, r, 3.99));
        Assert.False(PlaybackEvaluator.IsRunnerOut(play, r, 4.0));   // 消えるのは「過ぎた」後
        Assert.True(PlaybackEvaluator.IsRunnerOut(play, r, 4.01));
    }

    [Fact]
    public void FlyOutBatter_HasNoHideAt_ButDisappearsAtTheCatch()
    {
        // フライアウト: レッグは OutAtEnd を持たない（＝HideAt なし）。捕球の瞬間は判定イベントにだけ有る。
        var r = Runner("打", hideAt: null);
        var play = Play(r, new PlaybackJudgement(2.5, JudgementKind.Out, false, "打"));

        Assert.False(PlaybackEvaluator.IsRunnerOut(play, r, 2.5));
        Assert.True(PlaybackEvaluator.IsRunnerOut(play, r, 2.6));
    }

    [Fact]
    public void OutJudgementForAnotherRunner_DoesNotRetireThisOne()
    {
        // 併殺のような複数走者のプレーで、他人のアウトに巻き込まれて消えないこと。
        var r = Runner("打", hideAt: null);
        var play = Play(r, new PlaybackJudgement(2.5, JudgementKind.Out, false, "走一"));

        Assert.False(PlaybackEvaluator.IsRunnerOut(play, r, 5.9));
    }

    [Fact]
    public void HideAtWins_OverAnEarlierOutJudgement()
    {
        // 内野ゴロの一塁フォースは送球到達（判定イベント）で確定するが、走者はその後も塁まで走り切る
        // （Issue #328①）。判定イベントで先に消すと**塁に着く手前で走者が消える**ので、
        // HideAt を持つ走者は HideAt だけで判定する＝2D俯瞰と同じ「塁に着いた瞬間に消える」。
        var r = Runner("打", hideAt: 4.0);
        var play = Play(r, new PlaybackJudgement(3.6, JudgementKind.Out, false, "打"));

        Assert.False(PlaybackEvaluator.IsRunnerOut(play, r, 3.9));
        Assert.True(PlaybackEvaluator.IsRunnerOut(play, r, 4.1));
    }

    [Fact]
    public void SafeAndOtherJudgements_NeverRetireARunner()
    {
        var r = Runner("打", hideAt: null);
        var play = Play(r,
            new PlaybackJudgement(2.0, JudgementKind.Safe, true, "打"),
            new PlaybackJudgement(2.2, JudgementKind.Fair, false, "打"),
            new PlaybackJudgement(2.4, JudgementKind.Error, false, "打"));

        Assert.False(PlaybackEvaluator.IsRunnerOut(play, r, 5.9));
    }

    [Fact]
    public void NullRunner_IsNeverOut()
        => Assert.False(PlaybackEvaluator.IsRunnerOut(null!, null!, 1.0));
}
