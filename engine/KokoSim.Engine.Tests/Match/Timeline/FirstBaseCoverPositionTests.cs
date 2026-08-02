using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 一塁カバーの立ち位置（Issue #419）。ベース中心へまたぐ絵をやめ、送球元（実捕球点）側へ
/// 寄せた「踵をベース縁に付けた」立ち位置にする。表示専用・判定/帯には触れない。
/// </summary>
public sealed class FirstBaseCoverPositionTests
{
    private static FieldingPlay GroundOutAt(FieldPosition fielderRole, double fieldedX, double fieldedZ)
        => FieldingPlayFixtures.InfieldGrounder(
            fielderRole, fieldedX: fieldedX, fieldedZ: fieldedZ,
            fieldedAtSeconds: 1.0, throwArriveSeconds: 2.0);

    /// <summary>一塁への CoverBase 移動（このプレーで唯一のはず）。</summary>
    private static FielderMove FirstBaseCoverMove(PlayTimeline tl, FieldGeometry field)
    {
        var basePt = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);
        return tl.Moves.Single(m => m.Task == FielderTask.CoverBase
            && Distance(m.To, basePt) < 1.0);
    }

    private static double Distance(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    // ショート・サード・セカンドゴロの3場面（完了条件どおり）。ショートは静的表の CoverBase 経路
    // （TimelineBuilder.ResolveTarget）、サード/セカンドは押し出しが絡む動的注入経路
    // （TimelineBuilder.BuildBattedBall の Issue #257 ブロック）の両方を通す。
    [Theory]
    [InlineData(FieldPosition.Shortstop, -9.0, 30.0)]
    [InlineData(FieldPosition.ThirdBase, -17.0, 15.0)]
    [InlineData(FieldPosition.SecondBase, 12.0, 22.0)]
    public void FirstBaseCover_OffsetsTowardThrowOrigin_NotBaseCenter(
        FieldPosition fielderRole, double fieldedX, double fieldedZ)
    {
        var field = new FieldGeometry();
        var play = GroundOutAt(fielderRole, fieldedX, fieldedZ);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var basePt = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);
        var fieldPoint = new TimelinePoint(fieldedX, fieldedZ);
        var move = FirstBaseCoverMove(tl, field);

        // またいだまま（ベース中心ちょうど）ではなくなっている。
        var offsetDist = Distance(move.To, basePt);
        Assert.True(offsetDist > 0.05, $"一塁カバーがベース中心のまま（{offsetDist}m）");
        // 提案レンジ（issue本文 0.3〜0.5m）に収まっている。
        Assert.InRange(offsetDist, 0.2, 0.6);

        // 寄せた方向は送球元（実捕球点）を向いている＝体は送球が来る方向へ伸びる。
        var toMove = new TimelinePoint((move.To.X - basePt.X) / offsetDist, (move.To.Z - basePt.Z) / offsetDist);
        var toFieldDist = Distance(fieldPoint, basePt);
        var toField = new TimelinePoint((fieldPoint.X - basePt.X) / toFieldDist, (fieldPoint.Z - basePt.Z) / toFieldDist);
        var dot = toMove.X * toField.X + toMove.Z * toField.Z;
        Assert.True(dot > 0.999, $"寄せた方向が送球元を向いていない（dot={dot}）");
    }

    // 二塁・三塁・本塁のカバー、および一塁以外のプレーは従来どおりベース中心（完了条件どおり）。
    [Fact]
    public void OtherBaseCovers_StayExactlyAtBaseCenter()
    {
        var field = new FieldGeometry();
        // Bunt(range<15)・投手が処理: 捕手=本塁, 三塁手=三塁, 遊撃手=二塁 を静的表どおりカバーする。
        var play = GroundOutAt(FieldPosition.Pitcher, 0.0, 8.0);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var home = new TimelinePoint(0.0, 0.0);
        var second = new TimelinePoint(field.SecondBase.X, field.SecondBase.Z);
        var third = new TimelinePoint(field.ThirdBase.X, field.ThirdBase.Z);

        var catcherMove = Assert.Single(tl.Moves, m => m.Role == FieldPosition.Catcher && m.Task == FielderTask.CoverBase);
        Assert.Equal(home.X, catcherMove.To.X, 9);
        Assert.Equal(home.Z, catcherMove.To.Z, 9);

        var shortstopMove = Assert.Single(tl.Moves, m => m.Role == FieldPosition.Shortstop && m.Task == FielderTask.CoverBase);
        Assert.Equal(second.X, shortstopMove.To.X, 9);
        Assert.Equal(second.Z, shortstopMove.To.Z, 9);

        var thirdBaseMove = Assert.Single(tl.Moves, m => m.Role == FieldPosition.ThirdBase && m.Task == FielderTask.CoverBase);
        Assert.Equal(third.X, thirdBaseMove.To.X, 9);
        Assert.Equal(third.Z, thirdBaseMove.To.Z, 9);
    }
}
