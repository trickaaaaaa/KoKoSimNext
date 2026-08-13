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
/// 2アウトの内野ゴロでは、フォースされた走者は戻れないのでスタートを切る（issue #625）。
///
/// <para>判定側（<c>BaserunningModel</c>）は3アウト目の凡打で進塁計算を打ち切る＝移動レッグが
/// 1件も積まれないため、表示側で拾わないと走者が塁に張り付いて見える。止める時刻は
/// <b>打者が一塁でアウトになる瞬間</b>（監督決定 2026-08-13・案B＝走り切らせない）。</para>
///
/// <para>フォースの無い走者・0/1アウトは従来どおり静止（回帰）。
/// 表示専用の契約テスト（判定・乱数順には触れない）。</para>
/// </summary>
public sealed class TwoOutGrounderForceRunTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();

    /// <summary>遊撃ゴロ→一塁アウト。<c>IsFly=false</c>＝地面に落ちた打球（タッチアップ不要）。</summary>
    private static FieldingPlay GroundOut() => new()
    {
        Result = BattedBallResult.Out,
        LandingX = -12.0, LandingZ = 30.0, HangTimeSeconds = 0.6, ApexHeightM = 2.0,
        RangeM = 34.0, BearingDeg = -20.0, FielderRole = FieldPosition.Shortstop,
        FieldedAtSeconds = 1.5, BatterToFirstSeconds = 4.3, IsFly = false,
        FielderThrowSpeedMps = 33.0,
    };

    private static PlayTimeline GroundOutTimeline() => TimelineBuilder.BuildBattedBall(
        GroundOut(), Field.StandardAlignment(), Field, null, runnersOn: true,
        TimelineBuilder.DescribeResult(GroundOut()));

    private static PlaybackVec Vec(Vector3D p) => new(p.X, p.Z);

    private static double DistanceFrom(PlaybackVec p, Vector3D base3d)
    {
        var b = Vec(base3d);
        return Math.Sqrt(Math.Pow(p.X - b.X, 2) + Math.Pow(p.Y - b.Y, 2));
    }

    /// <summary>打者が一塁へ到達する（＝3アウト目が成立する）時刻。走者を止める時刻と同じ源。</summary>
    private static double BatterArriveOf(PlayTimeline tl)
    {
        foreach (var r in tl.Runners)
            if (r.Label == "打") return r.T1;
        throw new Xunit.Sdk.XunitException("打者のレッグが無い");
    }

    private static PlayTimeline Build(bool first, bool second, bool third, int outsBefore,
        Player? f = null, Player? s = null, Player? t = null)
        => TimelineBuilder.AppendHeldRunners(
            GroundOutTimeline(), first, second, third, f, s, t,
            moves: new List<RunnerMove>(), Field, outsBefore, GroundOut(), Baserunning);

    /// <summary>本題: 2アウト・一塁のゴロアウトで、一塁走者が二塁方向へ動く。</summary>
    [Fact]
    public void TwoOuts_GroundOut_RunnerOnFirst_StartsAndStopsAtBatterOut()
    {
        var runner = new Player { Speed = 50 };
        var tl = Build(first: true, second: false, third: false, outsBefore: 2, f: runner);
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var r = Assert.Single(play.Runners, x => x.Label == "走一");

        // 投球開始時は一塁の上。接触までに二次リードぶんだけ前へ出る＝塁は離れない。
        Assert.True(DistanceFrom(PlaybackEvaluator.RunnerAt(r, 0.0)!.Value, Field.FirstBase) < 0.01,
            "投球開始時に一塁に居ない");
        var beforeContact = PlaybackEvaluator.RunnerAt(r, 0.5)!.Value;
        Assert.InRange(DistanceFrom(beforeContact, Field.FirstBase), 0.0, Baserunning.HomeLeadDistanceM + 1e-6);

        // 3アウト目（打者が一塁でアウト）の時点で一塁から動き出し、二塁へ寄っている。
        var outAt = BatterArriveOf(tl);
        var atOut = PlaybackEvaluator.RunnerAt(r, outAt)!.Value;
        Assert.True(DistanceFrom(atOut, Field.FirstBase) > 1.0, "2アウトのゴロで走者がスタートを切っていない");
        Assert.True(DistanceFrom(atOut, Field.SecondBase) < DistanceFrom(beforeContact, Field.SecondBase),
            "二塁へ近づいていない");

        // 案B: その瞬間で凍結する（走り切らせない・帰塁も描かない）。
        var end = PlaybackEvaluator.RunnerAt(r, tl.Duration)!.Value;
        Assert.Equal(atOut.X, end.X, 3);
        Assert.Equal(atOut.Y, end.Y, 3);
    }

    /// <summary>フォースの無い走者は静止のまま（engine も進塁を解いていない）。</summary>
    [Fact]
    public void TwoOuts_GroundOut_UnforcedRunnerOnSecond_StaysStatic()
    {
        var runner = new Player { Speed = 50 };
        var tl = Build(first: false, second: true, third: false, outsBefore: 2, s: runner);
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var r = Assert.Single(play.Runners, x => x.Label == "走二");

        foreach (var t in new[] { 0.0, 0.5, BatterArriveOf(tl), tl.Duration })
            Assert.Equal(0.0, DistanceFrom(PlaybackEvaluator.RunnerAt(r, t)!.Value, Field.SecondBase), 3);
    }

    /// <summary>一塁が埋まっていれば二塁走者もフォース＝動く（連なりが効いている）。</summary>
    [Fact]
    public void TwoOuts_GroundOut_ForcedRunnerOnSecond_Starts()
    {
        var tl = Build(first: true, second: true, third: false, outsBefore: 2,
            f: new Player { Speed = 50 }, s: new Player { Speed = 50 });
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var r = Assert.Single(play.Runners, x => x.Label == "走二");

        var atOut = PlaybackEvaluator.RunnerAt(r, BatterArriveOf(tl))!.Value;
        Assert.True(DistanceFrom(atOut, Field.SecondBase) > 1.0, "一・二塁で二塁走者が動いていない");
    }

    /// <summary>0/1アウトのゴロは従来どおり静止（回帰）。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ZeroOrOneOut_GroundOut_RunnerOnFirst_StaysStatic(int outsBefore)
    {
        var tl = Build(first: true, second: false, third: false, outsBefore, f: new Player { Speed = 50 });
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var r = Assert.Single(play.Runners, x => x.Label == "走一");

        foreach (var t in new[] { 0.0, 0.5, BatterArriveOf(tl), tl.Duration })
            Assert.Equal(0.0, DistanceFrom(PlaybackEvaluator.RunnerAt(r, t)!.Value, Field.FirstBase), 3);
    }
}
