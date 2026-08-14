using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 失策の絵（issue #629 ③④）。
///
/// <para>③ 拾った野手が<b>棒立ち</b>だった: 追いかけレッグは球のところまで走って終わりで、返球が積まれて
/// いなかった＝走者が本塁まで走り抜けるのを眺めている絵になっていた。</para>
///
/// <para>④ <b>フライ落球には絵が1コマも無かった</b>: 失策球の処理は「送球がある内野ゴロ」の分岐の内側に
/// あり、フライ落球は <c>ThrowArriveSeconds</c> を持たないため一切通らなかった＝球が着地点に貼り付いて
/// 静止したまま走者だけが進む絵になっていた。</para>
///
/// <para>表示専用・乱数不使用（判定・帯には触れない）。</para>
/// </summary>
public sealed class ErrorBallPictureTests
{
    private static readonly FieldGeometry Field = new();

    private static PlayTimeline Build(FieldingPlay play, bool runnersOn = true)
        => TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn, TimelineBuilder.DescribeResult(play));

    private static double Dist(TimelinePoint a, TimelinePoint b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return System.Math.Sqrt(dx * dx + dz * dz);
    }

    // --- ④ フライ落球にも「落とす→転がる→拾い直す」の絵がある ---

    /// <summary>外野フライの落球で、球が着地点に貼り付いたままにならない。</summary>
    [Fact]
    public void DroppedFly_BallRollsAwayAndIsPickedUp()
    {
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var tl = Build(play);

        var landing = new TimelinePoint(play.LandingX, play.LandingZ);
        var roll = tl.Ball.LastOrDefault(b => b.Kind == BallSegmentKind.Roll);
        Assert.True(roll is not null, "落球した球が転がらない（着地点に貼り付いている）");
        Assert.True(Dist(roll!.From, landing) < 0.5, "転がりの起点が落球地点でない");
        Assert.True(Dist(roll.To, landing) > 1.0, "落球した球がその場から動いていない");

        // 落とした本人が拾いに行く（追いかけレッグ）。
        var chase = tl.Moves.Where(m => m.Role == play.FielderRole && m.Task == FielderTask.Chase).ToList();
        Assert.True(chase.Count > 0, "落とした野手が拾いに行かない（棒立ち）");
        Assert.True(Dist(chase[^1].To, roll.To) < 0.5, "追いかける先が球の止まった位置と違う");

        // 拾い上げる瞬間が明示的に載る（返球を積む側が使う）。
        Assert.NotNull(tl.ErrorPickupAt);
        Assert.Equal(play.FielderRole, tl.ErrorPickupRole);
        Assert.True(tl.ErrorPickupAt >= roll.T1, "球が止まる前に拾い上げている");
        Assert.True(tl.Duration >= tl.ErrorPickupAt, "尺が拾い上げより短い");
    }

    /// <summary>捕球された（＝失策でない）フライは従来どおり転がらない（回帰）。</summary>
    [Fact]
    public void CaughtFly_DoesNotRollOrChase()
    {
        var tl = Build(FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField));

        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Roll);
        Assert.Null(tl.ErrorPickupAt);
    }

    // --- ③ 拾ったら内野へ投げ返す ---

    /// <summary>内野のお手玉: 拾い直した野手が投手へ返球する（棒立ちにしない）。</summary>
    [Fact]
    public void InfieldCatchError_ReturnThrowGoesToThePitcher()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var tl = TimelineBuilder.AppendErrorReturnThrow(Build(play), play, Field);

        var back = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).OrderBy(b => b.T1).Last();
        Assert.True(Dist(back.To, new TimelinePoint(0, 18.44)) < 1.0,
            $"内野で拾った球の返球先がマウンドでない（({back.To.X:F1},{back.To.Z:F1})）");
        Assert.Equal(FieldPosition.Pitcher, back.ToRole);
        Assert.True(back.T0 >= tl.ErrorPickupAt, "拾う前に投げている");
        Assert.True(tl.Duration >= back.T1, "尺が返球到達より短い");
    }

    /// <summary>外野のフライ落球: 二塁へ投げ返す（内野へ戻す）。</summary>
    [Fact]
    public void OutfieldDroppedFly_ReturnThrowGoesToSecond()
    {
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var tl = TimelineBuilder.AppendErrorReturnThrow(Build(play), play, Field);

        var back = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).OrderBy(b => b.T1).Last();
        Assert.True(Dist(back.To, new TimelinePoint(Field.SecondBase.X, Field.SecondBase.Z)) < 1.0,
            "外野で拾った球の返球先が二塁でない");
        Assert.Equal("second", back.ToBase);
    }

    /// <summary>投手のお手玉は自分からマウンドへ投げない（拾った本人がそこに居る＝そのまま持っている）。</summary>
    [Fact]
    public void PitcherCatchError_DoesNotThrowToItself()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(
                FieldPosition.Pitcher, fieldedX: 0.0, fieldedZ: 19.0, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var before = Build(play);
        var after = TimelineBuilder.AppendErrorReturnThrow(before, play, Field);

        Assert.NotNull(before.ErrorPickupAt);   // 拾い直しの絵は出る
        Assert.Equal(before.Ball.Count, after.Ball.Count);
    }

    /// <summary>失策でないプレーには返球を足さない（恒等）。</summary>
    [Fact]
    public void NonErrorPlay_AppendErrorReturnThrow_IsIdentity()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop);
        var before = Build(play);
        var after = TimelineBuilder.AppendErrorReturnThrow(before, play, Field);

        Assert.Equal(before.Ball.Count, after.Ball.Count);
        Assert.Equal(before.Duration, after.Duration);
    }

    /// <summary>悪送球でも拾い直して返球する（逸れた球を一塁カバーが追って内野へ戻す）。</summary>
    [Fact]
    public void ErrantThrow_IsChasedAndReturned()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Throw };
        var tl = TimelineBuilder.AppendErrorReturnThrow(Build(play), play, Field);

        Assert.NotNull(tl.ErrorPickupAt);
        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).OrderBy(b => b.T1).ToList();
        Assert.True(throws.Count >= 2, "逸れた送球のあとに返球が無い");
        Assert.True(throws[^1].T0 >= tl.ErrorPickupAt, "拾う前に投げ返している");
    }
}
