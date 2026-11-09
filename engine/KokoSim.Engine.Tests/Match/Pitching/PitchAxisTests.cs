using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Pitching;

/// <summary>
/// 球種ごとの回転軸（issue #835）の物理突き合わせ。設計書02 §2.1「変化の性質」の記述どおりの
/// 符号・大きさが <see cref="PitchSimulator"/>（RK4）の結果に出ることを固定する。
/// 「全球種バックスピン固定」だった穴（issue #835）の再発防止テスト。
/// </summary>
public sealed class PitchAxisTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly MoundGeometry Mound = new();
    private static readonly PitchingCoefficients Coeff = new();

    /// <summary>tiltDeg=0（純バックスピン）は <see cref="PitchSpec.BackspinFromRpm"/> と完全一致する。</summary>
    [Fact]
    public void SpinFromRpmAndTilt_AtZeroDeg_MatchesBackspinFromRpm()
    {
        var a = PitchSpec.SpinFromRpmAndTilt(2200.0, 0.0);
        var b = PitchSpec.BackspinFromRpm(2200.0);

        Assert.Equal(b.X, a.X, 9);
        Assert.Equal(b.Y, a.Y, 9);
        Assert.Equal(b.Z, a.Z, 9);
    }

    /// <summary>tiltDeg=180（純トップスピン）は縦変化が負（無回転球より落ちる）になる。</summary>
    [Fact]
    public void SpinFromRpmAndTilt_At180Deg_ProducesNegativeVerticalBreak()
    {
        var spec = new PitchSpec { SpeedKmh = 125, SpinRadPerSec = PitchSpec.SpinFromRpmAndTilt(2200.0, 180.0) };
        var traj = PitchSimulator.Simulate(spec, Aero, Mound);

        Assert.True(traj.InducedVerticalBreakM < 0.0,
            $"トップスピンは無回転球より落ちるはず（負のIVB）: {traj.InducedVerticalBreakM:F3}");
        Assert.InRange(System.Math.Abs(traj.InducedHorizontalBreakM), 0.0, 0.01);
    }

    /// <summary>tiltDeg=±90（純サイドスピン）は縦変化が0近傍で横変化だけが出る。符号は±で反転する。</summary>
    [Theory]
    [InlineData(90.0)]
    [InlineData(-90.0)]
    public void SpinFromRpmAndTilt_AtPureSidespin_ProducesHorizontalOnlyBreak(double tiltDeg)
    {
        var spec = new PitchSpec { SpeedKmh = 130, SpinRadPerSec = PitchSpec.SpinFromRpmAndTilt(2200.0, tiltDeg) };
        var traj = PitchSimulator.Simulate(spec, Aero, Mound);

        Assert.InRange(System.Math.Abs(traj.InducedVerticalBreakM), 0.0, 0.01);
        Assert.NotEqual(0.0, traj.InducedHorizontalBreakM);
        // 符号は tiltDeg の符号と一致する（+90→+X、−90→−X）。
        Assert.Equal(System.Math.Sign(tiltDeg), System.Math.Sign(traj.InducedHorizontalBreakM));
    }

    /// <summary>
    /// 設計書02 §2.1 の全9球種が、既定の回転軸（<see cref="PitchingCoefficients.AxisFor"/>）どおりの
    /// 縦変化・横変化の符号を持つこと。ストレート基準の145km/h・2200rpm相当（能力50）を代表点に取る。
    /// </summary>
    [Theory]
    [InlineData(PitchType.Fastball, 1, 0)]   // バックスピンによるホップ成分 → 縦+・横0
    [InlineData(PitchType.TwoSeam, 1, 1)]    // 微シュート(横+)＋微沈(FBより弱いホップ)
    [InlineData(PitchType.Cutter, 1, -1)]    // 小さく横滑り（グラブ側）
    [InlineData(PitchType.Slider, 1, -1)]    // 横〜斜めの大きな変化（グラブ側）
    [InlineData(PitchType.Curve, -1, 1)]     // 縦の大変化（落ちる）
    [InlineData(PitchType.Fork, -1, 0)]      // 低回転の縦落ち
    [InlineData(PitchType.Changeup, -1, 1)]  // 沈み（アーム側へのフェード）
    [InlineData(PitchType.Shuuto, -1, 1)]    // 打者の内側へ食い込む（横成分が主）
    [InlineData(PitchType.Sinker, -1, 1)]    // 内側へ沈む（縦成分が主）
    public void EachPitchType_MatchesDesignedBreakSign(PitchType type, int expectedVerticalSign, int expectedHorizontalSign)
    {
        var axis = Coeff.AxisFor(type);
        var rpm = Coeff.SpinRpmBase * axis.RpmScale; // 能力=50相当
        var spec = new PitchSpec { SpeedKmh = 125, SpinRadPerSec = PitchSpec.SpinFromRpmAndTilt(rpm, axis.TiltDeg) };
        var traj = PitchSimulator.Simulate(spec, Aero, Mound);

        if (expectedVerticalSign > 0) Assert.True(traj.InducedVerticalBreakM > 0.0, $"{type}: IVB={traj.InducedVerticalBreakM:F3}");
        else if (expectedVerticalSign < 0) Assert.True(traj.InducedVerticalBreakM < 0.0, $"{type}: IVB={traj.InducedVerticalBreakM:F3}");

        if (expectedHorizontalSign > 0) Assert.True(traj.InducedHorizontalBreakM > 0.0, $"{type}: IHB={traj.InducedHorizontalBreakM:F3}");
        else if (expectedHorizontalSign < 0) Assert.True(traj.InducedHorizontalBreakM < 0.0, $"{type}: IHB={traj.InducedHorizontalBreakM:F3}");
        else Assert.InRange(System.Math.Abs(traj.InducedHorizontalBreakM), 0.0, 0.01);
    }

    /// <summary>フォークは「低回転」ぶん、カーブ（高回転・高効率）より誘発変化の合成量が小さい（issue #835）。</summary>
    [Fact]
    public void Fork_HasSmallerBreakMagnitude_ThanCurve()
    {
        PitchTrajectory TrajFor(PitchType type)
        {
            var axis = Coeff.AxisFor(type);
            var rpm = Coeff.SpinRpmBase * axis.RpmScale;
            var spec = new PitchSpec { SpeedKmh = 120, SpinRadPerSec = PitchSpec.SpinFromRpmAndTilt(rpm, axis.TiltDeg) };
            return PitchSimulator.Simulate(spec, Aero, Mound);
        }

        var fork = TrajFor(PitchType.Fork);
        var curve = TrajFor(PitchType.Curve);

        double Magnitude(PitchTrajectory t) => System.Math.Sqrt(
            t.InducedVerticalBreakM * t.InducedVerticalBreakM + t.InducedHorizontalBreakM * t.InducedHorizontalBreakM);

        Assert.True(Magnitude(fork) < Magnitude(curve),
            $"フォーク({Magnitude(fork):F3}m) < カーブ({Magnitude(curve):F3}m) のはず（低回転の縦落ち）");
    }

    /// <summary>球種ごとの実効回転数倍率（issue #835）: RpmForPlan 相当の計算が球種で変わること（単調性ではなく分岐確認）。</summary>
    [Fact]
    public void AxisFor_FastballAndFork_HaveDistinctRpmScale()
    {
        var fastball = Coeff.AxisFor(PitchType.Fastball);
        var fork = Coeff.AxisFor(PitchType.Fork);

        Assert.True(fork.RpmScale < fastball.RpmScale, "フォークはストレートより実効回転数倍率が低い（低回転の縦落ち）");
    }
}
