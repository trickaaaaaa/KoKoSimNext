using System;
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
/// 「外野手があと一歩届かない」場面のシナリオ（issue #1075 追記3・#1084・data/debug/scenarios.yaml の
/// fly-near-miss-*-lab）が、<b>最初の1打席でその打球を出す</b>こと。
/// 3Dの「届かない」芝居は追う向き（前・背走・横・その場）で収録を選ぶ（DuelStageRig.MissApproachAt と同じ分類を
/// ここでも計算する）。自然な試合では向きを狙って出せない＝撮影はこのシナリオで場面を作る。
/// 係数変更で打球が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class FlyNearMissScenarioTests
{
    [Theory]
    [InlineData("fly-near-miss-back-lab", "Back", false)]
    [InlineData("fly-near-miss-front-lab", "Forward", false)]
    [InlineData("fly-near-miss-left-lab", "Left", false)]
    [InlineData("fly-near-miss-right-lab", "Right", false)]
    [InlineData("fly-near-miss-fence-lab", "Back", true)]
    public void FirstPlateAppearance_IsAFlyThatJustClearsTheOutfielder(string id, string approach, bool nearFence)
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet(id, out var def), $"{id} が無い");
        Assert.Null(def.Force);   // 強制発動は打球の軌道を作らない

        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(prog.Advance(), "最初の打席が確定しない");

        var tl = prog.PlayLog[^1].Timeline;
        Assert.NotNull(tl);
        // 3D の「届かない」芝居が出る幅（DuelStageRig.MissMarginSeconds = 0.50）の内側で、捕れていない
        Assert.True(tl!.CatchMarginSeconds is { } m && m < 0 && m > -0.50,
            $"捕球余裕が「あと一歩届かない」幅に無い（{tl.CatchMarginSeconds}）");

        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var seg = play.Ball.First(b => b is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Flight);
        double tEnd = seg.T1 - seg.T0;
        var end = seg.At(tEnd);

        // 落下点に一番近い外野手が追う
        var who = new[] { FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField }
            .OrderBy(p => Dist2(PlaybackEvaluator.FielderAt(play, p, seg.T1).Pos, end)).First();
        Assert.True(Math.Sqrt(Dist2(PlaybackEvaluator.FielderAt(play, who, seg.T1).Pos, end)) <= 4.0,
            "着地の瞬間に落下点のそばに外野手が居ない（芝居の持ち主が決まらない）");

        Assert.Equal(approach, MissApproach(play, who, seg.T1 - 0.4, seg, tEnd));

        double bearing = Math.Atan2(end.X, end.Y);
        double toFence = built.Ctx.Field.FenceDistance(bearing) - Math.Sqrt(end.X * end.X + end.Y * end.Y);
        Assert.Equal(nearFence, toFence < 4.0);
    }

    private static double Dist2(PlaybackVec p, PlaybackBall b) => (p.X - b.X) * (p.X - b.X) + (p.Y - b.Y) * (p.Y - b.Y);

    /// <summary>DuelStageRig.MissApproachAt の写し（走る向き vs 球が飛んでくる向き）。</summary>
    private static string MissApproach(PlaybackPlay play, FieldPosition who, double t, PlaybackBallSegment seg, double tEnd)
    {
        const double dt = 1.0 / 60;   // DuelStageRig.LocoSampleDt
        double ta = Math.Max(0, t - dt), tb = t + dt;
        var pa = PlaybackEvaluator.FielderAt(play, who, ta).Pos;
        var pb = PlaybackEvaluator.FielderAt(play, who, tb).Pos;
        double vx = (pb.X - pa.X) / (tb - ta), vy = (pb.Y - pa.Y) / (tb - ta);
        if (Math.Sqrt(vx * vx + vy * vy) < 1.2) return "Still";
        var end = seg.At(tEnd);
        var back = seg.At(Math.Max(0, tEnd - 0.1));
        double ang = (Math.Atan2(back.Y - end.Y, back.X - end.X) - Math.Atan2(vy, vx)) * 180 / Math.PI;
        while (ang > 180) ang -= 360;
        while (ang < -180) ang += 360;
        return Math.Abs(ang) < 45 ? "Forward" : Math.Abs(ang) > 135 ? "Back" : ang > 0 ? "Left" : "Right";
    }
}
