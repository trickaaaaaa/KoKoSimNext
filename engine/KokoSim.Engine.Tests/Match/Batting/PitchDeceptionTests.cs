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
        var axis = Pitching.AxisFor(type);
        var slot = new PitchSlot { Type = type, Power = 50, Sharpness = sharpness };
        var rank = PitchSlot.IsSpinByPower(type) ? 50 : sharpness;
        var slope = PitchSlot.IsSpinByPower(type) ? Pitching.SpinRpmPerPower : Pitching.SpinRpmPerSharpness * axis.SharpnessRpmFactor;
        var rpm = (Pitching.SpinRpmBase + (rank - 50) * slope) * axis.RpmScale;
        var f = TrajectoryFeatureTable.LookupFeatures(Aero, Mound, axis.TiltDeg, topKmh * slot.SpeedRatio, rpm);
        if (type == PitchType.Fastball) return f;
        var fb = TrajectoryFeatureTable.LookupFeatures(Aero, Mound, 0.0, topKmh, Pitching.SpinRpmBase);
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

    /// <summary>キレが高いほど判定量が大きい（逆向きにならない）。フォークは回転を減らす向き＝ストレートから離れる。</summary>
    [Theory]
    [InlineData(PitchType.Fork)]
    [InlineData(PitchType.Slider)]
    [InlineData(PitchType.Curve)]
    [InlineData(PitchType.Changeup)]
    public void HigherSharpness_IncreasesDeception(PitchType type)
    {
        var low = PitchDeception.BreakM(Features(type, sharpness: 20), Batting);
        var high = PitchDeception.BreakM(Features(type, sharpness: 80), Batting);
        Assert.True(high > low, $"{type}: キレ20 {low:F3} / キレ80 {high:F3}");
    }

    /// <summary>フォークはキレが高いほど回転が少ない（良いフォークほどホップが消える, issue #911）。</summary>
    [Fact]
    public void Fork_SharpnessReducesSpin()
    {
        Assert.Equal(-1.0, Pitching.AxisFor(PitchType.Fork).SharpnessRpmFactor);
        Assert.Equal(1.0, Pitching.AxisFor(PitchType.Slider).SharpnessRpmFactor);
        var low = Features(PitchType.Fork, sharpness: 20);
        var high = Features(PitchType.Fork, sharpness: 80);
        Assert.True(high.InducedVerticalBreakM < low.InducedVerticalBreakM);
    }
}
