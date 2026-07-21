using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 投球1球ぶんの再生供給 <see cref="PitchPlaybackFactory"/> の契約テスト。
/// 打球にならない投球でも軌道が描けること・捕手返球は含まれないこと・占有塁の走者が静止表示されることを検証する。
/// </summary>
public sealed class PitchPlaybackFactoryTests
{
    [Fact]
    public void PitchOnly_HasSinglePitchSegment_AndNoCatcherReturnThrow()
    {
        var play = PitchPlaybackFactory.PitchOnly(false, false, false);

        var seg = Assert.Single(play.Ball);
        Assert.IsType<PitchSegment>(seg);
        Assert.Empty(play.Ball.OfType<ThrowSegment>());   // 捕手→マウンドの返球は描画対象外
        Assert.Equal(PitchSegment.DurationSeconds, play.Dur, 6);
    }

    [Fact]
    public void PitchOnly_BallTravelsFromMoundToHome()
    {
        var play = PitchPlaybackFactory.PitchOnly(false, false, false);

        var start = PlaybackEvaluator.BallAt(play, 0);
        var end = PlaybackEvaluator.BallAt(play, PitchSegment.DurationSeconds);
        Assert.True(start.HasValue && end.HasValue);

        // マウンド側（前方・高い）から本塁側（手前・低い）へ単調に近づく
        Assert.True(start!.Value.Y > end!.Value.Y);
        Assert.True(start.Value.H > end.Value.H);
        Assert.True(end.Value.Y < 1.5);
        Assert.Equal(0, end.Value.X, 6);

        var mid = PlaybackEvaluator.BallAt(play, PitchSegment.DurationSeconds / 2);
        Assert.True(mid!.Value.Y < start.Value.Y && mid.Value.Y > end.Value.Y);
    }

    [Fact]
    public void PitchOnly_PlacesStillRunnersOnOccupiedBases()
    {
        var play = PitchPlaybackFactory.PitchOnly(first: true, second: false, third: true);

        Assert.Equal(2, play.Runners.Count);
        foreach (var r in play.Runners)
        {
            var a = PlaybackEvaluator.RunnerAt(r, 0);
            var b = PlaybackEvaluator.RunnerAt(r, PitchSegment.DurationSeconds);
            Assert.True(a.HasValue && b.HasValue);
            Assert.Equal(a!.Value.X, b!.Value.X, 6);   // 投球中に走者は動かない
            Assert.Equal(a.Value.Y, b.Value.Y, 6);
        }

        var xs = play.Runners.Select(r => PlaybackEvaluator.RunnerAt(r, 0)!.Value.X).ToList();
        Assert.Contains(xs, x => System.Math.Abs(x - FieldDiagramGeometry.First.X) < 1e-6);
        Assert.Contains(xs, x => System.Math.Abs(x - FieldDiagramGeometry.Third.X) < 1e-6);
    }

    [Fact]
    public void PitchOnly_KeepsFieldersAtDefaultPositions()
    {
        var play = PitchPlaybackFactory.PitchOnly(false, false, false);

        foreach (var pos in new[] { FieldPosition.Pitcher, FieldPosition.Shortstop, FieldPosition.CenterField })
        {
            var (p, moving) = PlaybackEvaluator.FielderAt(play, pos, PitchSegment.DurationSeconds / 2);
            var d = PlaybackEvaluator.DefaultPositions[pos];
            Assert.Equal(d.X, p.X, 6);
            Assert.Equal(d.Y, p.Y, 6);
            Assert.False(moving);
        }
    }
}
