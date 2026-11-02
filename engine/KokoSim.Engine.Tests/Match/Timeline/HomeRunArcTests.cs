using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 本塁打の弧は柵の上（判定に使った高さ）を必ず通る（issue #743・表示専用）。対称な放物線のままだと、
/// ぎりぎりの当たりが柵の位置でフェンスより低く描かれ、柵の手前に落ちて見えていた。
/// </summary>
public sealed class HomeRunArcTests
{
    private static EndpointBallSegment FlightOf(PlaybackPlay pb)
        => pb.Ball.OfType<EndpointBallSegment>().First(s => s.Kind == BallSegmentKind.Flight);

    /// <summary>水平距離 range[m] の位置での絵の高さ（区間の水平は時間に等速）。</summary>
    private static double HeightAtRange(EndpointBallSegment f, double range)
    {
        // 二分探索で水平距離 range に届く時刻を探す（区間は本塁から外向きに単調）。
        double lo = 0, hi = f.T1 - f.T0;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            var b = f.At(mid);
            if (Math.Sqrt(b.X * b.X + b.Y * b.Y) < range) lo = mid; else hi = mid;
        }
        return f.At((lo + hi) / 2).H;
    }

    [Fact]
    public void WallScraper_PassesFenceTopAtFenceDistance()
    {
        // 柵 106m・柵上 4.3m（フェンス4mをかすめる）、着地は柵の 6m 先。
        var play = FieldingPlayFixtures.HomeRun(landingZ: 112.0, hangTimeSeconds: 4.6, apexHeightM: 24.0,
            fenceCrossRangeM: 106.0, fenceCrossHeightM: 4.3);
        var field = new FieldGeometry();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        var f = FlightOf(PlayTimelineAdapter.ToPlaybackPlay(tl));

        Assert.InRange(HeightAtRange(f, 106.0), 4.2, 4.4);
        Assert.InRange(f.At(f.T1 - f.T0).H, -1e-9, 1e-6);            // 着地は地面
        // 頂点は判定の頂点にほぼ近い（前半の放物線の頂点）。
        var top = Enumerable.Range(0, 201).Max(i => f.At((f.T1 - f.T0) * i / 200.0).H);
        Assert.InRange(top, 21.0, 25.0);
        // 柵の先は下りっぱなし（もう一度上がらない）。
        var prev = double.MaxValue;
        for (var r = 106.0; r <= 111.5; r += 0.5)
        {
            var h = HeightAtRange(f, r);
            Assert.True(h <= prev + 1e-6, $"柵の先で上がった: {r}m で {h}");
            prev = h;
        }
    }

    /// <summary>実試合の本塁打すべてで、柵の距離での絵の高さが判定の柵上の高さに 0.1m 以内で一致する。</summary>
    [Fact]
    public void GameHomeRuns_DrawnHeightAtFence_MatchesJudgedHeight()
    {
        var checkedCount = 0;
        var root = new Xoshiro256Random(743);
        for (var i = 0; i < 30; i++)
        {
            var g = root.Fork((ulong)i);
            var a = StrengthTeamFactory.Create(85.0, "A", g.Fork(1));
            var h = StrengthTeamFactory.Create(85.0, "H", g.Fork(2));
            var p = GameEngine.NewProgress(a, h, new GameContext { CaptureTimelines = true }, g);
            foreach (var _ in GameEngine.Steps(p)) { }
            var result = GameEngine.BuildResult(p);

            foreach (var e in result.Log.Where(e => e.Result == PlateAppearanceResult.HomeRun && e.Timeline != null))
            {
                var seg = e.Timeline!.Ball.First(s => s.Kind == BallSegmentKind.Flight);
                Assert.NotNull(seg.PassFraction);
                var land = Math.Sqrt(seg.To.X * seg.To.X + seg.To.Z * seg.To.Z);
                var fenceRange = seg.PassFraction!.Value * land;
                var f = FlightOf(PlayTimelineAdapter.ToPlaybackPlay(e.Timeline));
                Assert.InRange(HeightAtRange(f, fenceRange), seg.PassHeightM - 0.1, seg.PassHeightM + 0.1);
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 0);
    }
}
