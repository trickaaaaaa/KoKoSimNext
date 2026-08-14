using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 守備ビューを全景（縦155m）へハードカットしない（issue field-view-cut-back-sooner）。
///
/// <para>居残りそのものより「全景へ跳んでから居残る」ほうが違和感の主因だった（監督指示 2026-08-13）。
/// <c>CameraPlan</c> のコメントは以前から「3Dの守備ビューは全景へ戻さずこの時刻の枠のまま止める」と
/// 書いていて、実装だけが戻していた＝コメント側が正しかった。</para>
///
/// <para>2D俯瞰（<see cref="CameraFraming.Default"/>）は全景に全部入るので<b>従来どおり戻す</b>
/// ＝ゴールデン不変。3Dの守備カメラだけが <c>ReturnsToWide:false</c> を渡す。</para>
/// </summary>
public sealed class FieldViewCutTests
{
    private static readonly CameraFraming Field3D =
        new(40.0, 2.0, CameraFraming.Default.PitchZoom, ThrowFollowsBall: true, ReturnsToWide: false);

    // ---- ④ 全景ハードカットの廃止 ----

    [Fact]
    public void Plan_NeverGoesWide_WhenReturnsToWideIsFalse()
    {
        var play = HomeRunLikePlay();
        var wideStart = CameraPlanBuilder.WideStartTime(play);
        var plan = CameraPlanBuilder.Build(play, Field3D);

        for (var t = wideStart; t <= play.Dur; t += 0.1)
        {
            var vp = CameraEvaluator.ViewportAt(plan, t);
            Assert.True(vp.ZoomMult > CameraPlanBuilder.WideZoom + 1e-6,
                $"t={t:F2} で全景（zoom={vp.ZoomMult:F2}）へ戻っている");
        }
    }

    /// <summary>2D俯瞰（既定）は従来どおり全景へ戻る＝この issue の変更が2Dへ漏れていない。</summary>
    [Fact]
    public void Plan_StillGoesWide_WithTheDefaultFraming()
    {
        var play = HomeRunLikePlay();
        var plan = CameraPlanBuilder.Build(play);
        var late = CameraEvaluator.ViewportAt(plan, play.Ball[0].T1 + 5.0);

        Assert.Equal(CameraPlanBuilder.WideZoom, late.ZoomMult, 3);
        Assert.Equal(CameraPlanBuilder.WideCx, late.Cx, 3);
        Assert.Equal(CameraPlanBuilder.WideCy, late.Cy, 3);
    }

    // ---- ② 本塁打の一周: 走者が枠から外れない ----

    [Fact]
    public void Runner_StaysFramed_WhileCirclingTheBases()
    {
        var play = HomeRunLikePlay();
        var wideStart = CameraPlanBuilder.WideStartTime(play);
        var plan = CameraPlanBuilder.Build(play, Field3D);
        var runner = play.Runners[0];
        var lastLeg = runner.Segs[^1];

        // ⚠ 全景へ戻し始める時刻ちょうどからは見ない。カメラは柵際（y=118m）の着弾を追っていて、
        // 内野の走者までは約80m。パン速度の上限（45 m/s）で戻るのに 1.8 秒かかる＝
        // その間は走者が枠に居ないのが**正しい**（レート制限を外すと酔う）。戻り切ってから見る。
        for (var t = wideStart + 2.5; t <= lastLeg.T1; t += 0.1)
        {
            var at = PlaybackEvaluator.RunnerAt(runner, t);
            if (!at.HasValue) continue;
            AssertWithinViewport(CameraEvaluator.ViewportAt(plan, t), at.Value.X, at.Value.Y, t, "走者");
        }
    }

    /// <summary>
    /// 走者を追う枠は「1人＋向かう塁」で足りる大きさに留まる（人が20pxになる全景へ広げない）。
    /// issue #566 で退けた「走者を全部入れる枠は47.8m必要」の再発防止。
    /// </summary>
    [Fact]
    public void RunnerFollowFrame_StaysTight()
    {
        var play = HomeRunLikePlay();
        var wideStart = CameraPlanBuilder.WideStartTime(play);
        var plan = CameraPlanBuilder.Build(play, Field3D);
        const double designHeightM = 520.0 / 3.35;

        var worst = 0.0;
        for (var t = wideStart + 1.0; t <= play.Runners[0].Segs[^1].T1; t += 0.1)
        {
            var frameH = designHeightM / CameraEvaluator.ViewportAt(plan, t).ZoomMult;
            if (frameH > worst) worst = frameH;
        }
        Assert.True(worst <= 45.0, $"走者追従の枠が広すぎる（縦 {worst:F1}m）＝全景に近づいている");
    }

    // ---- ① 凡打の尺: アウトになった打者走者の駆け抜けを待たない ----

