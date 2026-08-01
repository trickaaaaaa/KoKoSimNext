using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 2アウトのフライアウトはタッチアップ不要のため、塁上の走者は接触と同時にスタートを切り、
/// 捕球時刻（＝イニング終了）の位置で止まるべき（issue #299）。0/1アウトは従来どおり静止（回帰）。
/// 表示専用の契約テスト（判定・乱数順には触れない）。
/// </summary>
public sealed class TwoOutFlyAutoRunTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();

    private static FieldingPlay FlyOut() => new()
    {
        Result = BattedBallResult.Out,
        LandingX = 0.0, LandingZ = 90.0, HangTimeSeconds = 3.0, ApexHeightM = 20.0,
        RangeM = 90.0, BearingDeg = 0.0, FielderRole = FieldPosition.CenterField,
        FieldedAtSeconds = 3.0, BatterToFirstSeconds = 4.2, IsFly = true,
    };

    private static PlayTimeline FlyOutTimeline() => TimelineBuilder.BuildBattedBall(
        FlyOut(), Field.StandardAlignment(), Field, null, runnersOn: true,
        TimelineBuilder.DescribeResult(FlyOut()));

    private static PlaybackVec Vec(Vector3D p) => new(p.X, p.Z);

    private static double DistanceFromFirst(PlaybackVec p)
    {
        var first = Vec(Field.FirstBase);
        return Math.Sqrt(Math.Pow(p.X - first.X, 2) + Math.Pow(p.Y - first.Y, 2));
    }

    [Fact]
    public void TwoOuts_FlyOut_RunnerOnFirst_StartsAtContact_AndStopsAtCatch()
    {
        var runner = new Player { Speed = 50 };
        var tl = TimelineBuilder.AppendHeldRunners(
            FlyOutTimeline(), first: true, second: false, third: false,
            runner, null, null,
            moves: new List<RunnerMove>(), Field, outsBefore: 2, FlyOut(), Baserunning);

        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var r = Assert.Single(play.Runners, x => x.Label == "走一");

        // 投球開始時は一塁の上。接触までに二次リード（3.5m）ぶんだけ前へ出る＝**塁は離れない**。
        // リードを描かないと、時計が「塁間からリードを引いた距離」で回っているぶん、走るレッグが
        // 満距離を短い時間で刻む（＝脚力を超えた速さになる）。
        Assert.True(DistanceFromFirst(PlaybackEvaluator.RunnerAt(r, 0.0)!.Value) < 0.01, "投球開始時に一塁に居ない");
        var beforeContact = PlaybackEvaluator.RunnerAt(r, 0.5);
        Assert.NotNull(beforeContact);
        Assert.InRange(DistanceFromFirst(beforeContact!.Value), 0.0, Baserunning.HomeLeadDistanceM + 1e-6);

        // 捕球時刻（0.85 + 3.0 = 3.85s）には一塁から動き出している。
        var catchAt = 0.85 + 3.0;
        var atCatch = PlaybackEvaluator.RunnerAt(r, catchAt);
        Assert.NotNull(atCatch);
        Assert.True(DistanceFromFirst(atCatch!.Value) > 1.0, "2アウトのフライで走者がスタートを切っていない");

        // 捕球後（イニング終了後）はその位置で静止する（帰塁の描画はしない）。
        var afterCatch = PlaybackEvaluator.RunnerAt(r, tl.Duration);
        Assert.NotNull(afterCatch);
        Assert.Equal(atCatch.Value.X, afterCatch!.Value.X, 3);
        Assert.Equal(atCatch.Value.Y, afterCatch.Value.Y, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ZeroOrOneOut_FlyOut_RunnerOnFirst_StaysStatic(int outsBefore)
    {
        var runner = new Player { Speed = 50 };
        var tl = TimelineBuilder.AppendHeldRunners(
            FlyOutTimeline(), first: true, second: false, third: false,
            runner, null, null,
            moves: new List<RunnerMove>(), Field, outsBefore, FlyOut(), Baserunning);

        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var r = Assert.Single(play.Runners, x => x.Label == "走一");

        var start = PlaybackEvaluator.RunnerAt(r, 0.0);
        var atCatch = PlaybackEvaluator.RunnerAt(r, 0.85 + 3.0);
        var end = PlaybackEvaluator.RunnerAt(r, tl.Duration);
        Assert.NotNull(start);
        Assert.NotNull(atCatch);
        Assert.NotNull(end);
        Assert.Equal(0.0, DistanceFromFirst(start!.Value), 3);
        Assert.Equal(0.0, DistanceFromFirst(atCatch!.Value), 3);
        Assert.Equal(0.0, DistanceFromFirst(end!.Value), 3);
    }
}
