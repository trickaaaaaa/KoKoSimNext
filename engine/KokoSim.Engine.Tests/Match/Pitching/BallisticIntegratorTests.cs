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
        // 誘発変化は「回転あり − 無回転」の差なので、無回転球ではどちらも恒等に0（何も検査にならない）。
        // 代わりに無回転球の絶対位置を見る: 真っ直ぐ投げて横に逸れず、抗力ぶん無抗力の放物運動より遅く・低く着く。
        var aero = new Aerodynamics();
        var geo = new MoundGeometry();
        var spec = new PitchSpec { SpeedKmh = 145 };
        var traj = PitchSimulator.Simulate(spec, aero, geo);

        Assert.Equal(spec.ReleasePosition.X, traj.PlateCrossing.X, 9);

        var vz = spec.SpeedKmh / 3.6;
        var dragFreeT = geo.ReleaseToPlateDistanceM / vz;
        var dragFreeY = spec.ReleasePosition.Y - 0.5 * aero.Gravity * dragFreeT * dragFreeT;
        Assert.True(traj.FlightTimeSeconds > dragFreeT, "抗力で減速して遅く着くはず");
        Assert.True(traj.PlateCrossing.Y < dragFreeY, "遅く着くぶん重力で余計に落ちるはず");
        Assert.InRange(traj.FlightTimeSeconds - dragFreeT, 0.005, 0.03);
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

        // ホップ量（誘発縦変化 IVB）: 四縫目2200rpm 相当。実測 0.429m（帯を実測の周りへ詰める）
        Assert.InRange(traj.InducedVerticalBreakM, 0.40, 0.46);
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

    // --- 打球への風（issue #1614）: IntegrateToGround にだけ効く。既知の符号・大小関係を固定する。 ---

    private static Vector3D FlyBallVelocity(double speedMps, double launchAngleDeg)
    {
        var rad = launchAngleDeg * Math.PI / 180.0;
        return new Vector3D(0, speedMps * Math.Sin(rad), speedMps * Math.Cos(rad));
    }

    [Fact]
    public void Wind_OmittedParameter_MatchesExplicitZeroVector()
    {
        // 既定（無風）は IntegrateToGround の既存の呼び出し（wind 引数なし）と1ビットも変わらない。
        var aero = new Aerodynamics();
        var contact = new Vector3D(0, 1.0, 0);
        var v0 = FlyBallVelocity(40.0, 28.0);

        var withoutArg = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero);
        var withZero = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero, wind: Vector3D.Zero);

        Assert.Equal(withoutArg.LandingPosition.X, withZero.LandingPosition.X, 12);
        Assert.Equal(withoutArg.LandingPosition.Z, withZero.LandingPosition.Z, 12);
        Assert.Equal(withoutArg.HangTimeSeconds, withZero.HangTimeSeconds, 12);
    }

    [Fact]
    public void Tailwind_IncreasesCarryDistance()
    {
        // 追い風（打球の進行方向=+Zへ吹く）は空気に対する相対速度を下げ、抗力が弱まる＝遠くへ飛ぶ。
        var aero = new Aerodynamics();
        var contact = new Vector3D(0, 1.0, 0);
        var v0 = FlyBallVelocity(40.0, 28.0);

        var noWind = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero);
        var tailwind = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero, wind: new Vector3D(0, 0, 5.0));

        Assert.True(tailwind.LandingPosition.Z > noWind.LandingPosition.Z,
            $"追い風5m/sで飛距離が伸びるはず: 無風Z={noWind.LandingPosition.Z:F2} 追い風Z={tailwind.LandingPosition.Z:F2}");
    }

    [Fact]
    public void Headwind_DecreasesCarryDistance()
    {
        // 向かい風（打球の進行方向と逆=−Zへ吹く）は相対速度を上げ、抗力が強まる＝縮む。
        var aero = new Aerodynamics();
        var contact = new Vector3D(0, 1.0, 0);
        var v0 = FlyBallVelocity(40.0, 28.0);

        var noWind = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero);
        var headwind = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero, wind: new Vector3D(0, 0, -5.0));

        Assert.True(headwind.LandingPosition.Z < noWind.LandingPosition.Z,
            $"向かい風5m/sで飛距離が縮むはず: 無風Z={noWind.LandingPosition.Z:F2} 向かい風Z={headwind.LandingPosition.Z:F2}");
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(-5.0)]
    public void Crosswind_DeflectsLandingPoint_InTheDirectionTheWindBlows(double windX)
    {
        // 横風（+X＝一塁/右翼側へ吹く）は打球をその方向へ流す（符号が正しいこと）。
        var aero = new Aerodynamics();
        var contact = new Vector3D(0, 1.0, 0);
        var v0 = FlyBallVelocity(40.0, 28.0);   // 中堅方向(X=0)へ打つ＝風の効きだけを見る

        var noWind = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero);
        var crosswind = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero, wind: new Vector3D(windX, 0, 0));

        Assert.Equal(0.0, noWind.LandingPosition.X, 9);   // 無風は横に流れない
        if (windX > 0)
            Assert.True(crosswind.LandingPosition.X > 0, $"+X の風は+X側へ流すはず: X={crosswind.LandingPosition.X:F2}");
        else
            Assert.True(crosswind.LandingPosition.X < 0, $"-X の風は-X側へ流すはず: X={crosswind.LandingPosition.X:F2}");
    }

    [Fact]
    public void StrongerCrosswind_DeflectsFurther()
    {
        // 風が強いほど流れが大きい（単調性）。
        var aero = new Aerodynamics();
        var contact = new Vector3D(0, 1.0, 0);
        var v0 = FlyBallVelocity(40.0, 28.0);

        var light = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero, wind: new Vector3D(2.0, 0, 0));
        var strong = BallisticIntegrator.IntegrateToGround(contact, v0, Vector3D.Zero, aero, wind: new Vector3D(6.0, 0, 0));

        Assert.True(strong.LandingPosition.X > light.LandingPosition.X,
            $"風が強いほど流れが大きいはず: 2m/s={light.LandingPosition.X:F2} 6m/s={strong.LandingPosition.X:F2}");
    }

}
