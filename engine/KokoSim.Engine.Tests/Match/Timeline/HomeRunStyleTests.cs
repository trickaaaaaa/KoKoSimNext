using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>本塁打の当たりの分類（issue #704・表示専用）。</summary>
public class HomeRunStyleTests
{
    [Theory]
    [InlineData(10.0, 28.0, HomeRunStyle.NoDoubt)]
    [InlineData(7.0, 18.0, HomeRunStyle.NoDoubt)]    // 余裕が大きければライナーでも「打った瞬間」が先
    [InlineData(1.0, 18.0, HomeRunStyle.Wall)]       // ぎりぎりはライナーより先
    [InlineData(1.0, 40.0, HomeRunStyle.Wall)]
    [InlineData(4.0, 20.0, HomeRunStyle.Liner)]
    [InlineData(4.0, 36.0, HomeRunStyle.Towering)]
    [InlineData(4.0, 28.0, HomeRunStyle.Standard)]
    [InlineData(1.5, 22.0, HomeRunStyle.Standard)]   // 境界: 余裕1.5は「ぎりぎり」でない・角度22は「ライナー」でない
    public void Classify_FollowsThresholdsAndPriority(double clearanceM, double angleDeg, HomeRunStyle expected)
        => Assert.Equal(expected, HomeRunStyles.Classify(clearanceM, angleDeg));

    /// <summary>実試合のタイムライン: 本塁打だけが種類を持ち、再生モデルまで届く。種類は1つに偏らない。</summary>
    [Fact]
    public void GameTimelines_CarryStyleOnlyOnHomeRuns()
    {
        var styles = new System.Collections.Generic.List<HomeRunStyle>();
        var root = new Xoshiro256Random(704);
        for (var i = 0; i < 40; i++)
        {
            var g = root.Fork((ulong)i);
            var a = StrengthTeamFactory.Create(80.0, "A", g.Fork(1));
            var h = StrengthTeamFactory.Create(80.0, "H", g.Fork(2));
            var p = GameEngine.NewProgress(a, h, new GameContext { CaptureTimelines = true }, g);
            foreach (var _ in GameEngine.Steps(p)) { }
            var result = GameEngine.BuildResult(p);

            foreach (var e in result.Log.Where(e => e.Timeline != null))
            {
                var style = e.Timeline!.HomeRunStyle;
                if (e.Result == PlateAppearanceResult.HomeRun)
                {
                    Assert.NotNull(style);
                    Assert.Equal(style, PlayTimelineAdapter.ToPlaybackPlay(e.Timeline).HomeRun);
                    styles.Add(style!.Value);
                }
                else
                {
                    Assert.Null(style);
                }
            }
        }

        Assert.NotEmpty(styles);
        Assert.True(styles.Distinct().Count() >= 3, "種類: " + string.Join(",", styles.GroupBy(s => s).Select(gr => gr.Key + "=" + gr.Count())));
    }
}
