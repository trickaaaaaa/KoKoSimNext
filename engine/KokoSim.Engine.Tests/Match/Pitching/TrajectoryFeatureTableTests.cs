using KokoSim.Engine.Match.Pitching;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Pitching;

/// <summary>
/// 判定用弾道特徴量テーブル（設計書15 Phase E-1）の検証。
/// テーブル補間が積分器（RK4, <see cref="PitchSimulator"/>）と一致すること（ゴールデン照合）と、
/// 補間経由でも既存の物理的妥当性（単調性）が保たれることを固定する。
/// </summary>
public sealed class TrajectoryFeatureTableTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly MoundGeometry Mound = new();

    // --- グリッド上の点は積分器と厳密一致（バイリニア補間の端点=そのまま返す） ---

    [Fact]
    public void Lookup_AtGridNode_ExactlyMatchesIntegrator()
    {
        var table = TrajectoryFeatureTable.Build(Aero, Mound);

        // Build のデフォルト格子点上（70+2*Nkm/h, 1800+50*Nrpm）。
        var speed = 146.0; // 70 + 2*38
        var rpm = 2200.0;  // 1800 + 50*8

        var expected = PitchSimulator.Simulate(
            new PitchSpec { SpeedKmh = speed, SpinRadPerSec = PitchSpec.BackspinFromRpm(rpm) }, Aero, Mound);
        var actual = table.Lookup(speed, rpm);

        Assert.Equal(expected.InducedVerticalBreakM, actual.InducedVerticalBreakM, 9);
        Assert.Equal(expected.FlightTimeSeconds, actual.FlightTimeSeconds, 9);
    }

    // --- 格子間（オフグリッド）でも積分器と近似一致（ゴールデン照合・許容誤差つき） ---

    [Theory]
    [InlineData(145.0, 2200.0)] // CLAUDE.md 検証値
    [InlineData(131.0, 1975.0)]
    [InlineData(158.5, 2465.0)]
    [InlineData(97.3, 1888.0)]
    [InlineData(169.9, 2610.0)]
    public void Lookup_OffGrid_MatchesIntegratorWithinTolerance(double speedKmh, double rpm)
    {
        var table = TrajectoryFeatureTable.Build(Aero, Mound);

        var expected = PitchSimulator.Simulate(
            new PitchSpec { SpeedKmh = speedKmh, SpinRadPerSec = PitchSpec.BackspinFromRpm(rpm) }, Aero, Mound);
        var actual = table.Lookup(speedKmh, rpm);

        // 格子2km/h×50rpmでのバイリニア誤差は物理的に滑らかな関数のため小さい（判定入力として十分な精度）。
        Assert.InRange(actual.InducedVerticalBreakM - expected.InducedVerticalBreakM, -0.01, 0.01);
        Assert.InRange(actual.FlightTimeSeconds - expected.FlightTimeSeconds, -0.001, 0.001);
    }

    // --- 範囲外はクランプ（例外を投げず、境界値相当に落ち着く） ---

    [Fact]
    public void Lookup_OutsideGridRange_Clamps()
    {
        var table = TrajectoryFeatureTable.Build(Aero, Mound);

        var belowRange = table.Lookup(10.0, 500.0);
        var atMin = table.Lookup(70.0, 1800.0);
        Assert.Equal(atMin.InducedVerticalBreakM, belowRange.InducedVerticalBreakM, 9);
        Assert.Equal(atMin.FlightTimeSeconds, belowRange.FlightTimeSeconds, 9);

        var aboveRange = table.Lookup(300.0, 5000.0);
        var atMax = table.Lookup(176.0, 2700.0);
        Assert.Equal(atMax.InducedVerticalBreakM, aboveRange.InducedVerticalBreakM, 9);
        Assert.Equal(atMax.FlightTimeSeconds, aboveRange.FlightTimeSeconds, 9);
    }

    // --- 補間経由でも物理的妥当性（単調性）が保たれる ---

    [Fact]
    public void Lookup_HigherSpin_ProducesMoreHop()
    {
        var table = TrajectoryFeatureTable.Build(Aero, Mound);
        var low = table.Lookup(145.0, 2000.0);
        var high = table.Lookup(145.0, 2600.0);

        Assert.True(high.InducedVerticalBreakM > low.InducedVerticalBreakM);
    }

    [Fact]
    public void Lookup_FasterPitch_ReachesPlateSooner()
    {
        var table = TrajectoryFeatureTable.Build(Aero, Mound);
        var slow = table.Lookup(130.0, 2200.0);
        var fast = table.Lookup(150.0, 2200.0);

        Assert.True(fast.FlightTimeSeconds < slow.FlightTimeSeconds);
    }

    // --- 決定論: 同一入力は常に同一出力（RNG非依存の純関数） ---

    [Fact]
    public void Lookup_IsDeterministic()
    {
        var table = TrajectoryFeatureTable.Build(Aero, Mound);
        var a = table.Lookup(143.7, 2233.0);
        var b = table.Lookup(143.7, 2233.0);

        Assert.Equal(a.InducedVerticalBreakM, b.InducedVerticalBreakM);
        Assert.Equal(a.FlightTimeSeconds, b.FlightTimeSeconds);
    }

    // --- キャッシュ: 同じ (aero, mound) は同一インスタンスを返す ---

    [Fact]
    public void GetOrBuild_CachesByAerodynamicsAndMound()
    {
        var a = TrajectoryFeatureTable.GetOrBuild(Aero, Mound);
        var b = TrajectoryFeatureTable.GetOrBuild(Aero, Mound);

        Assert.Same(a, b);
    }
}
