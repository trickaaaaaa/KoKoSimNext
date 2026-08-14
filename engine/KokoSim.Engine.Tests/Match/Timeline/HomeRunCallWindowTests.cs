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
/// 「ホームラン」の判定表示は<b>打者走者が本塁を踏むまで</b>出し続ける
/// （issue homerun-call-until-home-touch）。
///
/// <para>柵越えの判定は<b>着地の瞬間</b>に立つのに、打者が一周して還るのは約10秒後
/// （実測400試合: p10 9.3 / p50 10.2 / p90 11.1 / max 12.1 秒）。表示窓が種別によらず固定 0.6 秒
/// だったため、<b>約9.6秒間なにも出ていない</b>時間ができていた。</para>
///
/// <para>窓の規則は engine（<see cref="PlaybackEvaluator.JudgementEndTime"/>）が単一ソースで、
/// 再生の早切り（<see cref="PlaybackEvaluator.LastJudgementEndTime"/>）も同じ規則を読む
/// ＝Unity 側に種別分岐を書かない（issue #236 症状A の方針）。</para>
/// </summary>
public sealed class HomeRunCallWindowTests
{
    private const double Hold = 0.6;
    private const int Games = 60;

    // ---- 窓の規則（合成データ） ----

    [Fact]
    public void HomeRunCall_LastsUntilTheBatterTouchesHome()
    {
        var play = HomeRunPlay(landAt: 4.0, homeTouch: 14.2);

        Assert.Equal(JudgementKind.HomeRun, PlaybackEvaluator.JudgementAt(play, 4.0, Hold)!.Value.Kind);
        Assert.Equal(JudgementKind.HomeRun, PlaybackEvaluator.JudgementAt(play, 9.0, Hold)!.Value.Kind);   // 従来はここで空白
        Assert.Equal(JudgementKind.HomeRun, PlaybackEvaluator.JudgementAt(play, 14.2, Hold)!.Value.Kind);  // 本塁到達の瞬間
        Assert.Equal(JudgementKind.HomeRun, PlaybackEvaluator.JudgementAt(play, 14.79, Hold)!.Value.Kind); // 余韻
        Assert.Null(PlaybackEvaluator.JudgementAt(play, 14.81, Hold));                                     // 余韻が切れたら消える
    }

    /// <summary>推定飛距離（issue #354）はホームラン表示と同じ窓＝一緒に出続ける。</summary>
    [Fact]
    public void HomeRunDistance_StaysForTheSameWindow()
    {
        var play = HomeRunPlay(landAt: 4.0, homeTouch: 14.2, distanceM: 118.0);
        Assert.Equal(118.0, PlaybackEvaluator.JudgementAt(play, 12.0, Hold)!.Value.DistanceM!.Value, 6);
    }

    /// <summary>早切りは窓の終端まで含む＝「切ったせいで途中で消える」を作らない（issue #236 症状A）。</summary>
    [Fact]
    public void LastJudgementEndTime_CoversTheHomeRunWindow()
    {
        var play = HomeRunPlay(landAt: 4.0, homeTouch: 14.2);
        Assert.Equal(14.8, PlaybackEvaluator.LastJudgementEndTime(play, Hold)!.Value, 6);
    }

    /// <summary>ホームラン以外の種別は従来どおり固定窓（種別分岐が漏れて全部伸びていないこと）。</summary>
    [Theory]
    [InlineData(JudgementKind.Out)]
    [InlineData(JudgementKind.Safe)]
    [InlineData(JudgementKind.Fair)]
    public void OtherKinds_KeepTheFixedWindow(JudgementKind kind)
    {
        var play = HomeRunPlay(landAt: 4.0, homeTouch: 14.2) with
        {
            Judgements = new[] { new PlaybackJudgement(4.0, kind, CloseCall: true, "打") },
        };
        Assert.Equal(4.6, PlaybackEvaluator.LastJudgementEndTime(play, Hold)!.Value, 6);
        Assert.Null(PlaybackEvaluator.JudgementAt(play, 5.0, Hold));
    }

