using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// <see cref="FieldingResolver"/> が出す <see cref="FieldingPlay"/> の不変条件（Issue #315）。
///
/// 手書きフィクスチャの乖離そのものは型（nullable 化）と
/// <see cref="FieldingPlayFixtures"/> で防ぐ。こちらは**リゾルバ側の退行**を捕まえる別レイヤーで、
/// 「どの派生メンバーが揃って埋まるか」というフィクスチャの手本そのものを検証する。
/// ここが赤くなったら <see cref="FieldingPlayFixtures"/> も一緒に直すこと。
/// </summary>
public sealed class FieldingPlayInvariantTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    /// <summary>ファウル・本塁打・空中捕球・内野ゴロ・外野安打・内野を抜けるバウンドを一通り踏む掃引。</summary>
    private static IEnumerable<(FieldingPlay Play, string Case)> Sweep()
    {
        var coeff = new FieldingCoefficients();
        foreach (var bearing in new[] { -60.0, -30.0, -14.0, 0.0, 14.0, 30.0, 60.0 })
        foreach (var exit in new[] { 22.0, 30.0, 38.0, 45.0, 52.0, 58.0 })
        foreach (var launch in new[] { -20.0, -8.0, -2.0, 6.0, 16.0, 28.0, 40.0 })
        {
            var play = FieldingResolver.ResolveDetailed(
                new BattedBall { ExitVelocityMps = exit, LaunchAngleDeg = launch, BearingDeg = bearing },
                Field, Aero, BatterAttributes.LeagueAverage,
                Field.StandardAlignment(), coeff, new Xoshiro256Random(12345));
            yield return (play, $"bearing={bearing} exit={exit} launch={launch}");
        }
    }

    /// <summary>掃引が全経路を踏んでいること（不変条件が空振りで緑になっていないことの担保）。</summary>
    [Fact]
    public void Sweep_CoversEveryResolverPath()
    {
        int foul = 0, homeRun = 0, caughtFly = 0, infieldGrounder = 0, outfieldHit = 0, unsetFieldPoint = 0;
        foreach (var (p, _) in Sweep())
        {
            if (p.Result == BattedBallResult.Foul) foul++;
            else if (p.Result == BattedBallResult.HomeRun) homeRun++;
            else if (p.IsFly && p.FielderRole is not null && p.FieldedX is null) caughtFly++;
            else if (p.ThrowArriveSeconds is not null) infieldGrounder++;
            else if (p.FieldedX is not null) outfieldHit++;
            if (p.FieldedX is null) unsetFieldPoint++;
        }

        Assert.True(foul > 0, "ファウルが掃引に無い");
        Assert.True(homeRun > 0, "本塁打が掃引に無い");
        Assert.True(caughtFly > 0, "空中捕球が掃引に無い");
        Assert.True(infieldGrounder > 0, "内野ゴロ（一塁送球あり）が掃引に無い");
        Assert.True(outfieldHit > 0, "転がりを解いた安打が掃引に無い");
        // 「回収点が未設定」は本番でも普通に出る＝フォールバックは死んだ経路ではない（Issue #315）。
        Assert.True(unsetFieldPoint > 0, "回収点が未設定の出力が1件も無い（フォールバックが本番で踏まれていない）");
    }

    /// <summary>回収点(FieldedX/Z)は必ず両方揃うか両方 null。片肺だと読む側のフォールバックが破綻する。</summary>
    [Fact]
    public void FieldPoint_IsSetOrUnset_Together()
    {
        foreach (var (p, c) in Sweep())
            Assert.True(p.FieldedX is null == (p.FieldedZ is null), $"回収点が片肺（{c}）");
    }

    /// <summary>
    /// 回収点が未設定なのは「転がりを解かない経路」だけ＝そこでは回収点は着地点と同義になる。
    /// 逆に回収点が設定されているなら回収時刻も必ず持つ（座標だけあって時刻が無い出力を作らない）。
    /// </summary>
    [Fact]
    public void FieldPoint_WhenSet_AlwaysComesWithRetrieveTime()
    {
        foreach (var (p, c) in Sweep())
        {
            if (p.FieldedX is not null)
                Assert.True(p.FieldedAtSeconds is not null, $"回収点があるのに回収時刻が無い（{c}）");
            else
                // 未設定なら「空中で捕った」か「誰も触っていない（ファウル・本塁打）」のどちらか。
                Assert.True(p.FielderRole is null || p.IsFly, $"回収点が未設定なのに地上で処理している（{c}）");
        }
    }

    /// <summary>回収は着地より後（時間を遡らない）。</summary>
    [Fact]
    public void RetrieveTime_NeverPrecedesLanding()
    {
        foreach (var (p, c) in Sweep())
        {
            if (p.FieldedAtSeconds is not { } at) continue;
            Assert.True(at >= p.HangTimeSeconds - 1e-9,
                $"回収({at:F3}s)が着地({p.HangTimeSeconds:F3}s)より前（{c}）");
        }
    }

    /// <summary>処理野手が居るなら回収時刻と送球速度も揃う（返球・中継の起点が作れない出力を出さない）。</summary>
    [Fact]
    public void Fielder_ComesWithRetrieveTimeAndArm()
    {
        foreach (var (p, c) in Sweep())
        {
            if (p.FielderRole is null)
            {
                Assert.True(p.FieldedAtSeconds is null, $"処理野手が居ないのに回収時刻がある（{c}）");
                Assert.True(p.FielderThrowSpeedMps is null, $"処理野手が居ないのに送球速度がある（{c}）");
                continue;
            }
            Assert.True(p.FieldedAtSeconds is not null, $"処理野手が居るのに回収時刻が無い（{c}）");
            Assert.True(p.FielderThrowSpeedMps is not null, $"処理野手が居るのに送球速度が無い（{c}）");
        }
    }

    /// <summary>
    /// 判定margin は一塁送球の勝負をしたときだけ立つ（表示のCloseCallゲートがこの対応に乗っている, Issue #59）。
    /// </summary>
    [Fact]
    public void JudgementMargin_ExistsExactlyWhenThrowToFirstHappened()
    {
        foreach (var (p, c) in Sweep())
            Assert.True(p.ThrowArriveSeconds is null == (p.JudgementMarginSeconds is null),
                $"一塁送球と判定marginの有無が食い違う（{c}）");
    }

    /// <summary>打球分類が未判定なのはファウルだけ（弾道積分の前に返る唯一の経路）。</summary>
    [Fact]
    public void BallClass_IsUnsetOnlyForFoul()
    {
        foreach (var (p, c) in Sweep())
            Assert.True(p.Class is null == (p.Result == BattedBallResult.Foul),
                $"打球分類の有無がファウル判定と食い違う（{c}）");
    }

    /// <summary>内野を抜けたバウンドは、回収点が内野の外にある（内野で死んだ扱いになっていない）。</summary>
    [Fact]
    public void ThroughInfield_RetrievedOutsideTheInfield()
    {
        var coeff = new FieldingCoefficients();
        foreach (var (p, c) in Sweep())
        {
            if (!p.ThroughInfield) continue;
            Assert.True(p.FieldedZ is { } z && z > coeff.InfieldDepthM - 5.0,
                $"内野を抜けた打球の回収点が内野の中（{p.FieldedZ?.ToString("F1") ?? "未設定"}m, {c}）");
            Assert.True(p.ThrowArriveSeconds is null, $"内野を抜けたのに一塁送球の勝負をしている（{c}）");
        }
    }

    /// <summary>距離・方位は着地点と整合する（幾何が内部矛盾した出力を作らない）。</summary>
    [Fact]
    public void Geometry_RangeAndBearing_AgreeWithLanding()
    {
        foreach (var (p, c) in Sweep())
        {
            if (p.Result == BattedBallResult.Foul) continue; // ファウルは幾何を持たない
            var range = System.Math.Sqrt(p.LandingX * p.LandingX + p.LandingZ * p.LandingZ);
            Assert.Equal(range, p.RangeM, 6);
            var bearing = System.Math.Atan2(p.LandingX, p.LandingZ) * 180.0 / System.Math.PI;
            Assert.True(System.Math.Abs(bearing - p.BearingDeg) < 1.0,
                $"方位({p.BearingDeg:F1}°)と着地点({bearing:F1}°)が食い違う（{c}）");
        }
    }

    /// <summary>本塁打は誰も処理しない（外野手が場外まで追う絵にならない, Issue #300）。</summary>
    [Fact]
    public void HomeRun_HasNoFielder()
    {
        foreach (var (p, c) in Sweep())
        {
            if (p.Result != BattedBallResult.HomeRun) continue;
            Assert.True(p.FielderRole is null, $"本塁打に処理野手が居る（{c}）");
            Assert.True(p.FieldedX is null, $"本塁打に回収点がある（{c}）");
        }
    }
}
