using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// フライの読み（issue #1130）の絵を撮るシナリオ（data/debug/scenarios.yaml の flyread-*）が、最初の打席で
/// 宣言どおりの場面になること。強制発動は使わず自然な解決をシードで固定している（打球の軌道が要る）。
/// 係数変更で結果が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class FlyReadScenarioTests
{
    private static MatchProgression Start(string id)
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet(id, out var def), id + " が無い");
        Assert.Null(def.Force);
        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(prog.Advance(), "最初の打席が確定しない");
        return prog;
    }

    [Fact]
    public void Halfway_RunnerOnFirstWaitsThenRunsOnTheHit()
    {
        var prog = Start("flyread-halfway-lab");
        var e = prog.PlayLog[^1];
        Assert.True(e.Result is PlateAppearanceResult.Single or PlateAppearanceResult.Double, $"安打でない: {e.Result}");
        var r1 = e.Timeline!.Runners.Where(l => l.Label == "走1").ToList();
        Assert.Contains(r1, l => l.From == l.To && l.T1 - l.T0 > 0.5 && l.T0 > 1.2); // ハーフウェーで止まって待つ
        Assert.DoesNotContain(r1, l => l.OutAtEnd);
    }

    [Fact]
    public void OutfieldForce_RunnerOnFirstIsForcedAtSecond_AndTheBatterGetsNoHit()
    {
        var prog = Start("flyread-force-lab");
        var e = prog.PlayLog[^1];
        Assert.Equal(PlateAppearanceResult.InPlayOut, e.Result);
        Assert.True(e.BattedIsFly);
        Assert.EndsWith("ゴロ", e.Timeline!.Result);
        Assert.Contains(e.Timeline.Runners, l => l.Label == "走1" && l.OutAtEnd);
        Assert.True(e.BaseFirstAfter, "打者は野選で一塁に残る");
        Assert.False(e.BaseSecondAfter);
    }
}
