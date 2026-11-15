using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Pitching;

/// <summary>
/// 変化球の横の曲がる向きは投げ手で反転する（右投手のシュート系は三塁側＝−x、スライダー系は一塁側＝+x。左投手は鏡写し）。
/// 反転は <see cref="PitchSpinModel.ForThrower"/> の1か所だけ。判定は大きさしか見ないので大きさは同じでなければならない。
/// </summary>
public sealed class PitcherHandednessBreakTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly MoundGeometry Mound = new();
    private static readonly PitchingCoefficients Coeff = new();

    private static double HorizontalBreakM(PitchType type, Handedness throws)
    {
        var spin = PitchSpinModel.ForThrower(PitchSpinModel.SpinFor(Coeff, type, 50, 50), throws);
        var slot = new PitchSlot { Type = type, Power = 50, Sharpness = 50 };
        var sim = PitchSimulator.Simulate(new PitchSpec { SpeedKmh = 130.0 * slot.SpeedRatio, SpinRadPerSec = spin }, Aero, Mound);
        var table = TrajectoryFeatureTable.LookupFeatures(Aero, Mound, spin, 130.0 * slot.SpeedRatio);
        // 表の補間誤差（横 ≤3.1mm・issue #911）以内。小数2桁の丸め比較だと 0.3250 を挟んだ 0.2mm の差でも落ちる（issue #1016）。
        Assert.InRange(System.Math.Abs(sim.InducedHorizontalBreakM - table.InducedHorizontalBreakM), 0.0, 0.005);
        return sim.InducedHorizontalBreakM;
    }

    [Fact]
    public void ForThrower_LeftKeepsSpin_RightFlipsOnlyLateralComponent()
    {
        var spin = PitchSpec.SpinFromRpmAndTilt(2200.0, 110.0);
        var left = PitchSpinModel.ForThrower(spin, Handedness.Left);
        var right = PitchSpinModel.ForThrower(spin, Handedness.Right);
        Assert.Equal(spin, left);
        Assert.Equal(spin.X, right.X, 9);
        Assert.Equal(-spin.Y, right.Y, 9);
        Assert.Equal(spin.Z, right.Z, 9);
    }

    [Theory]
    [InlineData(PitchType.TwoSeam)]
    [InlineData(PitchType.Shuuto)]
    [InlineData(PitchType.Sinker)]
    [InlineData(PitchType.Changeup)]
    public void ArmSideBreakers_RunToThirdBaseForRighty_FirstBaseForLefty(PitchType type)
    {
        var right = HorizontalBreakM(type, Handedness.Right);
        var left = HorizontalBreakM(type, Handedness.Left);
        Assert.True(right < -0.01, $"{type} 右投手 {right}");
        Assert.True(left > 0.01, $"{type} 左投手 {left}");
        Assert.Equal(-right, left, 6);
    }

    [Theory]
    [InlineData(PitchType.Cutter)]
    [InlineData(PitchType.Slider)]
    public void GloveSideBreakers_RunToFirstBaseForRighty_ThirdBaseForLefty(PitchType type)
    {
        var right = HorizontalBreakM(type, Handedness.Right);
        var left = HorizontalBreakM(type, Handedness.Left);
        Assert.True(right > 0.01, $"{type} 右投手 {right}");
        Assert.True(left < -0.01, $"{type} 左投手 {left}");
        Assert.Equal(-right, left, 6);
    }

    [Fact]
    public void Session_PitchLog_FlipsHorizontalBreakByThrowingHand_SameMagnitude()
    {
        var pitcher = new PitcherAttributes
        {
            MaxVelocityKmh = 140, Control = 50,
            Repertoire = new[]
            {
                PitchSlot.FastballOf(50),
                new PitchSlot { Type = PitchType.Shuuto, Power = 50, Sharpness = 50 },
                new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 50 },
            },
        };
        var field = new FieldGeometry();

        double MeanIhb(Handedness throws, PitchType type)
        {
            var ctx = new AtBatContext { Fielders = field.StandardAlignment(), PitcherThrows = throws, CaptureTimeline = true };
            double sum = 0; var n = 0;
            for (ulong seed = 1; seed <= 60; seed++)
            {
                var session = AtBatSession.Begin(BatterAttributes.LeagueAverage, pitcher, ctx);
                var rng = new Xoshiro256Random(seed);
                while (!session.IsComplete) session.ThrowNextPitch(rng);
                foreach (var rec in session.Result.PitchLog!)
                    if (rec.PitchType == type) { sum += rec.Trajectory!.InducedHorizontalBreakM; n++; }
            }
            Assert.True(n > 0, $"{type} が1球も投げられていない");
            return sum / n;
        }

        var shuutoR = MeanIhb(Handedness.Right, PitchType.Shuuto);
        var shuutoL = MeanIhb(Handedness.Left, PitchType.Shuuto);
        var sliderR = MeanIhb(Handedness.Right, PitchType.Slider);
        var sliderL = MeanIhb(Handedness.Left, PitchType.Slider);
        Assert.True(shuutoR < 0 && shuutoL > 0);
        Assert.True(sliderR > 0 && sliderL < 0);
        Assert.Equal(-shuutoR, shuutoL, 6);
        Assert.Equal(-sliderR, sliderL, 6);
    }
}
