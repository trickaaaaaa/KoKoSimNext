using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 捕ったアウトの球がグラブの中に残るかを撮る場面のシナリオ（issue #1081・data/debug/scenarios.yaml の
/// foul-fly-out-lab / fair-fly-out-lab）が、<b>最初の1打席でその打球を出す</b>こと。
/// 係数変更で打球が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class FoulFlyOutScenarioTests
{
    [Theory]
    [InlineData("foul-fly-out-lab", "サードファウルフライ")]
    [InlineData("fair-fly-out-lab", "ライトフライ")]
    public void FirstPlateAppearance_IsTheExpectedFlyOut(string id, string expected)
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet(id, out var def), $"{id} が無い");
        Assert.Null(def.Force);   // 強制発動は打球の軌道を作らない

        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(prog.Advance(), "最初の打席が確定しない");

        var tl = prog.PlayLog[^1].Timeline;
        Assert.NotNull(tl);
        Assert.Contains(expected, tl!.Result);
        // 捕球の後に球の区間が無い＝グラブに付けたまま打席が畳まれる形（#1081 の再現条件）
        Assert.Equal(BallSegmentKind.Flight, tl.Ball[^1].Kind);
    }
}
