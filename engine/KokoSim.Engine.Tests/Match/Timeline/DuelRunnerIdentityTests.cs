using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 打球タイムラインの走者→選手の対応（issue #393）。<see cref="TimelineBuilder"/> が
/// <see cref="RunnerLeg.Runner"/> に本人を積み、<see cref="PlayTimelineAdapter"/> がラベルでグルーピングする際に
/// 先頭レッグの本人を <see cref="PlaybackRunner.Identity"/> へ運ぶ（対決ビューの頭上フルネームラベル用）。
/// 表示専用データ＝判定・乱数順には一切関わらない。
/// </summary>
public sealed class DuelRunnerIdentityTests
{
    private static FieldingPlay Grounder() => FieldingPlayFixtures.InfieldGrounder(
        FieldPosition.Shortstop, landingX: -6.0, landingZ: 20.0);

    [Fact]
    public void BuildBattedBall_AttachesBatterIdentity_ToBatterRunnerLeg()
    {
        var field = new FieldGeometry();
        var play = Grounder();
        var batter = new Player { Position = FieldPosition.DesignatedHitter, Name = "打者太郎", UniformNumber = 20 };

        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play),
            batter: batter);

        // 内野ゴロは主到達レッグの後ろにオーバーラン(issue #443)レッグが続くため先頭を見る。
        var batterLeg = tl.Runners.First(r => r.Label == "打");
        Assert.Same(batter, batterLeg.Runner);

        var playback = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var batterRunner = Assert.Single(playback.Runners, r => r.Label == "打");
        Assert.Equal(new DuelPlayerIdentity("打者太郎", 20), batterRunner.Identity);
    }

    [Fact]
    public void BuildBattedBall_OmitsIdentity_WhenBatterNotPassed()
    {
        // 既存呼び出し（テスト・盗塁専用経路等）は batter を渡さない＝Runner は null のまま安全に素通り。
        var field = new FieldGeometry();
        var play = Grounder();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var batterLeg = tl.Runners.First(r => r.Label == "打");
        Assert.Null(batterLeg.Runner);

        var playback = PlayTimelineAdapter.ToPlaybackPlay(tl);
        Assert.Null(Assert.Single(playback.Runners, r => r.Label == "打").Identity);
    }

    [Fact]
    public void AppendHeldRunners_AttachesRunnerIdentity_DistinctFromBatter()
    {
        var field = new FieldGeometry();
        var play = Grounder();
        var batter = new Player { Position = FieldPosition.DesignatedHitter, Name = "打者太郎", UniformNumber = 20 };
        var firstRunner = new Player { Position = FieldPosition.CenterField, Name = "一塁走者", UniformNumber = 21 };

        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true, TimelineBuilder.DescribeResult(play),
            batter: batter);
        tl = TimelineBuilder.AppendHeldRunners(
            tl, first: true, second: false, third: false,
            firstPlayer: firstRunner, secondPlayer: null, thirdPlayer: null,
            moves: Array.Empty<RunnerMove>(), field, outsBefore: 0, play, new BaserunningCoefficients());

        var heldLeg = Assert.Single(tl.Runners, r => r.Label == "走一");
        Assert.Same(firstRunner, heldLeg.Runner);

        var playback = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var heldRunner = Assert.Single(playback.Runners, r => r.Label == "走一");
        Assert.Equal(new DuelPlayerIdentity("一塁走者", 21), heldRunner.Identity);
        Assert.NotEqual(
            Assert.Single(playback.Runners, r => r.Label == "打").Identity, heldRunner.Identity);
    }
}
