using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Pitching;

/// <summary>
/// 弾道積分（重力＋空気抵抗＋マグヌス力）の検証。
/// 既知値との突き合わせ（無回転・無抗力＝放物運動）と、物理的妥当性の帯を確認する。
/// </summary>
public sealed class BallisticIntegratorTests
{
    // --- 既知値: 無抗力・無回転なら解析解（放物運動）に一致する ---

    [Fact]
    public void NoDragNoSpin_MatchesProjectileMotion()
    {
        var aero = new Aerodynamics { DragCoefficient = 0.0 }; // 抗力オフ
        var release = new Vector3D(0, 1.8, 0);
        var v0 = new Vector3D(0, 0, 145.0 / 3.6); // 水平投射
        var distance = 16.74;

        var r = BallisticIntegrator.IntegrateToPlate(release, v0, Vector3D.Zero, aero, distance);

        // 水平速度一定 → t = 距離 / vz、落下量 = 0.5 g t^2
        var expectedT = distance / v0.Z;
        var expectedDrop = 0.5 * aero.Gravity * expectedT * expectedT;
        var expectedY = release.Y - expectedDrop;

        Assert.Equal(expectedT, r.FlightTimeSeconds, 4);
        Assert.Equal(expectedY, r.CrossingPosition.Y, 4);
        Assert.Equal(distance, r.CrossingPosition.Z - release.Z, 5);
    }

    [Fact]
    public void NoSpin_ProducesNoHorizontalDeflection()
    {
        var aero = new Aerodynamics();
        var spec = new PitchSpec { SpeedKmh = 145 };
        var traj = PitchSimulator.Simulate(spec, aero, new MoundGeometry());

        Assert.Equal(0.0, traj.InducedVerticalBreakM, 6);
        Assert.Equal(0.0, traj.InducedHorizontalBreakM, 6);
    }

    // --- 物理的妥当性: 145km/h・2200rpm のストレート（CLAUDE.md 指定の検証） ---

    [Fact]
    public void Fastball_145kmh_2200rpm_IsPhysicallyPlausible()
    {
        var aero = new Aerodynamics();
        var spec = new PitchSpec
        {
            SpeedKmh = 145,
            SpinRadPerSec = PitchSpec.BackspinFromRpm(2200),
        };

        var traj = PitchSimulator.Simulate(spec, aero, new MoundGeometry());

        // 飛行時間: 16.74m を約40m/s → 0.40〜0.48s
        Assert.InRange(traj.FlightTimeSeconds, 0.40, 0.48);

        // 到達球速: 抗力で減速するが初速より下・かつ大きく落ちない（125〜138km/h）
        Assert.InRange(traj.FinalSpeedKmh, 125.0, 138.0);
        Assert.True(traj.FinalSpeedKmh < spec.SpeedKmh, "抗力で初速より遅くなるはず");

        // ホップ量（誘発縦変化 IVB）: 四縫目2200rpm 相当の 30〜55cm
        Assert.InRange(traj.InducedVerticalBreakM, 0.30, 0.55);
        Assert.True(traj.InducedVerticalBreakM > 0, "バックスピンは無回転球より落ちにくい（正のホップ）");

        // 本塁通過高さがストライクゾーン近傍に収まる（暴れていない）
        Assert.InRange(traj.PlateCrossing.Y, 0.5, 2.0);
    }

    [Fact]
    public void HigherSpin_ProducesMoreHop()
    {
        var aero = new Aerodynamics();
        var geo = new MoundGeometry();

        var low = PitchSimulator.Simulate(
            new PitchSpec { SpeedKmh = 145, SpinRadPerSec = PitchSpec.BackspinFromRpm(2000) }, aero, geo);
        var high = PitchSimulator.Simulate(
            new PitchSpec { SpeedKmh = 145, SpinRadPerSec = PitchSpec.BackspinFromRpm(2600) }, aero, geo);

        Assert.True(high.InducedVerticalBreakM > low.InducedVerticalBreakM,
            $"高回転ほどホップが大きいはず: low={low.InducedVerticalBreakM:F3} high={high.InducedVerticalBreakM:F3}");
    }

    [Fact]
    public void FasterPitch_ReachesPlateSooner()
    {
        var aero = new Aerodynamics();
        var geo = new MoundGeometry();

        var slow = PitchSimulator.Simulate(new PitchSpec { SpeedKmh = 130 }, aero, geo);
        var fast = PitchSimulator.Simulate(new PitchSpec { SpeedKmh = 150 }, aero, geo);

        Assert.True(fast.FlightTimeSeconds < slow.FlightTimeSeconds);
    }

    [Fact]
    public void Simulation_IsDeterministic()
    {
        var aero = new Aerodynamics();
        var geo = new MoundGeometry();
        var spec = new PitchSpec { SpeedKmh = 145, SpinRadPerSec = PitchSpec.BackspinFromRpm(2200) };

        var a = PitchSimulator.Simulate(spec, aero, geo);
        var b = PitchSimulator.Simulate(spec, aero, geo);

        Assert.Equal(a.FlightTimeSeconds, b.FlightTimeSeconds);
        Assert.Equal(a.InducedVerticalBreakM, b.InducedVerticalBreakM);
        Assert.Equal(a.PlateCrossing.Y, b.PlateCrossing.Y);
    }

    [Fact]
    public void IntegrationStep_DoesNotMateriallyChangeResult()
    {
        // 刻み幅を変えても結果がほぼ不変（RK4 の収束確認）。
        var aero = new Aerodynamics();
        var release = new Vector3D(0, 1.8, 0);
        var v0 = new Vector3D(0, 0, 145.0 / 3.6);
        var spin = PitchSpec.BackspinFromRpm(2200);

        var coarse = BallisticIntegrator.IntegrateToPlate(release, v0, spin, aero, 16.74, dt: 0.001);
        var fine = BallisticIntegrator.IntegrateToPlate(release, v0, spin, aero, 16.74, dt: 0.0001);

        Assert.Equal(fine.FlightTimeSeconds, coarse.FlightTimeSeconds, 4);
        Assert.Equal(fine.CrossingPosition.Y, coarse.CrossingPosition.Y, 4);
    }
}
