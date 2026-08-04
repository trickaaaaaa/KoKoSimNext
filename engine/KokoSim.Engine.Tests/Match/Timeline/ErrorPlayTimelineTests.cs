using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// エラー出塁の表示（issue #439）: 「完璧な送球が先着した絵＋判定表示なし」で黙って出塁しない。
/// ・判定イベント「エラー」がエラー確定の瞬間（捕球エラー＝弾いた時刻／悪送球＝送球到達時刻）に積まれる
/// ・捕球エラーは送球を描かず弾く絵、悪送球は一塁を逸れる絵（正着する送球＝ToBase="first" を描かない）
/// </summary>
public sealed class ErrorPlayTimelineTests
{
    private const double ContactAt = 0.85;   // TimelineBuilder.ContactAt の写し（private のため）

    private static PlayTimeline Build(FieldingPlay play)
    {
        var field = new FieldGeometry();
        return TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
    }

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // 捕球エラー（お手玉）: 送球・刺殺運びを描かず、処理点から打球の進行方向へ弾く絵になる。
    // 判定「エラー」は弾いた時刻（捕球時刻）に出る。
    [Fact]
    public void CatchError_DeflectsBall_NoThrow_JudgesAtBobble()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(
                FieldPosition.SecondBase, fieldedX: 9.0, fieldedZ: 30.0,
                fieldedAtSeconds: 1.4, throwArriveSeconds: 3.0, batterToFirstSeconds: 4.2,
                result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var tl = Build(play);

        Assert.DoesNotContain(tl.Ball, b => b.Kind is BallSegmentKind.Throw or BallSegmentKind.Carry);
        // 弾く絵: 捕球時刻から始まる短い転がり。終点は処理点から数m先（本塁→処理点の延長）。
        var fieldedAt = ContactAt + 1.4;
        var deflect = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Roll && b.T0 == fieldedAt);
        Assert.Equal(3.0, Dist(deflect.From, deflect.To), 3);

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Error, j.Kind);
        Assert.Equal(fieldedAt, j.T, 6);

        // ① こぼした本人（処理野手）が弾いた球を追いかけて拾い直す（issue #439 追記）
        var chase = Assert.Single(tl.Moves, m => m.Task == FielderTask.Chase);
        Assert.Equal(FieldPosition.SecondBase, chase.Role);
        Assert.Equal(fieldedAt + 0.35, chase.T0, 6);
        Assert.Equal(0.0, Dist(chase.To, deflect.To), 3);       // 行き先＝球が止まる点
        Assert.True(chase.T1 >= deflect.T1, "球が止まる前に拾い終わっている");
    }

    // 悪送球: 送球は描くが一塁に正着しない（ToBase 無し・受け手無し・終点はベースの先）。
    // 判定「エラー」は送球が届くはずだった時刻に出る。
    [Fact]
    public void ThrowError_ThrowMissesFirstBase_JudgesAtArrival()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(
                FieldPosition.SecondBase, fieldedX: 9.0, fieldedZ: 30.0,
                fieldedAtSeconds: 1.4, throwArriveSeconds: 3.0, batterToFirstSeconds: 4.2,
                result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Throw };
        var tl = Build(play);

        var field = new FieldGeometry();
        var first = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);
        var thrown = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        Assert.Null(thrown.ToBase);
        Assert.Null(thrown.ToRole);
        Assert.Equal(6.0, Dist(thrown.To, first), 3);   // ベースの6m先へ抜ける

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Error, j.Kind);
        Assert.Equal(ContactAt + 3.0, j.T, 6);

        // ① 逸れた球は一塁カバー（処理が二塁手なので一塁手）が振り返って追いかける（issue #439 追記）
        var chase = Assert.Single(tl.Moves, m => m.Task == FielderTask.Chase);
        Assert.Equal(FieldPosition.FirstBase, chase.Role);
        Assert.Equal(ContactAt + 3.0 + 0.35, chase.T0, 6);      // 届くはずだった時刻＋反応
        Assert.Equal(0.0, Dist(chase.To, thrown.To), 3);        // 行き先＝球が止まる点
        Assert.True(chase.T1 >= thrown.T1, "球が止まる前に拾い終わっている");
    }

    // 落球（フライの捕球エラー）: 判定「エラー」が捕球を試みた瞬間に出る。
    [Fact]
    public void DroppedFly_JudgesErrorAtCatchAttempt()
    {
        var play = FieldingPlayFixtures.CaughtFly(
                FieldPosition.CenterField, hangTimeSeconds: 3.5, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var tl = Build(play);

        var j = Assert.Single(tl.Judgements, x => x.Kind == JudgementKind.Error);
        Assert.Equal(ContactAt + 3.5, j.T, 6);
    }

    // 従来のアウト・内野安打の絵は不変（エラー分岐の追加でフォース送球・3U運びが消えていないこと）。
    [Fact]
    public void NonErrorPlays_KeepThrowAndCarryPictures()
    {
        var outPlay = FieldingPlayFixtures.InfieldGrounder(FieldPosition.SecondBase);
        Assert.Single(Build(outPlay).Ball, b => b.Kind == BallSegmentKind.Throw && b.ToBase == "first");

        var unassisted = FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.FirstBase, fieldedX: 20.0, fieldedZ: 26.0, unassistedPutout: true);
        Assert.Single(Build(unassisted).Ball, b => b.Kind == BallSegmentKind.Carry && b.ToBase == "first");
    }
}
