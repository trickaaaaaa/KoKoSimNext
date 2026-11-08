using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// エラーのテロップの出る時刻（issue #806）。
/// ・テロップは「打者が一塁でセーフになった瞬間」に出る（記録として失策が確定する瞬間）
/// ・ただし「！」マーク（取り損ない＋<see cref="JudgementEvent.ErrorMarkDelaySeconds"/>）より前には出さない
///   ＝打者が先に一塁を越えている落球などは「！」と同時
/// ・判定の物理時刻 T（取り損ないの瞬間＝「！」マーク・寄りカメラの起点）は動かさない
/// </summary>
public sealed class ErrorTelopTimingTests
{
    private static PlayTimeline Build(FieldingPlay play)
    {
        var field = new FieldGeometry();
        return TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
    }

    private static double BatterArrive(FieldingPlay play) => TimelineBuilder.ContactAt + play.BatterToFirstSeconds;

    private static double Expected(double errAt, FieldingPlay play)
        => Math.Max(BatterArrive(play), errAt + JudgementEvent.ErrorMarkDelaySeconds);

    [Fact]
    public void CatchError_TelopAtBatterReachingFirst_MarkTimeUnchanged()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(
                FieldPosition.SecondBase, fieldedX: 9.0, fieldedZ: 30.0,
                fieldedAtSeconds: 1.4, throwArriveSeconds: 3.0, batterToFirstSeconds: 4.2,
                result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var j = Assert.Single(Build(play).Judgements, x => x.Kind == JudgementKind.Error);

        var errAt = TimelineBuilder.ContactAt + 1.4;
        Assert.Equal(errAt, j.T, 6);                         // 「！」の起点は弾いた瞬間のまま
        Assert.Equal(BatterArrive(play), j.DisplayT!.Value, 6);   // テロップは打者の一塁到達
        Assert.Equal(Expected(errAt, play), j.DisplayT!.Value, 6);
    }

    [Fact]
    public void ThrowError_TelopAtBatterReachingFirst_MarkTimeUnchanged()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(
                FieldPosition.SecondBase, fieldedX: 9.0, fieldedZ: 30.0,
                fieldedAtSeconds: 1.4, throwArriveSeconds: 3.0, batterToFirstSeconds: 4.2,
                result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Throw };
        var j = Assert.Single(Build(play).Judgements, x => x.Kind == JudgementKind.Error);

        var errAt = TimelineBuilder.ContactAt + 3.0;
        Assert.Equal(errAt, j.T, 6);
        Assert.Equal(BatterArrive(play), j.DisplayT!.Value, 6);
    }

    /// <summary>トンネル（ErrorAtSeconds＝抜かれた瞬間）も同じ規則。</summary>
    [Fact]
    public void Tunnel_TelopAtBatterReachingFirst_MarkTimeAtBeaten()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(
                FieldPosition.Shortstop, fieldedX: -8.0, fieldedZ: 32.0,
                fieldedAtSeconds: 4.0, throwArriveSeconds: 5.0, batterToFirstSeconds: 4.2,
                result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch, ErrorAtSeconds = 1.1 };
        var j = Assert.Single(Build(play).Judgements, x => x.Kind == JudgementKind.Error);

        var errAt = TimelineBuilder.ContactAt + 1.1;
        Assert.Equal(errAt, j.T, 6);
        Assert.Equal(Expected(errAt, play), j.DisplayT!.Value, 6);
        Assert.Equal(BatterArrive(play), j.DisplayT!.Value, 6);
    }

    /// <summary>外野の落球: 打者は落球の時点で一塁を越えている＝テロップは「！」と同時（取り損ない＋0.5秒）。</summary>
    [Fact]
    public void DroppedFly_BatterAlreadyPastFirst_TelopWithTheMark()
    {
        var play = FieldingPlayFixtures.CaughtFly(
                FieldPosition.CenterField, hangTimeSeconds: 5.0, batterToFirstSeconds: 4.2,
                result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var j = Assert.Single(Build(play).Judgements, x => x.Kind == JudgementKind.Error);

        var errAt = TimelineBuilder.ContactAt + 5.0;
        Assert.True(BatterArrive(play) < errAt, "前提: 打者が先に一塁へ着いている");
        Assert.Equal(errAt, j.T, 6);
        Assert.Equal(errAt + JudgementEvent.ErrorMarkDelaySeconds, j.DisplayT!.Value, 6);
    }

    /// <summary>
    /// 再生側（2D俯瞰・3D守備ビュー共通の <see cref="PlaybackEvaluator"/>）も表示時刻で出し分ける。
    /// 取り損ない＋0.5秒（旧来の出る時刻）ではまだ出ず、打者が一塁を踏んだ瞬間に出る。
    /// 早切りの終端（LastJudgementTime / LastJudgementEndTime）も表示時刻に揃う。
    /// </summary>
    [Fact]
    public void Playback_ShowsErrorAtDisplayTime_AndCutoffFollowsIt()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(
                FieldPosition.SecondBase, fieldedX: 9.0, fieldedZ: 30.0,
                fieldedAtSeconds: 1.4, throwArriveSeconds: 3.0, batterToFirstSeconds: 4.2,
                result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var pb = PlayTimelineAdapter.ToPlaybackPlay(Build(play));
        var errAt = TimelineBuilder.ContactAt + 1.4;
        var disp = BatterArrive(play);
        const double hold = 0.6;

        Assert.Null(PlaybackEvaluator.JudgementAt(pb, errAt + JudgementEvent.ErrorMarkDelaySeconds + 0.01, hold));
        var shown = PlaybackEvaluator.JudgementAt(pb, disp + 0.01, hold);
        Assert.NotNull(shown);
        Assert.Equal(JudgementKind.Error, shown!.Value.Kind);
        Assert.Equal(disp, shown.Value.T1, 6);

        Assert.Equal(disp, PlaybackEvaluator.LastJudgementTime(pb)!.Value, 6);
        Assert.Equal(disp + hold, PlaybackEvaluator.LastJudgementEndTime(pb, hold)!.Value, 6);

        // 物理時刻は再生データでも不変（「！」マークの起点）
        var pj = Assert.Single(pb.Judgements, x => x.Kind == JudgementKind.Error);
        Assert.Equal(errAt, pj.T, 6);
    }

    /// <summary>エラー以外の判定は DisplayT を持たない＝表示時刻は従来どおり T。</summary>
    [Fact]
    public void NonErrorJudgements_HaveNoDisplayTime()
    {
        var tl = Build(FieldingPlayFixtures.InfieldGrounder(FieldPosition.SecondBase));
        Assert.All(tl.Judgements, j => Assert.Null(j.DisplayT));
    }
}
