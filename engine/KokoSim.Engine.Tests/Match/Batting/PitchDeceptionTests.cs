using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>
/// 変化球の判定を「同じ投手のストレートからの外れ」で効かせる（issue #911・<see cref="PitchDeception"/>）。
/// ストレート自身は従来の合成量のまま、変化球はストレートの合成量＋重み×外れ。外れには球速差による重力落下の差が入る。
/// </summary>
public sealed class PitchDeceptionTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly MoundGeometry Mound = new();
    private static readonly PitchingCoefficients Pitching = new();
    private static readonly BattingCoefficients Batting = new();

    /// <summary>能力50・最速 <paramref name="topKmh"/> の投手が投げた球種 <paramref name="type"/> の特徴量（判定と同じ表引き）。</summary>
    private static PitchTrajectoryFeatures Features(PitchType type, double topKmh = 140.0, int sharpness = 50)
    {
        var f = TrajectoryFeatureTable.LookupFeatures(
            Aero, Mound, PitchSpinModel.SpinFor(Pitching, type, sharpness, 50), topKmh * Pitching.AxisFor(type).SpeedRatio);
        if (type == PitchType.Fastball) return f;
        var fb = TrajectoryFeatureTable.LookupFeatures(Aero, Mound, PitchSpinModel.FastballSpin(Pitching, 50), topKmh);
        return f with { Fastball = new FastballReference(fb.InducedVerticalBreakM, fb.InducedHorizontalBreakM, fb.FlightTimeSeconds) };
    }

    [Fact]
    public void Fastball_KeepsLegacyMagnitudeAndLaunchShift()
    {
        var fb = Features(PitchType.Fastball);
        Assert.Null(fb.Fastball);
        Assert.Equal(fb.BreakMagnitudeM, PitchDeception.BreakM(fb, Batting), 12);
        Assert.Equal(Batting.LaunchAnglePerVerticalBreakM * (fb.InducedVerticalBreakM - Batting.LaunchAngleBreakRefM),
            PitchDeception.LaunchAngleShiftDeg(fb, Batting), 12);
        Assert.Equal(0.0, PitchDeception.RelativeMagnitudeM(fb));
    }

    /// <summary>球速差は重力落下の差として縦の外れに入る（回転が同じでも遅い球はストレートより沈む）。</summary>
    [Fact]
    public void SlowerPitch_WithSameSpin_DropsBelowTheFastball()
    {
        var fb = Features(PitchType.Fastball);
        var slow = TrajectoryFeatureTable.LookupFeatures(Aero, Mound, 0.0, 120.0, Pitching.SpinRpmBase)
            with { Fastball = new FastballReference(fb.InducedVerticalBreakM, fb.InducedHorizontalBreakM, fb.FlightTimeSeconds) };
        var expected = (slow.InducedVerticalBreakM - fb.InducedVerticalBreakM)
                       - 9.81 / 2.0 * (slow.FlightTimeSeconds * slow.FlightTimeSeconds - fb.FlightTimeSeconds * fb.FlightTimeSeconds);
        Assert.Equal(expected, PitchDeception.RelativeVerticalM(slow), 12);
        Assert.True(PitchDeception.RelativeVerticalM(slow) < -0.2, $"縦の外れ {PitchDeception.RelativeVerticalM(slow):F3}m");
    }

    /// <summary>
    /// 実物寄りのフォーク（ホップしない速球）は無回転からの変化量こそ小さいが、ストレートからは沈む＝判定量は
    /// ストレートより大きく、打球角度はストレートより下がる（ゴロ寄り）。
    /// </summary>
    [Theory]
    [InlineData(125.0)]
    [InlineData(140.0)]
    [InlineData(150.0)]
    public void Fork_DeceivesMoreThanTheFastball_AndHitsLower(double topKmh)
    {
        var fb = Features(PitchType.Fastball, topKmh);
        var fork = Features(PitchType.Fork, topKmh);

        Assert.True(fork.BreakMagnitudeM < fb.BreakMagnitudeM, "前提: 無回転からの変化量はフォークの方が小さい");
        Assert.True(PitchDeception.RelativeVerticalM(fork) < -0.25, $"ストレートから25cm以上沈むはず: {PitchDeception.RelativeVerticalM(fork):F3}");
        Assert.True(PitchDeception.BreakM(fork, Batting) > PitchDeception.BreakM(fb, Batting));
        Assert.True(PitchDeception.LaunchAngleShiftDeg(fork, Batting) < PitchDeception.LaunchAngleShiftDeg(fb, Batting));
    }

    /// <summary>
    /// キレ＝ストレートからの回転の外れの大きさ（2026-11-14 監督決定）: 全変化球で、キレを上げるとストレートからの外れ
    /// （本塁での縦横の合成）が単調に増え、判定量も単調に増える（逆向きにならない）。
    /// </summary>
    [Theory]
    [InlineData(PitchType.TwoSeam)]
    [InlineData(PitchType.Cutter)]
    [InlineData(PitchType.Slider)]
    [InlineData(PitchType.Curve)]
    [InlineData(PitchType.Fork)]
    [InlineData(PitchType.Changeup)]
    [InlineData(PitchType.Shuuto)]
    [InlineData(PitchType.Sinker)]
    public void HigherSharpness_MonotonicallyWidensTheGapFromTheFastball(PitchType type)
    {
        var prevGap = double.NegativeInfinity;
        var prevBreak = double.NegativeInfinity;
        for (var sharpness = 1; sharpness <= 99; sharpness += 7)
        {
            var f = Features(type, sharpness: sharpness);
            var gap = PitchDeception.RelativeMagnitudeM(f);
            var brk = PitchDeception.BreakM(f, Batting);
            Assert.True(gap > prevGap, $"{type} キレ{sharpness}: 外れ {gap:F4} ≤ 前 {prevGap:F4}");
            Assert.True(brk > prevBreak, $"{type} キレ{sharpness}: 判定量 {brk:F4} ≤ 前 {prevBreak:F4}");
            prevGap = gap;
            prevBreak = brk;
        }
    }

    /// <summary>フォークはキレが高いほど回転が少ない（良いフォークほどホップが消える, issue #911）＝ストレートから離れる向き。</summary>
    [Fact]
    public void Fork_SharpnessReducesSpin()
    {
        var low = PitchSpinModel.ToRpmAndTilt(PitchSpinModel.SpinFor(Pitching, PitchType.Fork, 20, 50)).Rpm;
        var high = PitchSpinModel.ToRpmAndTilt(PitchSpinModel.SpinFor(Pitching, PitchType.Fork, 80, 50)).Rpm;
        Assert.True(high < low, $"キレ80 {high:F0}rpm / キレ20 {low:F0}rpm");
        Assert.True(Features(PitchType.Fork, sharpness: 80).InducedVerticalBreakM
                    < Features(PitchType.Fork, sharpness: 20).InducedVerticalBreakM);
    }

    /// <summary>
    /// フォークは「ホップしない速球」: 誘発縦変化はストレートより小さいが、カーブのような大きなマイナス（トップスピンで
    /// 下へ曲げる）にはならない（issue #911 の症状＝対決ビューでフォークがカーブのような山なりに見えた）。
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(80)]
    public void Fork_HasLessHopThanTheFastball_ButIsNotACurve(int sharpness)
    {
        var fb = Features(PitchType.Fastball);
        var fork = Features(PitchType.Fork, sharpness: sharpness);
        var curve = Features(PitchType.Curve);   // 比べるカーブはキレ50
        Assert.True(fork.InducedVerticalBreakM < fb.InducedVerticalBreakM);
        Assert.True(fork.InducedVerticalBreakM > -0.05, $"フォークの誘発縦変化 {fork.InducedVerticalBreakM:F3}m");
        Assert.True(curve.InducedVerticalBreakM < -0.2, $"カーブの誘発縦変化 {curve.InducedVerticalBreakM:F3}m");
        Assert.True(System.Math.Abs(fork.InducedHorizontalBreakM) < 0.01);
    }
}
