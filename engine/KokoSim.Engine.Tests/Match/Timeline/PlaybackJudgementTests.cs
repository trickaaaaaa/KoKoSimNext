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

    private static PlaybackJudgement J(double t, bool isOut, bool closeCall = false, string label = "打") =>
        new(t, isOut ? JudgementKind.Out : JudgementKind.Safe, closeCall, label);

    private static PlaybackJudgement JFair(double t, string label = "打") =>
        new(t, JudgementKind.Fair, false, label);

    private static PlaybackPlay PlayWith(params PlaybackJudgement[] judgements) => new()
    {
        Name = "test", Dur = 10, Result = "test", ResAt = 1,
        Ball = Array.Empty<PlaybackBallSegment>(),
        Judgements = judgements,
    };

    [Fact]
    public void JudgementAt_ShowsOut_OnlyInsideHoldWindow()
    {
        var play = PlayWith(J(4.0, isOut: true));

        Assert.Null(PlaybackEvaluator.JudgementAt(play, 3.99, Hold));           // 判定前は出さない
        Assert.Equal(JudgementKind.Out, PlaybackEvaluator.JudgementAt(play, 4.0, Hold)!.Value.Kind);  // 確定の瞬間から
        Assert.Equal(JudgementKind.Out, PlaybackEvaluator.JudgementAt(play, 4.59, Hold)!.Value.Kind); // hold の直前まで
        Assert.Null(PlaybackEvaluator.JudgementAt(play, 4.61, Hold));           // hold 経過で消える
    }

    [Fact]
    public void JudgementAt_ReturnsNull_WhenNoJudgements()
    {
        // 判定イベントが無いプレー（生還・進塁だけ）は判定を出さない＝表示のゲートはデータ側。
        var play = PlayWith();
        Assert.Null(PlaybackEvaluator.JudgementAt(play, 4.0, Hold));
        Assert.Null(PlaybackEvaluator.LastJudgementTime(play));
    }

    [Fact]
    public void JudgementAt_ShowsSafe_OnCloseCallOnly()
    {
        var play = PlayWith(J(4.0, isOut: false, closeCall: true));
        var j = PlaybackEvaluator.JudgementAt(play, 4.2, Hold);
        Assert.NotNull(j);
        Assert.Equal(JudgementKind.Safe, j!.Value.Kind);   // 際どい判定＝セーフ表示
        Assert.Equal(4.0, j.Value.T1, 6);
    }

    [Fact]
    public void JudgementAt_ShowsFair_OnCloseDroppedFly()
    {
        // Issue #328②: 野手が追いついていた際どい飛球が落ちた瞬間は「フェア」を表示する。
        var play = PlayWith(JFair(4.0));
        var j = PlaybackEvaluator.JudgementAt(play, 4.2, Hold);
        Assert.NotNull(j);
        Assert.Equal(JudgementKind.Fair, j!.Value.Kind);
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
            Judgements = new[] { J(3.0, isOut: true, label: "走1"), J(4.0, isOut: true, label: "打") },
        };

        Assert.Equal(3.0, PlaybackEvaluator.JudgementAt(play, 3.4, Hold)!.Value.T1, 6);
        Assert.Equal(4.0, PlaybackEvaluator.JudgementAt(play, 4.0, Hold)!.Value.T1, 6);
        Assert.Equal(4.0, PlaybackEvaluator.LastJudgementTime(play)!.Value, 6);
    }

    [Fact]
    public void JudgementAt_FlagsSecondOut_AsAdditionalOut_OnDoublePlay()
    {
        // issue double-play-throw-pace-and-second-out-call ③案F（2026-08-27監督判断＝「ダブルプレー」）。
        // 1つ目（走1・二塁封殺）は通常の「アウト」のまま、2つ目（打・一塁封殺）だけ IsAdditionalOut=true。
        var play = new PlaybackPlay
        {
            Name = "dp", Dur = 10, Result = "dp", ResAt = 4,
            Ball = Array.Empty<PlaybackBallSegment>(),
            Judgements = new[] { J(3.0, isOut: true, label: "走1"), J(4.0, isOut: true, label: "打") },
        };

        Assert.False(PlaybackEvaluator.JudgementAt(play, 3.4, Hold)!.Value.IsAdditionalOut);
        Assert.True(PlaybackEvaluator.JudgementAt(play, 4.0, Hold)!.Value.IsAdditionalOut);
    }

    [Fact]
    public void JudgementAt_SingleOut_IsNotAdditionalOut()
    {
        var play = PlayWith(J(4.0, isOut: true));
        Assert.False(PlaybackEvaluator.JudgementAt(play, 4.0, Hold)!.Value.IsAdditionalOut);
    }

    [Fact]
    public void JudgementAt_SecondSafe_IsNotAdditionalOut()
    {
        // アウトでない判定（セーフ等）には適用しない。1つ目がアウト・2つ目がセーフでも false のまま。
        var play = new PlaybackPlay
        {
            Name = "fc", Dur = 10, Result = "fc", ResAt = 4,
            Ball = Array.Empty<PlaybackBallSegment>(),
            Judgements = new[]
            {
                J(3.0, isOut: true, label: "走1"),
                J(4.0, isOut: false, closeCall: true, label: "打"),
            },
        };

        Assert.False(PlaybackEvaluator.JudgementAt(play, 4.0, Hold)!.Value.IsAdditionalOut);
    }

    [Fact]
    public void LastJudgementTime_ScalesWindowWithHold()
    {
        // 倍速再生では hold をプレー時間軸で広げる（実時間の見え方を一定にする）。窓の始点は変わらない。
        var play = PlayWith(J(4.0, isOut: true));
        Assert.Null(PlaybackEvaluator.JudgementAt(play, 5.5, Hold));            // ×1 では消えている
        Assert.NotNull(PlaybackEvaluator.JudgementAt(play, 5.5, Hold * 4));     // ×4 なら まだ出ている
    }

    /// <summary>
    /// issue #236 症状A の回帰防止 → issue #328①でさらに強化: 内野ゴロのフォースアウトの判定時刻は
    /// 送球到達（Throwセグメントの終端そのもの）になったため、早切り（結果確定＋最後のボール事象＋0.6秒）が
    /// 判定表示窓の始点より前に来ることは構造的に無くなった（判定は必ずボール事象の内側で確定する）。
    /// </summary>
    [Fact]
    public void GroundOut_JudgementNeverAfterDeadOutEarlyCut()
    {
        var tl = CollectGroundOut();
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);

        var judged = PlaybackEvaluator.LastJudgementTime(play);
        Assert.NotNull(judged);   // 内野ゴロは打者走者レッグに OutAtEnd が立つ

        // 旧 PlayEndTime（早切り）＝ min(Dur, max(ResAt, ボール終端) + 0.6)。
        var lastEvent = play.Ball.Aggregate(play.ResAt, (m, s) => Math.Max(m, s.T1));
        var earlyCut = Math.Min(play.Dur, lastEvent + 0.6);
        Assert.True(judged!.Value <= earlyCut,
            $"判定時刻 {judged.Value:F2}s が早切り {earlyCut:F2}s より後になっている（送球到達に同期しているはず, Issue #328①）");

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
                // 単純な内野ゴロ（打者走者を一塁で封殺）だけを採る。併殺（一塁走者を二塁で封殺＝Issue #369）は
                // 2本目送球（二塁→一塁の転送）が判定窓の後ろへ伸びる別ケースで、この「早切り vs 一塁フォースアウトの
                // 判定窓」不変条件の対象外（2D再生の併殺演出は AppendDoublePlayThrows 側で別途担保）。
                if (!tl.Runners.Any(x => x.OutAtEnd && x.Label == "打")) continue;
                if (tl.Runners.Any(x => x.OutAtEnd && x.Label != "打")) continue;
                return tl;
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
            Reserves = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }
}
