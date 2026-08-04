using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 表示同期の時刻計算（issue #438）: 結果テキストは「打者がセーフで生きる結果」だけ到達まで待ち、
/// 得点加算は各走者の本塁到達レッグ T1 に同期する。アウト側（#260/#328）と本塁打は従来の確定時刻のまま。
/// </summary>
public sealed class PlaybackDisplaySyncTests
{
    private static readonly PlaybackVec Home = new(0, 0);
    private static readonly PlaybackVec First = new(19.4, 19.4);
    private static readonly PlaybackVec Second = new(0, 38.8);

    private static PlaybackRunner Runner(string label, params PlaybackRun[] segs) =>
        new() { Label = label, Segs = segs };

    private static PlaybackPlay Play(double resAt, IReadOnlyList<PlaybackRunner> runners,
        params PlaybackJudgement[] judgements) => new()
    {
        Name = "test", Dur = 30, Result = "テスト", ResAt = resAt,
        Ball = Array.Empty<PlaybackBallSegment>(),
        Runners = runners, Judgements = judgements,
    };

    // 内野安打・外野前ヒット: 結果テキストは打者走者の一塁到達（レッグT1）まで待つ。
    [Fact]
    public void ResultShow_BatterSafe_WaitsForArrival()
    {
        // 外野への単打: ResAt=野手処理(2.8s) < 一塁到達(4.3s)
        var play = Play(2.8, new[] { Runner("打", new PlaybackRun(0.55, 4.3, Home, First)) });
        Assert.Equal(4.3, PlaybackDisplaySync.ResultShowTime(play));

        // 内野安打（際どいセーフ判定付き）: ResAt=到達なのでそのまま
        var close = Play(4.3, new[] { Runner("打", new PlaybackRun(0.55, 4.3, Home, First) { CloseCall = true }) },
            new PlaybackJudgement(4.3, JudgementKind.Safe, true, "打"));
        Assert.Equal(4.3, PlaybackDisplaySync.ResultShowTime(close));
    }

    // 内野ゴロ（フォースアウト）: 送球到達（ResAt）で確定＝走者の到達を待たない（#328を壊さない）。
    [Fact]
    public void ResultShow_ForceOut_StaysAtResAt()
    {
        var play = Play(3.6, new[] { Runner("打", new PlaybackRun(0.55, 4.3, Home, First) { OutAtEnd = true }) },
            new PlaybackJudgement(3.6, JudgementKind.Out, false, "打"));
        Assert.Equal(3.6, PlaybackDisplaySync.ResultShowTime(play));
    }

    // フライアウト: 打者レッグは終端アウトにならず走り切るが、捕球の瞬間（ResAt）で確定（#260を壊さない）。
    [Fact]
    public void ResultShow_FlyOut_StaysAtCatch()
    {
        var play = Play(3.0, new[] { Runner("打", new PlaybackRun(0.55, 4.3, Home, First)) },
            new PlaybackJudgement(3.0, JudgementKind.Out, false, "打"));
        Assert.Equal(3.0, PlaybackDisplaySync.ResultShowTime(play));
    }

    // 本塁打: 柵越え（ResAt=着地）で確定＝ダイヤモンド一周を待たない。
    [Fact]
    public void ResultShow_HomeRun_StaysAtLanding()
    {
        var trot = Runner("打",
            new PlaybackRun(0.55, 4.5, Home, First),
            new PlaybackRun(4.5, 15.9, First, Home));
        var play = Play(5.0, new[] { trot }, new PlaybackJudgement(5.0, JudgementKind.HomeRun, false, "打"));
        Assert.Equal(5.0, PlaybackDisplaySync.ResultShowTime(play));
    }

    // 得点加算: 各走者の本塁到達レッグ T1 の昇順。
    [Fact]
    public void RunScore_TwoRunners_EachHomeArrival()
    {
        var play = Play(4.3, new[]
        {
            Runner("打", new PlaybackRun(0.55, 4.3, Home, First)),
            Runner("走1", new PlaybackRun(0.4, 7.7, new PlaybackVec(-19.4, 19.4), Home)),
            Runner("走2", new PlaybackRun(0.4, 9.9, Second, Home)),
        });
        Assert.Equal(new[] { 7.7, 9.9 }, PlaybackDisplaySync.RunScoreTimes(play, 2, 0.5));
    }

    // 本塁到達が生還数より多い（第3アウトと同時のタイムプレー）: 先着から数える。
    [Fact]
    public void RunScore_MoreArrivalsThanRuns_TakesEarliest()
    {
        var play = Play(4.3, new[]
        {
            Runner("走1", new PlaybackRun(0.4, 7.7, new PlaybackVec(-19.4, 19.4), Home)),
            Runner("走2", new PlaybackRun(0.4, 9.9, Second, Home)),
        });
        Assert.Equal(new[] { 7.7 }, PlaybackDisplaySync.RunScoreTimes(play, 1, 0.5));
    }