    [Fact]
    public void LastActionTime_DoesNotWaitForTheOutBatterRunner()
    {
        var shortened = new List<double>();
        var plays = 0;
        var advancingKept = 0;

        foreach (var (entry, play) in Plays())
        {
            var oldRule = OldLastEventTime(play);
            var neu = PlaybackDisplaySync.LastActionTime(play);
            Assert.True(neu <= oldRule + 1e-9, $"{entry}: 尺が伸びている（{oldRule:F2} → {neu:F2}）");

            if (entry == "InPlayOut")
            {
                plays++;
                shortened.Add(oldRule - neu);
            }
            else if (oldRule - neu > 1e-9)
            {
                advancingKept++;   // 打者がアウトでないプレーで縮んだ＝規則が漏れている
            }
        }

        Assert.True(plays >= 200, $"凡打が {plays} 件しかない＝テストが空回り");
        Assert.Equal(0, advancingKept);
        var median = shortened.OrderBy(x => x).ElementAt(shortened.Count / 2);
        Assert.True(median > 0.2, $"凡打の尺がほとんど縮んでいない（中央値 {median:F2}s）");
    }

    /// <summary>この issue の前の規則（走者レッグを全部待つ）。縮んだ量を測るための比較用。</summary>
    private static double OldLastEventTime(PlaybackPlay play)
    {
        var last = play.ResAt;
        foreach (var seg in play.Ball)
        {
            if (seg.T1 > last) last = seg.T1;
        }
        foreach (var r in play.Runners)
            foreach (var s in r.Segs)
            {
                if (s.From.X == s.To.X && s.From.Y == s.To.Y) continue;
                if (s.T1 > last) last = s.T1;
            }
        return last;
    }

    /// <summary>判定オーバーレイは短くした尺の中に必ず収まる（「切ったせいで一度も見えない」を作らない）。</summary>
    [Fact]
    public void ShortenedTail_StillCoversTheJudgementWindow()
    {
        const double hold = 0.6;
        const double tail = 0.6;
        var bad = new List<string>();

        foreach (var (entry, play) in Plays())
        {
            if (PlaybackEvaluator.LastJudgementEndTime(play, hold) is not { } judged) continue;
            // MatchLiveController.PlayEndTime と同じ組み方（判定の窓を必ず max で含める）。
            var end = Math.Max(Math.Min(play.Dur, PlaybackDisplaySync.LastActionTime(play) + tail), judged);
            if (end < judged - 1e-9) bad.Add($"{entry}: 判定が {judged - end:F2}s ぶん切れる");
        }
        Assert.True(bad.Count == 0, string.Join(" / ", bad.Take(5)));
    }

    // ---- 補助 ----

    private static IEnumerable<(string Result, PlaybackPlay Play)> Plays()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log)
            {
                if (e.Timeline is null) continue;
                yield return (e.Result.ToString(), PlayTimelineAdapter.ToPlaybackPlay(e.Timeline));
            }
        }
    }

    /// <summary>柵越え（球は 4.2 秒で死ぬ）＋打者がダイヤモンドを一周する（14.5 秒）プレー。</summary>
    private static PlaybackPlay HomeRunLikePlay()
    {
        var first = FieldDiagramGeometry.First;
        var second = FieldDiagramGeometry.Second;
        var third = FieldDiagramGeometry.Third;
        var home = FieldDiagramGeometry.Home;
        return new PlaybackPlay
        {
            Name = "hr", Dur = 17.0, Result = "本塁打", ResAt = 4.2,
            Ball = new PlaybackBallSegment[]
            {
                EndpointBallSegment.From(new BallSegment
                {
                    Kind = BallSegmentKind.Flight, T0 = 0.85, T1 = 4.2,
                    From = new TimelinePoint(0, 0), To = new TimelinePoint(10, 118), ApexHeightM = 30.0,
                }),
            },
            Runners = new[]
            {
                new PlaybackRunner
                {
                    Label = "打",
                    Segs = new[]
                    {
                        new PlaybackRun(0.95, 4.5, new PlaybackVec(home.X, home.Y), new PlaybackVec(first.X, first.Y)),
                        new PlaybackRun(4.5, 8.0, new PlaybackVec(first.X, first.Y), new PlaybackVec(second.X, second.Y)),
                        new PlaybackRun(8.0, 11.3, new PlaybackVec(second.X, second.Y), new PlaybackVec(third.X, third.Y)),
                        new PlaybackRun(11.3, 14.5, new PlaybackVec(third.X, third.Y), new PlaybackVec(home.X, home.Y)),
                    },
                },
            },
            Judgements = new[] { new PlaybackJudgement(4.2, JudgementKind.HomeRun, false, "打", 118.0) },
        };
    }

    private static void AssertWithinViewport(CameraViewport vp, double x, double y, double t, string label)
    {
        const double designWidthM = 760.0 / 3.35;
        const double designHeightM = 520.0 / 3.35;
        var halfW = designWidthM / 2.0 / vp.ZoomMult;
        var halfH = designHeightM / 2.0 / vp.ZoomMult;
        Assert.True(Math.Abs(x - vp.Cx) <= halfW + 1e-6,
            $"t={t:F2} {label} x={x:F2} が枠の外（半幅 {halfW:F2} / cx={vp.Cx:F2}）");
        Assert.True(Math.Abs(y - vp.Cy) <= halfH + 1e-6,
            $"t={t:F2} {label} y={y:F2} が枠の外（半高 {halfH:F2} / cy={vp.Cy:F2}）");
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team BuildTeam(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[]
            {
                Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage },
            },
        };
    }
}
