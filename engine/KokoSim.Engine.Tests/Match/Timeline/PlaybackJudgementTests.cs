using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 判定オーバーレイ（issue #59）の表示窓を決める純関数（<see cref="PlaybackEvaluator.JudgementAt"/> /
/// <see cref="PlaybackEvaluator.LastJudgementTime"/>）のテスト。
/// ビュー（Match2DPlaybackElement）と再生の打ち切り時刻（MatchLiveController.PlayEndTime）が
/// この単一ソースを共有するため、両者の食い違い（issue #236 症状A＝内野ゴロの「アウト」が
/// 一度も表示されない）を engine 側で回帰させる。
/// </summary>
public sealed class PlaybackJudgementTests
{
    private const double Hold = 0.6;

    private static PlaybackRun Leg(double t0, double t1, bool outAtEnd = false, bool closeCall = false) =>
        new(t0, t1, new PlaybackVec(0, 0), new PlaybackVec(27, 0)) { OutAtEnd = outAtEnd, CloseCall = closeCall };

    private static PlaybackPlay PlayWith(params PlaybackRun[] segs) => new()
    {
        Name = "test", Dur = 10, Result = "test", ResAt = 1,
        Ball = Array.Empty<PlaybackBallSegment>(),
        Runners = new[] { new PlaybackRunner { Label = "打", Segs = segs } },
    };

    [Fact]
    public void JudgementAt_ShowsOut_OnlyInsideHoldWindow()
    {
        var play = PlayWith(Leg(0.5, 4.0, outAtEnd: true));

        Assert.Null(PlaybackEvaluator.JudgementAt(play, 3.99, Hold));           // 判定前は出さない
        Assert.True(PlaybackEvaluator.JudgementAt(play, 4.0, Hold)!.Value.IsOut);  // 終端の瞬間から
        Assert.True(PlaybackEvaluator.JudgementAt(play, 4.59, Hold)!.Value.IsOut); // hold の直前まで
        Assert.Null(PlaybackEvaluator.JudgementAt(play, 4.61, Hold));           // hold 経過で消える
    }

    [Fact]
    public void JudgementAt_IgnoresLegsWithoutJudgement()
    {
        // 生還・進塁だけのレッグ（アウトでも際どくもない）は判定を出さない＝表示のゲートはデータ側。
        var play = PlayWith(Leg(0.5, 4.0));
        Assert.Null(PlaybackEvaluator.JudgementAt(play, 4.0, Hold));
        Assert.Null(PlaybackEvaluator.LastJudgementTime(play));
    }

    [Fact]
    public void JudgementAt_ShowsSafe_OnCloseCallOnly()
    {
        var play = PlayWith(Leg(0.5, 4.0, closeCall: true));
        var j = PlaybackEvaluator.JudgementAt(play, 4.2, Hold);
        Assert.NotNull(j);
        Assert.False(j!.Value.IsOut);   // 際どい判定＝セーフ表示
        Assert.Equal(4.0, j.Value.T1, 6);
    }

    [Fact]
    public void JudgementAt_PicksLatestJudgement_OnDoublePlay()
    {
        // 併殺: 二塁封殺(3.0) → 一塁封殺(4.0)。両方が窓に入る時刻でも直近の判定だけを出す。
        var play = new PlaybackPlay
        {
            Name = "dp", Dur = 10, Result = "dp", ResAt = 4,
            Ball = Array.Empty<PlaybackBallSegment>(),
            Runners = new[]
            {
                new PlaybackRunner { Label = "走1", Segs = new[] { Leg(0.5, 3.0, outAtEnd: true) } },
                new PlaybackRunner { Label = "打", Segs = new[] { Leg(0.5, 4.0, outAtEnd: true) } },
            },
        };

        Assert.Equal(3.0, PlaybackEvaluator.JudgementAt(play, 3.4, Hold)!.Value.T1, 6);
        Assert.Equal(4.0, PlaybackEvaluator.JudgementAt(play, 4.0, Hold)!.Value.T1, 6);
        Assert.Equal(4.0, PlaybackEvaluator.LastJudgementTime(play)!.Value, 6);
    }

    [Fact]
    public void LastJudgementTime_ScalesWindowWithHold()
    {
        // 倍速再生では hold をプレー時間軸で広げる（実時間の見え方を一定にする）。窓の始点は変わらない。
        var play = PlayWith(Leg(0.5, 4.0, outAtEnd: true));
        Assert.Null(PlaybackEvaluator.JudgementAt(play, 5.5, Hold));            // ×1 では消えている
        Assert.NotNull(PlaybackEvaluator.JudgementAt(play, 5.5, Hold * 4));     // ×4 なら まだ出ている
    }

    /// <summary>
    /// issue #236 症状A の回帰。内野ゴロの打者走者レッグは「送球到達＝アウト成立」より後に終わる
    /// （＝アウトでも一塁まで走り切る）ため、再生を「結果確定＋最後の送球＋0.6秒」で早切りすると
    /// 判定表示窓 [T1, T1+hold) の始点にすら届かず「アウト」が一度も出ない。
    /// </summary>
    [Fact]
    public void GroundOut_JudgementComesAfterDeadOutEarlyCut()
    {
        var tl = CollectGroundOut();
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);

        var judged = PlaybackEvaluator.LastJudgementTime(play);
        Assert.NotNull(judged);   // 内野ゴロは打者走者レッグに OutAtEnd が立つ

        // 旧 PlayEndTime（早切り）＝ min(Dur, max(ResAt, ボール終端) + 0.6)。
        var lastEvent = play.Ball.Aggregate(play.ResAt, (m, s) => Math.Max(m, s.T1));
        var earlyCut = Math.Min(play.Dur, lastEvent + 0.6);
        Assert.True(judged!.Value > earlyCut,
            $"判定時刻 {judged.Value:F2}s が早切り {earlyCut:F2}s より前になっている（この回帰テストの前提が崩れた）");

        // 判定窓を含めた打ち切り時刻なら、窓の全体（T1〜T1+hold）が再生範囲に入る。
        var end = Math.Max(earlyCut, judged.Value + Hold);
        Assert.NotNull(PlaybackEvaluator.JudgementAt(play, judged.Value, Hold));
        Assert.NotNull(PlaybackEvaluator.JudgementAt(play, end - 0.01, Hold));
    }

    // ── 実エンジン出力から内野ゴロ（アウト＋ゴロ軌道）のタイムラインを1つ採取する ──
    // 構成は PlayTimelineAdapterTests と同じ最小チーム＋固定シード（決定論）。
    private static PlayTimeline CollectGroundOut()
    {
        for (var seed = 1UL; seed <= 400; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));

            foreach (var e in r.Log)
            {
                if (e.Timeline is null || e.Result != PlateAppearanceResult.InPlayOut) continue;
                var tl = e.Timeline;
                if (!tl.Ball.Any(b => b.Kind == BallSegmentKind.Roll)) continue;      // ゴロだけ
                if (!tl.Ball.Any(b => b.Kind == BallSegmentKind.Throw)) continue;     // 一塁送球が絡む内野ゴロ
                if (tl.Runners.Any(x => x.OutAtEnd)) return tl;
            }
        }

        throw new InvalidOperationException("内野ゴロアウトのタイムラインが採取できなかった");
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
            Bullpen = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }
}
