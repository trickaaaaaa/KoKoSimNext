using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 終局の絵の検証用シナリオ（issue #647・data/debug/scenarios.yaml の game-end-*）が、
/// <b>最初の1打席のうちに宣言どおりの決着で試合を終わらせる</b>こと。
/// 試合画面の撮影ハーネスはこの3本でゲームセットの瞬間を作る＝ここが崩れると「出たとこ勝負」に戻る。
/// 強制発動は使わず自然な解決をシードで固定している（打球アウト・サヨナラは3Dの守備プレーを通すため
/// 打球の軌道＝Timeline が要る）。係数変更で結果が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class GameEndScenarioTests
{
    [Theory]
    [InlineData("game-end-strikeout-lab", PlateAppearanceResult.Strikeout, false)]
    [InlineData("game-end-groundout-lab", PlateAppearanceResult.InPlayOut, true)]
    [InlineData("game-end-walkoff-lab", PlateAppearanceResult.Single, true)]
    public void EndsTheGameOnFirstPlateAppearance(string id, PlateAppearanceResult expected, bool battedBall)
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet(id, out var def), id + " が無い");
        Assert.Null(def.Force);   // 強制発動は打球の軌道を作らない＝3Dの守備プレーを通らない

        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);

        Assert.True(prog.Advance(), "最初の打席が確定しない");
        Assert.Equal(expected, prog.Current!.Result);
        Assert.Equal(battedBall, prog.PlayLog[^1].Timeline != null);
        Assert.False(prog.Advance(), "1打席で終局していない");
        Assert.True(prog.IsFinished);
        // 3本とも後攻の勝ちで終わる（表2本＝リードのまま9回表で終了／walkoff＝9回裏のサヨナラ）。
        Assert.True(prog.HomeScore > prog.AwayScore, "後攻の勝ちで終わっていない");
    }
}
