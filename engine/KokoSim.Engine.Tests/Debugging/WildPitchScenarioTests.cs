using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 暴投の絵の検証用シナリオ（issue #1769・data/debug/scenarios.yaml の wild-pitch-second/first-lab）が、
/// 最初の打席のうちに暴投の再生（BatteryMissPlay）を作ること。強制発動は使わず自然な解決をシードで固定している
/// （暴投は着弾コースがゲートに入る球でだけ起きる）。係数変更で動いたらこのテストが赤くなる→シードを選び直す。
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

        AdvancePitchResult r;
        do { r = prog.AdvancePitch(); } while (r == AdvancePitchResult.Pitch && prog.LastPitch?.BatteryMissPlay is null);

        Assert.NotNull(prog.LastPitch?.BatteryMissPlay);
    }
}
