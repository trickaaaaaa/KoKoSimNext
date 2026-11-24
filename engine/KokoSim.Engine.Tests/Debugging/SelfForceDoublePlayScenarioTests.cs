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
/// 6-6-3 / 4-4-3（issue #1549）の絵を撮るシナリオ（data/debug/scenarios.yaml の dp-663-lab / dp-443-lab）が、最初の打席で
/// 宣言どおりの場面になること。強制発動は使わず自然な解決をシードで固定している（打球の軌道が要る）。
/// 係数変更で結果が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class SelfForceDoublePlayScenarioTests
{
    [Theory]
    [InlineData("dp-663-lab", FieldPosition.Shortstop)]
    [InlineData("dp-443-lab", FieldPosition.SecondBase)]
    public void FielderStepsOnSecondHimself_ThenThrowsToFirst_ForTwoOuts(string id, FieldPosition fielder)
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet(id, out var def), id + " が無い");
        Assert.Null(def.Force);
        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(prog.Advance(), "最初の打席が確定しない");

        var tl = prog.PlayLog[^1].Timeline;
        Assert.NotNull(tl);
        var carry = Assert.Single(tl!.Ball, b => b.Kind == BallSegmentKind.Carry && b.ToBase == "second");
        Assert.Equal(fielder, carry.FromRole);
        Assert.Equal(fielder, carry.ToRole);       // 二塁カバーは受けない＝処理した本人が踏む
        Assert.Contains(tl.Ball, b => b.Kind == BallSegmentKind.Throw && b.FromRole == fielder && b.ToBase == "first");
        Assert.Equal(2, tl.Judgements.Count(j => j.Kind == JudgementKind.Out));
    }
}