    /// <summary>打者レッグが無い（モック等）なら従来の固定窓へ落ちる＝落ちても壊れない。</summary>
    [Fact]
    public void FallsBackToFixedWindow_WhenTheBatterLegIsMissing()
    {
        var play = HomeRunPlay(landAt: 4.0, homeTouch: 14.2) with
        {
            Runners = Array.Empty<PlaybackRunner>(),
        };
        Assert.Equal(4.6, PlaybackEvaluator.LastJudgementEndTime(play, Hold)!.Value, 6);
    }

    // ---- 実試合の本塁打 ----

    /// <summary>
    /// 実試合の本塁打で「着地 → 打者の本塁到達」の全区間に表示が出続ける（＝空白が0）。
    /// あわせて、打者の本塁到達レッグが全件あることも確認する（無いと窓が伸ばせない）。
    /// </summary>
    [Fact]
    public void RealHomeRuns_ShowTheCallForTheWholeCircuit()
    {
        var homeRuns = 0;
        var gaps = new List<string>();
        var noTouch = 0;
        var spans = new List<double>();

        foreach (var (result, play) in HomeRunPlays())
        {
            homeRuns++;
            var touch = PlaybackEvaluator.BatterHomeTouchTime(play);
            if (touch is not { } th) { noTouch++; continue; }
            var land = play.Judgements.First(j => j.Kind == JudgementKind.HomeRun).T;
            spans.Add(th - land);

            // 着地から本塁到達まで 0.1 秒刻みで舐めて、一度でも消えたら記録する。
            for (var t = land; t <= th; t += 0.1)
            {
                if (PlaybackEvaluator.JudgementAt(play, t, Hold) is not { } j || j.Kind != JudgementKind.HomeRun)
                {
                    gaps.Add($"{result}: t={t:F1}s で表示が消えている（着地{land:F1} 到達{th:F1}）");
                    break;
                }
            }
        }

        Assert.True(homeRuns >= 10, $"本塁打が {homeRuns} 本しか出ていない＝テストが空回り");
        Assert.Equal(0, noTouch);
        Assert.True(gaps.Count == 0, $"表示が途切れた本塁打が {gaps.Count}/{homeRuns} 本: {string.Join(" / ", gaps.Take(3))}");
        // 「約10秒の空白があった」という issue の実測と同じ形で、伸ばした長さを数字で残す。
        Assert.InRange(spans.Average(), 8.0, 13.0);
    }

    // ---- 補助 ----

    private static IEnumerable<(string Result, PlaybackPlay Play)> HomeRunPlays()
    {
        for (ulong seed = 1; seed <= Games; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log)
            {
                if (e.Timeline is null) continue;
                var play = PlayTimelineAdapter.ToPlaybackPlay(e.Timeline);
                if (play.Judgements.Any(j => j.Kind == JudgementKind.HomeRun))
                    yield return (e.Timeline.Result, play);
            }
        }
    }

    /// <summary>柵越え判定＋打者の一周レッグを持つ合成プレー。</summary>
    private static PlaybackPlay HomeRunPlay(double landAt, double homeTouch, double? distanceM = null) => new()
    {
        Name = "hr", Dur = homeTouch + 2.0, Result = "本塁打", ResAt = landAt,
        Ball = Array.Empty<PlaybackBallSegment>(),
        Judgements = new[] { new PlaybackJudgement(landAt, JudgementKind.HomeRun, false, "打", distanceM) },
        Runners = new[]
        {
            new PlaybackRunner
            {
                Label = "打",
                Segs = new[]
                {
                    new PlaybackRun(1.0, 5.0, new PlaybackVec(0, 0), new PlaybackVec(19.4, 19.4)),
                    new PlaybackRun(5.0, homeTouch, new PlaybackVec(19.4, 19.4), new PlaybackVec(0, 0)),
                },
            },
        },
    };

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 70, Power = 80, LaunchTendency = 70, Discipline = 50,
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