    // 本塁到達レッグが足りない（異常系の保険）: 最後の既知時刻から等間隔で続ける。
    [Fact]
    public void RunScore_MissingArrivals_FallsBackToSteps()
    {
        var play = Play(4.3, new[]
        {
            Runner("走1", new PlaybackRun(0.4, 7.7, Second, Home)),
        });
        Assert.Equal(new[] { 7.7, 8.2 }, PlaybackDisplaySync.RunScoreTimes(play, 2, 0.5));
        // 本塁レッグが1本も無い場合は ResAt 起点＝従来の等間隔挙動へ縮退
        var none = Play(4.3, Array.Empty<PlaybackRunner>());
        Assert.Equal(new[] { 4.8, 5.3 }, PlaybackDisplaySync.RunScoreTimes(none, 2, 0.5));
    }

    // 終端アウトの本塁突入（本塁憤死）は加算しない。
    [Fact]
    public void RunScore_OutAtHome_NotCounted()
    {
        var play = Play(4.3, new[]
        {
            Runner("走1", new PlaybackRun(0.4, 7.7, Second, Home) { OutAtEnd = true }),
            Runner("走2", new PlaybackRun(0.4, 9.9, new PlaybackVec(-19.4, 19.4), Home)),
        });
        Assert.Equal(new[] { 9.9 }, PlaybackDisplaySync.RunScoreTimes(play, 1, 0.5));
    }

    // アダプタの結果キャプション再時刻付け（issue #438）: 「決着時刻・結果テキスト」の実況行だけを
    // 表示解決時刻（打者到達）へ動かし、中間実況・後続の追記実況（本塁クロスの「還った！」等）は
    // 時系列どおりに読める（CaptionAt はリスト順上書きなので安定ソートも掛かっていること）。
    [Fact]
    public void Adapter_RetimesResultCaption_ToBatterArrival()
    {
        var timeline = new PlayTimeline
        {
            Duration = 10,
            Result = "レフト前ヒット",
            ResolvedAt = 2.8,
            Runners = new[]
            {
                new RunnerLeg { Label = "打", T0 = 0.55, T1 = 4.3, From = new TimelinePoint(0, 0), To = new TimelinePoint(19.4, 19.4) },
            },
            Captions = new[]
            {
                new TimelineCaption(2.0, "レフトが捕った"),
                new TimelineCaption(2.8, "レフト前ヒット"),
                // 本塁クロスプレーの追記実況（リスト上は結果の後ろ・時刻は打者到達より前）
                new TimelineCaption(3.9, "際どいセーフ、還った！"),
            },
        };
        var play = PlayTimelineAdapter.ToPlaybackPlay(timeline);

        Assert.Equal("レフトが捕った", PlaybackEvaluator.CaptionAt(play, 2.9));   // 旧: ここで結果が出ていた
        Assert.Equal("際どいセーフ、還った！", PlaybackEvaluator.CaptionAt(play, 4.0));
        Assert.Equal("レフト前ヒット", PlaybackEvaluator.CaptionAt(play, 4.35)); // 到達で結果
    }

    // 実シム不変条件: 全得点プレーで「本塁到達レッグ数＝生還数」（保険パスを通らない）かつ
    // 加算時刻は決着（ResAt）以後（実測 2026-08-04 の30試合probeを回帰として固定・軽量5試合）。
    [Fact]
    public void RealSim_ScoringPlays_ArrivalsMatchRuns()
    {
        Player Pos(FieldPosition p) => new()
        {
            Position = p, Contact = 50, Power = 50, LaunchTendency = 50,
            Discipline = 50, Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
        };
        Team Team(string name) => new()
        {
            Name = name,
            BattingOrder = new List<Player>
            {
                Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
                Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
                Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
                Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
            },
            PitcherSlot = 8,
        };

        var scoringPlays = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true }, seed);
            while (prog.Advance())
            {
                var cur = prog.Current;
                if (cur?.Play is not { } play || cur.RunsScored <= 0) continue;
                scoringPlays++;
                var times = PlaybackDisplaySync.RunScoreTimes(play, cur.RunsScored, 0.5);
                Assert.Equal(cur.RunsScored, times.Count);
                // 保険パス（等間隔の継ぎ足し）を通っていない＝全部が実レッグ由来:
                // レッグ由来なら昇順・重複なしで、最後の時刻がプレー尺内に収まる
                Assert.True(times.SequenceEqual(times.OrderBy(t => t)));
                Assert.True(times[^1] <= play.Dur + 0.001,
                    $"生還時刻 {times[^1]:F2} がプレー尺 {play.Dur:F2} を超えた（保険パス混入の疑い）");
                // 結果テキストの表示時刻は決着以後・打者レッグ到達以後
                Assert.True(PlaybackDisplaySync.ResultShowTime(play) >= play.ResAt - 0.001);
            }
        }
        Assert.True(scoringPlays > 0, "得点プレーが1件も無い＝テストが何も検証していない");
    }
}
