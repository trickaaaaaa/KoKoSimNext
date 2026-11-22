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
    [InlineData(PitchType.Curve, -1, -1)]    // 縦の大変化（落ちる）＋スライダーと同じ側（腕と反対側。issue #1016）
    [InlineData(PitchType.Fork, 1, 0)]       // 低回転の縦落ち＝弱いバックスピン（ホップはあるがストレートより小さい。issue #911）
    [InlineData(PitchType.Changeup, 1, 1)]   // 弱いバックスピン＋アーム側へのフェード（浮かない分だけ沈む。issue #1016）
    [InlineData(PitchType.Shuuto, 1, 1)]     // 弱いバックスピン＋打者の内側へ食い込む（横成分が主。issue #1016）
    [InlineData(PitchType.Sinker, 1, 1)]     // 弱いバックスピン＋腕側（浮かない分だけ沈む。issue #1016）
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

    /// <summary>
    /// フォークは「ホップしない速球」（issue #911）。誘発縦変化は弱いプラス（バックスピン寄り）で、
    /// カーブのような大きなマイナス（トップスピンで下へ曲げる球）にならない。ストレートとの差は
    /// ホップが無い分の落差で、同じ球速・同じ能力（50）で 15cm 以上沈む。旧設定（純トップスピン 180°・
    /// 0.90）は IVB≈−0.42m で「少し速いカーブ」になり、対決ビューで山なりに描かれていた。
    /// </summary>
    [Theory]
    [InlineData(145.0)]
    [InlineData(133.4)] // 145km/h × フォークの球速比 0.92
    [InlineData(120.0)]
    public void Fork_IsUnhoppedFastball_NotCurveLike(double speedKmh)
    {
        PitchTrajectory TrajFor(PitchType type)
        {
            var axis = Coeff.AxisFor(type);
            var rpm = Coeff.SpinRpmBase * axis.RpmScale; // 能力=50相当
            var spec = new PitchSpec { SpeedKmh = speedKmh, SpinRadPerSec = PitchSpec.SpinFromRpmAndTilt(rpm, axis.TiltDeg) };
            return PitchSimulator.Simulate(spec, Aero, Mound);
        }

        var fork = TrajFor(PitchType.Fork);
        var fastball = TrajFor(PitchType.Fastball);
        var curve = TrajFor(PitchType.Curve);

        // カーブのような大きなマイナスにならない（弱いバックスピン＝ゼロ〜小さなプラス）。
        Assert.InRange(fork.InducedVerticalBreakM, -0.05, 0.30);
        Assert.True(curve.InducedVerticalBreakM < -0.15, $"比較対象のカーブは落ちるはず: {curve.InducedVerticalBreakM:F3}");
        Assert.True(fork.InducedVerticalBreakM - curve.InducedVerticalBreakM >= 0.30,
            $"フォーク({fork.InducedVerticalBreakM:F3}m)はカーブ({curve.InducedVerticalBreakM:F3}m)より 30cm 以上浮く（トップスピンの球ではない）");
        // ストレートより小さく、ホップが無い分だけ 15cm 以上沈む。
        Assert.True(fastball.InducedVerticalBreakM - fork.InducedVerticalBreakM >= 0.15,
            $"ストレート({fastball.InducedVerticalBreakM:F3}m)とフォーク({fork.InducedVerticalBreakM:F3}m)の落差が15cm未満");
        // 横には曲げない（縦落ちの球）。
        Assert.InRange(System.Math.Abs(fork.InducedHorizontalBreakM), 0.0, 0.01);
    }

    /// <summary>
    /// フォークの到達時間はストレートに近い（同じ球速なら回転の差で到達時間はほとんど変わらない）＝
    /// カーブのように「遅く山なりに来る」球ではない（issue #911）。実球速の差（球速比）で比べる。
    /// </summary>
    [Fact]
    public void Fork_FlightTime_IsCloserToFastballThanCurve()
    {
        PitchTrajectory TrajFor(PitchType type, double kmh)
        {
            var axis = Coeff.AxisFor(type);
            var rpm = Coeff.SpinRpmBase * axis.RpmScale;
            var spec = new PitchSpec { SpeedKmh = kmh, SpinRadPerSec = PitchSpec.SpinFromRpmAndTilt(rpm, axis.TiltDeg) };
            return PitchSimulator.Simulate(spec, Aero, Mound);
        }

        const double Top = 145.0;
        var fastball = TrajFor(PitchType.Fastball, Top);
        var fork = TrajFor(PitchType.Fork, Top * 0.92);
        var curve = TrajFor(PitchType.Curve, Top * 0.80);

        Assert.True(fork.FlightTimeSeconds - fastball.FlightTimeSeconds < curve.FlightTimeSeconds - fork.FlightTimeSeconds,
            $"FB {fastball.FlightTimeSeconds:F3}s / フォーク {fork.FlightTimeSeconds:F3}s / カーブ {curve.FlightTimeSeconds:F3}s");
    }

    /// <summary>能力50・ストレート最速140km/h・球種の球速比で投げたときの弾道（issue #1016 の表と同じ測り方）。</summary>
    private static (PitchTrajectory Pitch, PitchTrajectory Fastball) AtTop140(PitchType type)
    {
        const double Top = 140.0;
        PitchTrajectory Sim(PitchType t, double kmh)
        {
            var axis = Coeff.AxisFor(t);
            var spec = new PitchSpec { SpeedKmh = kmh, SpinRadPerSec = PitchSpec.SpinFromRpmAndTilt(Coeff.SpinRpmBase * axis.RpmScale, axis.TiltDeg) };
            return PitchSimulator.Simulate(spec, Aero, Mound);
        }
        var ratio = Coeff.AxisFor(type).SpeedRatio;
        return (Sim(type, Top * ratio), Sim(PitchType.Fastball, Top));
    }

    /// <summary>
    /// 各球種の誘発変化の大きさが実物の目安（MLB の代表値を高校野球の球速へ寄せた範囲）に入る（issue #1016）。
    /// 能力50・最速140km/h・球種の球速比。縦＝回転による浮き(+)／沈み(−)［重力ぶんを除く］、横＝+が投げ腕の側。
    /// シンカー・チェンジアップ・シュートは弱いバックスピン（縦プラス）、カーブは −20〜−40cm でスライダーと同じ側、
    /// ツーシーム・カットは +20cm 前後（ストレートの +44cm より浮かない）。
    /// </summary>
    [Theory]
    [InlineData(PitchType.Fastball, 35, 50, -1, 1)]
    [InlineData(PitchType.TwoSeam, 12, 28, 10, 30)]
    [InlineData(PitchType.Cutter, 12, 28, -20, -3)]
    [InlineData(PitchType.Slider, -5, 12, -45, -20)]
    [InlineData(PitchType.Curve, -40, -20, -30, -10)]
    [InlineData(PitchType.Fork, 12, 30, -1, 1)]
    [InlineData(PitchType.Changeup, 8, 25, 15, 35)]
    [InlineData(PitchType.Shuuto, 10, 25, 30, 50)]
    [InlineData(PitchType.Sinker, 12, 28, 20, 40)]
    public void EachPitchType_InducedBreak_IsInRealisticRange(PitchType type, double vMinCm, double vMaxCm, double hMinCm, double hMaxCm)
    {
        var (p, _) = AtTop140(type);
        Assert.InRange(p.InducedVerticalBreakM * 100.0, vMinCm, vMaxCm);
        Assert.InRange(p.InducedHorizontalBreakM * 100.0, hMinCm, hMaxCm);
    }

    /// <summary>
    /// 本塁でのストレートとの高さの差（誘発縦変化の差＋飛翔時間の差による重力落下の差）。
    /// シンカー・チェンジアップ・シュートは「ストレートより浮かない分だけ沈む」球＝ストレートより低いが、
    /// トップスピンで下へ曲げるカーブよりずっと浅い（直す前は −70〜−123cm で山なりに描かれていた。issue #1016）。
    /// </summary>
    [Theory]
    [InlineData(PitchType.Sinker)]
    [InlineData(PitchType.Changeup)]
    [InlineData(PitchType.Shuuto)]
    [InlineData(PitchType.TwoSeam)]
    [InlineData(PitchType.Cutter)]
    public void SinkingFastballFamily_DropsBelowFastball_ButFarLessThanCurve(PitchType type)
    {
        double DropBelowFastballM(PitchType t)
        {
            var (p, fb) = AtTop140(t);
            return (p.InducedVerticalBreakM - fb.InducedVerticalBreakM)
                - Aero.Gravity / 2.0 * (p.FlightTimeSeconds * p.FlightTimeSeconds - fb.FlightTimeSeconds * fb.FlightTimeSeconds);
        }

        var drop = DropBelowFastballM(type);
        var curve = DropBelowFastballM(PitchType.Curve);
        Assert.InRange(drop, -0.75, -0.10);
        Assert.True(drop - curve >= 0.50, $"{type}: ストレートとの高さの差 {drop:F3}m がカーブ {curve:F3}m に近すぎる");
    }

    /// <summary>カーブの横はスライダーと同じ側（腕と反対側）。スライダーより横が小さく、縦は大きく落ちる（issue #1016）。</summary>
    [Fact]
    public void Curve_BreaksToGloveSide_LikeSlider_ButMoreVertical()
    {
        var (curve, _) = AtTop140(PitchType.Curve);
        var (slider, _) = AtTop140(PitchType.Slider);

        Assert.True(curve.InducedHorizontalBreakM < 0.0, $"カーブの横 {curve.InducedHorizontalBreakM:F3}m は腕と反対側（−）のはず");
        Assert.True(System.Math.Abs(curve.InducedHorizontalBreakM) < System.Math.Abs(slider.InducedHorizontalBreakM));
        Assert.True(curve.InducedVerticalBreakM < slider.InducedVerticalBreakM - 0.20);
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
