using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 盗塁の判定テロップ・塁審の撮影シナリオ（issue #1790・data/debug/scenarios.yaml の steal-*-lab）が、
/// 強制発動で最初の打席の初球に盗塁の再生（StealPlay）を作り、結果どおりの判定（セーフ／アウト）を積むこと。
/// </summary>
public sealed class StealScenarioTests
{
    private const ulong Seed = 1UL;

    [Theory]
    [InlineData("steal-safe-lab", JudgementKind.Safe)]
    [InlineData("steal-third-safe-lab", JudgementKind.Safe)]
    [InlineData("steal-caught-lab", JudgementKind.Out)]
    public void ProducesAStealPlayWithTheForcedJudgement(string id, JudgementKind expected)
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet(id, out var def), id + " が無い");
        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, Seed);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(ForcedOutcomes.TryParse(def.Force, out var force));
        prog.ForceNext(force);

        AdvancePitchResult r;
        do { r = prog.AdvancePitch(); } while (r == AdvancePitchResult.Pitch && prog.LastPitch?.StealPlay is null);

        var play = prog.LastPitch?.StealPlay;
        Assert.NotNull(play);
        Assert.Equal(expected, Assert.Single(play!.Judgements).Kind);
    }
}
