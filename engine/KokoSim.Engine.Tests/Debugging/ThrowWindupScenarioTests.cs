using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 内野ゴロの一塁送球を撮る場面（issue #1095・data/debug/scenarios.yaml の if-grounder-3b-lab）が、
/// <b>最初の1打席でサードゴロを出す</b>こと。係数変更で打球が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class ThrowWindupScenarioTests
{
    [Fact]
    public void FirstPlateAppearance_IsAThirdBaseGrounder()
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet("if-grounder-3b-lab", out var def));
        Assert.Null(def.Force);
        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(prog.Advance(), "最初の打席が確定しない");
        Assert.Contains("サードゴロ", prog.PlayLog[^1].Timeline!.Result);
    }
}
