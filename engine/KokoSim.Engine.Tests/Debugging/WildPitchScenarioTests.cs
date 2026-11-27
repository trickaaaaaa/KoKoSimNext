using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 暴投の絵の検証用シナリオ（issue #1769・data/debug/scenarios.yaml の wild-pitch-second/first-lab）が、
/// 強制発動（WildPitch）で最初の打席のうちに暴投の再生（BatteryMissPlay）を作ること（撮影ハーネスが頼る）。
/// </summary>
public sealed class WildPitchScenarioTests
{
    [Theory]
    [InlineData("wild-pitch-second-lab")]
    [InlineData("wild-pitch-first-lab")]
    public void ProducesABatteryMissPlayInTheFirstPlateAppearance(string id)
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet(id, out var def), id + " が無い");
        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(ForcedOutcomes.TryParse(def.Force, out var force));
        prog.ForceNext(force);

        AdvancePitchResult r;
        do { r = prog.AdvancePitch(); } while (r == AdvancePitchResult.Pitch && prog.LastPitch?.BatteryMissPlay is null);

        Assert.NotNull(prog.LastPitch?.BatteryMissPlay);
    }
}
