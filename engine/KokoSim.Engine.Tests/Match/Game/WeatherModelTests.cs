using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 試合ごとの天候（気温）モデル（Issue #120）。気温→空気密度（決定1=案B・減衰k）／
/// 気温→投手消耗（決定2=案A・線形加算）の純関数と、専用Forkによる決定論・帯不変を固定する。
/// </summary>
public sealed class WeatherModelTests
{
    private static readonly WeatherCoefficients Summer = new() { Enabled = true };

    // ===== 決定1: 気温→空気密度 =====

    [Fact]
    public void AirDensity_AtBaselineTemperature_EqualsBaseDensity()
    {
        // 基準気温（15℃）ではρは既定値そのまま（差分ゼロ）。
        var rho = WeatherModel.AirDensityAt(Summer.BaselineTemperatureC, 1.225, Summer);
        Assert.Equal(1.225, rho, 9);
    }

    [Fact]
    public void AirDensity_HotterAir_IsLessDense()
    {
        // 暑いほどρは下がる（打球が伸びる方向）。
        var rho32 = WeatherModel.AirDensityAt(32.0, 1.225, Summer);
        Assert.True(rho32 < 1.225, $"32℃のρ {rho32} は基準1.225より小さいはず");
        var rho36 = WeatherModel.AirDensityAt(36.0, 1.225, Summer);
        Assert.True(rho36 < rho32, "36℃は32℃よりさらに低密度のはず");
    }

    [Fact]
    public void AirDensity_DampingHalvesThePhysicalDelta()
    {
        // 案B: 物理式の基準差分に k=0.5 を掛ける＝物理そのまま(k=1)の丁度半分の差分になる。
        const double t = 34.0;
        var physical = 1.225 * (273.15 + 15.0) / (273.15 + t);     // k=1 相当
        var damped = WeatherModel.AirDensityAt(t, 1.225, Summer with { DensityDamping = 0.5 });
        var expected = 1.225 + 0.5 * (physical - 1.225);
        Assert.Equal(expected, damped, 9);
        // 減衰0なら一切動かない。
        Assert.Equal(1.225, WeatherModel.AirDensityAt(t, 1.225, Summer with { DensityDamping = 0.0 }), 9);
    }

    [Fact]
    public void ApplyToAerodynamics_OnlyChangesAirDensity()
    {
        var aero = new Aerodynamics();
        var hot = WeatherModel.ApplyTo(aero, 33.0, Summer);
        Assert.NotEqual(aero.AirDensity, hot.AirDensity);
        Assert.Equal(aero.DragCoefficient, hot.DragCoefficient);
        Assert.Equal(aero.BallMassKg, hot.BallMassKg);
    }

    // ===== 決定2: 気温→投手消耗 =====

    [Fact]
    public void Fatigue_AtOrBelowBaseline_IsUnchanged()
    {
        var f = new FatigueCoefficients();
        var cool = WeatherModel.ApplyTo(f, 28.0, Summer);   // 基準温＝加算ゼロ
        Assert.Equal(f.VelocityDropPerOverPitch, cool.VelocityDropPerOverPitch, 12);
        Assert.Equal(f.ControlDropPerOverPitch, cool.ControlDropPerOverPitch, 12);
        var colder = WeatherModel.ApplyTo(f, 20.0, Summer); // 基準未満でも max(0,・) で加算ゼロ
        Assert.Equal(f.VelocityDropPerOverPitch, colder.VelocityDropPerOverPitch, 12);
    }

    [Fact]
    public void Fatigue_At36Degrees_IsEightPercentHigher()
    {
        // α=0.01/℃, 基準28℃ → 36℃で (1 + 0.01×8) = 1.08 倍。
        var f = new FatigueCoefficients();
        var hot = WeatherModel.ApplyTo(f, 36.0, Summer);
        Assert.Equal(f.VelocityDropPerOverPitch * 1.08, hot.VelocityDropPerOverPitch, 12);
        Assert.Equal(f.ControlDropPerOverPitch * 1.08, hot.ControlDropPerOverPitch, 12);
    }

    // ===== 気温生成（決定論・クランプ） =====

    [Fact]
    public void SampleTemperature_IsClampedToRange()
    {
        for (ulong seed = 0; seed < 500; seed++)
        {
            var t = WeatherModel.SampleTemperatureC(new Xoshiro256Random(seed), Summer);
            Assert.InRange(t, Summer.MinTemperatureC, Summer.MaxTemperatureC);
        }
    }

    [Fact]
    public void SampleTemperature_IsDeterministic()
    {
        var a = WeatherModel.SampleTemperatureC(new Xoshiro256Random(42), Summer);
        var b = WeatherModel.SampleTemperatureC(new Xoshiro256Random(42), Summer);
        Assert.Equal(a, b);
    }

    // ===== 配線（ApplyForMatch）: 無効なら不変・Fork は本体の乱数順を進めない =====

    [Fact]
    public void ApplyForMatch_WhenWeatherNull_ReturnsSameContext()
    {
        var ctx = new GameContext();
        Assert.Same(ctx, WeatherModel.ApplyForMatch(ctx, new Xoshiro256Random(1)));
    }

    [Fact]
    public void ApplyForMatch_WhenDisabled_ReturnsSameContext()
    {
        var ctx = new GameContext { Weather = new WeatherCoefficients { Enabled = false } };
        Assert.Same(ctx, WeatherModel.ApplyForMatch(ctx, new Xoshiro256Random(1)));
    }

    [Fact]
    public void ApplyForMatch_WhenEnabled_AdjustsAeroAndFatigue()
    {
        var ctx = new GameContext { Weather = Summer };
        var adjusted = WeatherModel.ApplyForMatch(ctx, new Xoshiro256Random(7));
        Assert.NotEqual(ctx.Aerodynamics.AirDensity, adjusted.Aerodynamics.AirDensity);
        Assert.NotEqual(ctx.Fatigue.VelocityDropPerOverPitch, adjusted.Fatigue.VelocityDropPerOverPitch);
    }

    [Fact]
    public void ApplyForMatch_DoesNotAdvanceMainRngStream()
    {
        // 不変条件#2: 気温生成の Fork は親 rng の状態を進めない＝本体の乱数順・帯は不変。
        var rng = new Xoshiro256Random(12345);
        var ctx = new GameContext { Weather = Summer };
        _ = WeatherModel.ApplyForMatch(ctx, rng);

        var reference = new Xoshiro256Random(12345);
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(reference.NextUInt64(), rng.NextUInt64());
        }
    }
}
