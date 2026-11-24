using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 進行体が場面ジャンプの開始局面を読めること。試合の画面は最初の打席より前に掲示板を掲げるので、
/// これが無いと9回から始めた場面でも「1回表」と出ていた（ストアのスクショ撮影で発覚）。
/// </summary>
public sealed class ScenarioStartPointTests
{
    [Fact]
    public void ScenarioStartPoint_IsTheScenarioStart()
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet("bases-loaded-9th", out var def));
        var built = ScenarioBuilder.Build(def, new GameContext(), 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);

        Assert.NotNull(prog.ScenarioStartPoint);
        Assert.Equal(9, prog.ScenarioStartPoint!.Inning);
        Assert.False(prog.ScenarioStartPoint.IsTop);
    }

    [Fact]
    public void ScenarioStartPoint_IsNullForAnOrdinaryGame()
    {
        var g = new KokoSim.Engine.Core.Xoshiro256Random(1);
        var away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "先攻", g);
        var home = KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "後攻", g);
        var prog = new MatchProgression(away, home, new GameContext(), 1UL);

        Assert.Null(prog.ScenarioStartPoint);
    }
}
