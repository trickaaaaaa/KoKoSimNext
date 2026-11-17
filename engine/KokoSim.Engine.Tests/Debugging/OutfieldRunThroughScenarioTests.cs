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
    }
}
