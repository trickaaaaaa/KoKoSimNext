using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 一塁駆け抜け（オーバーラン, issue #443）: 内野ゴロで一塁へ向かう打者走者は、塁上で即停止せず
/// 塁の数m先まで走り抜けてから減速して止まる絵になる。表示専用の追加レッグ＝判定・帯には無関係
/// （不変条件#2）。<see cref="TimelineBuilder"/> が "打" ラベルの主到達レッグの後ろに続けてもう1本
/// 積む契約を検証する（Unity側は同じ Label の PlaybackRunner として1本の走者に合成する）。
/// </summary>
public sealed class FirstBaseOverrunTests
{
    private static readonly FieldGeometry Field = new();

    private static PlayTimeline Build(FieldingPlay play)
        => TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play));

    [Theory]
    [InlineData(BattedBallResult.Out)]
    [InlineData(BattedBallResult.Single)]
    [InlineData(BattedBallResult.Error)]
    public void InfieldGrounder_AppendsOverrunLeg_AfterPrimaryArrival_RegardlessOfOutcome(BattedBallResult result)
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: result);
        var tl = Build(play);

        var legs = tl.Runners.Where(r => r.Label == "打").ToList();
        Assert.Equal(2, legs.Count);

        var primary = legs[0];
        var overrun = legs[1];

        // 主到達レッグは従来どおり本塁→一塁・T1=真の到達時刻。
        var first = new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z);
        Assert.Equal(first.X, primary.To.X, 6);
        Assert.Equal(first.Z, primary.To.Z, 6);

        // オーバーランは主到達レッグの終端でシームレスに始まる（ワープしない）。
        Assert.Equal(primary.T1, overrun.T0, 9);
        Assert.Equal(primary.To.X, overrun.From.X, 9);
        Assert.Equal(primary.To.Z, overrun.From.Z, 9);
        Assert.True(overrun.T1 > overrun.T0, "オーバーランの尺が0以下");

        // アウト/セーフを問わずオーバーランは常に足す（一塁到達は際どさに関係なく常に駆け抜け）。
        Assert.False(overrun.OutAtEnd);
        Assert.False(overrun.CloseCall);
    }

    [Fact]
    public void OverrunLeg_ContinuesStraightPastFirstBase_ByTheOverrunDistance()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop);
        var tl = Build(play);
        var legs = tl.Runners.Where(r => r.Label == "打").ToList();
        var primary = legs[0];
        var overrun = legs[1];

        // 本塁→一塁の方向ベクトルと、一塁→オーバーラン終端の方向ベクトルが一致（直進・ターンしない）。
        var approachDx = primary.To.X - primary.From.X;
        var approachDz = primary.To.Z - primary.From.Z;
        var approachLen = Math.Sqrt(approachDx * approachDx + approachDz * approachDz);

        var overrunDx = overrun.To.X - overrun.From.X;
        var overrunDz = overrun.To.Z - overrun.From.Z;
        var overrunLen = Math.Sqrt(overrunDx * overrunDx + overrunDz * overrunDz);

        Assert.Equal(approachDx / approachLen, overrunDx / overrunLen, 6);
        Assert.Equal(approachDz / approachLen, overrunDz / overrunLen, 6);

        // 「数m」= 3.5m（design comment と一致）。
        Assert.Equal(3.5, overrunLen, 3);
    }

    [Fact]
    public void OverrunLeg_Decelerates_SlowerThanTheApproachLeg()
    {
        // オーバーランの平均速度は接近レッグの半分（等減速で停止する運動）＝必ず接近より遅い。
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop);
        var tl = Build(play);
        var legs = tl.Runners.Where(r => r.Label == "打").ToList();
        var primary = legs[0];
        var overrun = legs[1];

        var approachSpeed = Field.BaseDistanceM / (primary.T1 - primary.T0);
        var overrunSpeed = 3.5 / (overrun.T1 - overrun.T0);
        Assert.True(overrunSpeed < approachSpeed, $"オーバーランが接近より速い: {overrunSpeed} vs {approachSpeed}");
        Assert.True(overrunSpeed > 0.0);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(3.7)]
    [InlineData(4.5)]
    public void OverrunLeg_DoesNotChangeThePrimaryArrivalTime_AcrossApproachSpeeds(double batterToFirstSeconds)
    {
        // オーバーランを足しても主到達レッグの時刻（＝判定に使われた値そのもの）は不変（表示追加のみ・帯不変）。
        var play = FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.Shortstop, batterToFirstSeconds: batterToFirstSeconds);
        var tl = Build(play);
        var legs = tl.Runners.Where(r => r.Label == "打").ToList();
        Assert.Equal(2, legs.Count);
        Assert.Equal(0.85 + batterToFirstSeconds, legs[0].T1, 9);
    }

    [Fact]
    public void FlyBallOut_DoesNotGetAnOverrunLeg()
    {
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField);
        var tl = Build(play);
        Assert.Single(tl.Runners, r => r.Label == "打");
    }

    [Fact]
    public void OutfieldHit_DoesNotGetAnOverrunLeg_FromBuildBattedBallAlone()
    {
        // 外野安打は一塁を回って進塁しうる＝ AppendRunnerLegs が続きを描く別経路。
        // BuildBattedBall 単体では主到達レッグ1本のまま（重複してオーバーランを足さない）。
        var play = FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: 78.0, fieldedAtSeconds: 3.4);
        var tl = Build(play);
        Assert.Single(tl.Runners, r => r.Label == "打");
    }

    [Fact]
    public void BuildBattedBall_IsDeterministic_WithOverrunLeg()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop);
        var a = Build(play);
        var b = Build(play);

        var legsA = a.Runners.Where(r => r.Label == "打").ToList();
        var legsB = b.Runners.Where(r => r.Label == "打").ToList();
        Assert.Equal(legsA.Count, legsB.Count);
        for (var i = 0; i < legsA.Count; i++)
        {
            Assert.Equal(legsA[i].T0, legsB[i].T0, 9);
            Assert.Equal(legsA[i].T1, legsB[i].T1, 9);
            Assert.Equal(legsA[i].To.X, legsB[i].To.X, 9);
            Assert.Equal(legsA[i].To.Z, legsB[i].To.Z, 9);
        }
    }
}
