using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 拾い直せず確定した捕球ミス（issue #816）の絵と判定の時計（issue #1293）。
/// 判定は拾い直しを終えた時刻（<see cref="FieldingPlay.CatchErrorReadyAtSeconds"/>）から本塁へ投げる前提で刺す。
/// 絵も、判定が引いたこぼれ幅（<see cref="FieldingPlay.CatchErrorScatterM"/>）で球を転がし、
/// 拾い直した地点・時刻から本塁へ投げる。表示専用・乱数不使用。
/// </summary>
public sealed class CatchErrorHomeThrowPictureTests
{
    private static readonly FieldGeometry Field = new();
    private const double Contact = TimelineBuilder.ContactAt;

    private static FieldingPlay CatchError(double scatterM, double readyAfterFielded)
    {
        var p = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        return p with
        {
            CatchErrorScatterM = scatterM,
            CatchErrorReadyAtSeconds = p.FieldedAtSeconds + readyAfterFielded,
        };
    }

    private static PlayTimeline Build(FieldingPlay play)
        => TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));

    private static double Dist(TimelinePoint a, TimelinePoint b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return System.Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>絵の転がりは判定のこぼれ幅で、止まるのは拾い直しの時刻（固定の3m・0.8秒ではない）。</summary>
    [Fact]
    public void Picture_UsesJudgedScatterAndReadyTime()
    {
        var play = CatchError(scatterM: 1.2, readyAfterFielded: 0.55);
        var tl = Build(play);

        var roll = tl.Ball.Last(b => b.Kind == BallSegmentKind.Roll);
        Assert.InRange(Dist(roll.From, roll.To), 1.15, 1.25);
        Assert.Equal(Contact + play.CatchErrorReadyAtSeconds!.Value, roll.T1, 6);
        Assert.Equal(roll.T1, tl.ErrorPickupAt!.Value, 6);
        Assert.True(Dist(tl.ErrorPickupPoint!.Value, roll.To) < 0.01, "拾い直しの地点が球の止まった位置でない");
    }

    /// <summary>
    /// 本塁で刺す送球は、拾い直した地点・時刻から出る（弾いた瞬間・弾いた地点からではない）。
    /// 弾いた地点から出ると、3m先へ転がる球と本塁へ向かう送球が同時に存在していた。
    /// </summary>
    [Theory]
    [InlineData(1.2, 0.55, true)]
    [InlineData(2.5, 0.80, true)]
    [InlineData(2.5, 0.80, false)]
    public void BackHomeThrow_StartsFromTheRecoveredBallAtTheReadyTime(double scatterM, double readyAfterFielded, bool runnerOut)
    {
        var play = CatchError(scatterM, readyAfterFielded);
        var baseTl = Build(play);
        var pickAt = baseTl.ErrorPickupAt!.Value;
        var pickPoint = baseTl.ErrorPickupPoint!.Value;
        var ballBefore = baseTl.Ball.Count;

        var tl = TimelineBuilder.AppendBackHomeThrows(baseTl, play, Field, homeArriveAnchor: pickAt + 3.0, runnerOut);

        var added = tl.Ball.Skip(ballBefore).ToList();
        Assert.NotEmpty(added);
        var first = added[0];
        Assert.Equal(BallSegmentKind.Throw, first.Kind);
        Assert.True(first.T0 >= pickAt - 1e-9, $"拾い直す前に投げている（T0={first.T0:F3} < 拾い直し {pickAt:F3}）");
        Assert.True(Dist(first.From, pickPoint) < 0.01, "送球の起点が拾い直した地点でない");
        Assert.Equal(play.FielderRole, first.FromRole);
        // 転がる球と送球が同時に存在しない
        var roll = baseTl.Ball.Last(b => b.Kind == BallSegmentKind.Roll);
        Assert.True(first.T0 >= roll.T1 - 1e-9, "球が止まる前に送球が始まっている");
    }

    /// <summary>拾い直しの判定を持たない失策（フライ落球等）は従来どおり弾いた地点・時刻から（回帰）。</summary>
    [Fact]
    public void ErrorWithoutRecoveryClock_KeepsTheFixedDeflect()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };
        var tl = Build(play);

        var roll = tl.Ball.Last(b => b.Kind == BallSegmentKind.Roll);
        Assert.InRange(Dist(roll.From, roll.To), 2.9, 3.1);
        Assert.Equal(0.8, roll.T1 - roll.T0, 6);
    }

    /// <summary>追いかけは弾いた後に始まり、拾い直しの時刻に着く。</summary>
    [Theory]
    [InlineData(0.3, 0.32)]
    [InlineData(1.2, 0.55)]
    [InlineData(4.0, 1.4)]
    public void ChaseLeg_StartsAfterTheFumbleAndEndsAtThePickup(double scatterM, double readyAfterFielded)
    {
        var play = CatchError(scatterM, readyAfterFielded);
        var tl = Build(play);
        var chase = tl.Moves.Where(m => m.Role == play.FielderRole && m.Task == FielderTask.Chase).ToList();
        Assert.NotEmpty(chase);
        var leg = chase[0];
        Assert.True(leg.T1 >= leg.T0);
        Assert.True(leg.T0 >= play.FieldedAtSeconds!.Value - 1e-9, "弾く前に追いかけ始めている");
        Assert.Equal(play.CatchErrorReadyAtSeconds!.Value + Contact, leg.T1, 6);
    }
}
