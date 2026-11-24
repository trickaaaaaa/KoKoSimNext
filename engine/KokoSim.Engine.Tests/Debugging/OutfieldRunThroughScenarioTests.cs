using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 走者ありの外野ゴロを走りながら掬う場面のシナリオ（issue #1079・data/debug/scenarios.yaml の of-grounder-*-lab）が、
/// <b>最初の1打席でその打球を出す</b>こと。係数変更で打球が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class OutfieldRunThroughScenarioTests
{
    [Theory]
    [InlineData("of-grounder-runner-lf-lab", FieldPosition.LeftField, true)]
    [InlineData("of-grounder-runner-cf-lab", FieldPosition.CenterField, true)]
    [InlineData("of-grounder-runner-rf-lab", FieldPosition.RightField, true)]
    [InlineData("of-grounder-norunner-lab", FieldPosition.LeftField, false)]
    public void FirstPlateAppearance_IsAnOutfieldGrounder(string id, FieldPosition fielder, bool runThrough)
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet(id, out var def), $"{id} が無い");
        Assert.Null(def.Force);

        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(prog.Advance(), "最初の打席が確定しない");

        var tl = prog.PlayLog[^1].Timeline;
        Assert.NotNull(tl);
        Assert.Contains("ヒット", tl!.Result);
        Assert.Contains(tl.Moves, m => m.Role == fielder && m.Task == FielderTask.FieldBall);
        // 走者ありは捕りに行く移動が RunIn・捕った後に RunOut で運ぶ。走者なしは従来どおり（どちらも無い）
        Assert.Equal(runThrough, tl.Moves.Any(m => m.Role == fielder && m.Profile == MoveProfile.RunIn));
        Assert.Equal(runThrough, tl.Moves.Any(m => m.Role == fielder && m.Profile == MoveProfile.RunOut));
        if (!runThrough) return;

        // issue #1552: 運びは数歩で終わり、送球のリリースの前に止まっている（送球クリップの間は位置が動かない）
        var carry = tl.Moves.Single(m => m.Role == fielder && m.Profile == MoveProfile.RunOut);
        var th = tl.Ball.First(b => b.Kind == BallSegmentKind.Throw);
        Assert.True(carry.T1 - carry.T0 <= 0.35 + 1e-9, "運びが0.35sを超えている");
        Assert.True(carry.T1 <= th.T0 + 1e-9, "運びがリリースより後まで続いている");
        Assert.Equal(carry.To.X, th.From.X, 6);                 // 送球は運んで止まった点から
        Assert.Equal(carry.To.Z, th.From.Z, 6);
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var stop = PlaybackEvaluator.FielderAt(play, fielder, carry.T1).Pos;
        var rel = PlaybackEvaluator.FielderAt(play, fielder, th.T0).Pos;
        Assert.Equal(stop.X, rel.X, 6);
        Assert.Equal(stop.Y, rel.Y, 6);
    }
}
