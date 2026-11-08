using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>
/// フェアのポップフライ（issue #830 ②）。芯の下を叩いた打球（品質が低く元の角度が上向き）の一部を、
/// 真上に近い遅い打球へ差し替える。以前は打球角度50°以上が打球の0.9%しかなく、しかも8割を外野手が捕っていた。
/// </summary>
public sealed class PopupTests
{
    private static readonly FieldGeometry Field = new();

    /// <summary>ポップフライの弾道（角度64°・初速95km/h 前後）は内野に落ち、内野手（か捕手）が捕る。</summary>
    [Theory]
    [InlineData(64.0, 95.0, 0.0)]
    [InlineData(68.0, 90.0, 20.0)]
    [InlineData(71.0, 80.0, -25.0)]
    public void PopupTrajectory_IsCaughtInTheInfield(double launchDeg, double kmh, double bearingDeg)
    {
        var p = FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = kmh / 3.6, LaunchAngleDeg = launchDeg, BearingDeg = bearingDeg },
            Field, new Aerodynamics(), BatterAttributes.LeagueAverage, Field.StandardAlignment(),
            new FieldingCoefficients { FlyErrorBaseProb = 0.0 }, new Xoshiro256Random(1));
        Assert.Equal(BattedBallResult.Out, p.Result);
        Assert.True(p.IsFly);
        Assert.True(p.RangeM < 46.0, $"内野を越えた（{p.RangeM:F1}m）");
        Assert.True(p.FielderRole is FieldPosition.Pitcher or FieldPosition.Catcher or FieldPosition.FirstBase
            or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop,
            $"内野手でない野手が捕った: {p.FielderRole}");
    }

    /// <summary>本塁のすぐ前に上がった飛球は捕手が捕る（以前は捕手が飛球の候補から外れていた）。</summary>
    [Fact]
    public void PopJustInFrontOfHome_CatcherCatchesIt()
    {
        var p = FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 70 / 3.6, LaunchAngleDeg = 84.0, BearingDeg = 0.0 },
            Field, new Aerodynamics(), BatterAttributes.LeagueAverage, Field.StandardAlignment(),
            new FieldingCoefficients { FlyErrorBaseProb = 0.0 }, new Xoshiro256Random(1));
        Assert.Equal(BattedBallResult.Out, p.Result);
        Assert.Equal(FieldPosition.Catcher, p.FielderRole);
    }

    /// <summary>
    /// 割合0なら差し替えは起きず、Fork した別ストリームなので本流の乱数列も変わらない
    /// （ポップフライにならない打席は1ビットも変わらない）。割合>0でもポップフライにならなかった打球は同一。
    /// </summary>
    [Fact]
    public void Popup_DoesNotDisturbTheMainRandomStream()
    {
        var plan = new PitchPlan { Type = PitchType.Fastball, AimX = 0.0, AimY = 0.5, VelocityKmh = 135.0, Stuff = 0.0 };
        var features = new PitchTrajectoryFeatures(0.0, 0.0, 0.45);
        int popped = 0, same = 0;
        for (ulong seed = 1; seed <= 2000; seed++)
        {
            var off = new Xoshiro256Random(seed);
            var on = new Xoshiro256Random(seed);
            var a = ContactModel.Resolve(BatterAttributes.LeagueAverage, plan, features, true,
                new BattingCoefficients { PopupShare = 0.0 }, off);
            var b = ContactModel.Resolve(BatterAttributes.LeagueAverage, plan, features, true,
                new BattingCoefficients(), on);
            Assert.Equal(off.NextUInt64(), on.NextUInt64());   // 本流は同じだけ進んでいる
            if (a.Ball is null) { Assert.Null(b.Ball); continue; }
            if (b.Ball!.LaunchAngleDeg >= 50.0 && a.Ball.LaunchAngleDeg < 50.0) { popped++; continue; }
            Assert.Equal(a.Ball, b.Ball);
            same++;
        }
        Assert.True(popped > 0, "ポップフライが1本も出ない");
        Assert.True(same > popped, "差し替えが多すぎる");
    }
}
