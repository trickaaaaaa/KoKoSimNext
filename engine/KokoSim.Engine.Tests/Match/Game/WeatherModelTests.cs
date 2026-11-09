using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Season;
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

    // ===== Issue #803: 天気の状態（晴れ/曇り/雨）は気温と同じ専用ストリームで別に抽選する =====

    [Fact]
    public void SampleState_FrequenciesMatchProbabilities()
    {
        int rain = 0, cloudy = 0;
        const int n = 20000;
        var rng = new Xoshiro256Random(7);
        for (var i = 0; i < n; i++)
        {
            var st = WeatherModel.SampleState(rng, Summer);
            if (st == WeatherState.Rainy) rain++;
            else if (st == WeatherState.Cloudy) cloudy++;
        }
        Assert.InRange((double)rain / n, Summer.RainProb - 0.01, Summer.RainProb + 0.01);
        Assert.InRange((double)cloudy / n, Summer.CloudyProb - 0.015, Summer.CloudyProb + 0.015);
    }

    [Fact]
    public void SampleWeather_HotRainyDaysStillOccur()
    {
        // 気温から状態を決める作りだと「暑い日は必ず晴れ」になる。抽選なら平均以上の雨の日も出る。
        var hotRain = 0;
        for (ulong seed = 0; seed < 4000; seed++)
        {
            var s = WeatherModel.SampleWeather(new Xoshiro256Random(seed), Summer);
            if (s.State == WeatherState.Rainy && s.TemperatureC >= Summer.MeanTemperatureC) hotRain++;
        }
        Assert.True(hotRain > 0, "平均気温以上の雨の日が一度も出ない");
    }

    [Fact]
    public void SampleWeather_RainCorrelatesWithLowerTemperature()
    {
        // 決定1: 雨の日は気温が低い（雨の日は気温の平均を下げる）。
        double rainySum = 0, sunnySum = 0;
        int rainyCount = 0, sunnyCount = 0;
        for (ulong seed = 0; seed < 2000; seed++)
        {
            var sample = WeatherModel.SampleWeather(new Xoshiro256Random(seed), Summer);
            if (sample.State == WeatherState.Rainy) { rainySum += sample.TemperatureC; rainyCount++; }
            if (sample.State == WeatherState.Sunny) { sunnySum += sample.TemperatureC; sunnyCount++; }
        }
        Assert.True(rainyCount > 0 && sunnyCount > 0, "2000シードの中に雨・晴れの両方が出るはず");
        Assert.True(rainySum / rainyCount < sunnySum / sunnyCount, "雨の日の平均気温は晴れの日より低いはず");
    }

    [Fact]
    public void SampleWeather_IsDeterministic()
    {
        var a = WeatherModel.SampleWeather(new Xoshiro256Random(99), Summer);
        var b = WeatherModel.SampleWeather(new Xoshiro256Random(99), Summer);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SampleForMatch_TemperatureIsWhatTheMatchUses()
    {
        // 画面に出す天気（SampleForMatch）と、試合に効く気温（ApplyForMatch）は同じストリーム＝同じ気温。
        var ctx = new GameContext { Weather = Summer };
        var sample = WeatherModel.SampleForMatch(new Xoshiro256Random(99), Summer);
        var applied = WeatherModel.ApplyForMatch(ctx, new Xoshiro256Random(99));
        var expected = WeatherModel.ApplyTo(ctx, sample.TemperatureC);
        Assert.Equal(expected.Aerodynamics, applied.Aerodynamics);
        Assert.Equal(expected.Fatigue, applied.Fatigue);
    }

    // ===== Issue #803: 季節別プリセットと季節判定 =====

    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void SeasonFor_SpringMonths_ReturnSpring(int month)
        => Assert.Equal(WeatherSeason.Spring, WeatherModel.SeasonFor(month));

    [Theory]
    [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void SeasonFor_SummerMonths_ReturnSummer(int month)
        => Assert.Equal(WeatherSeason.Summer, WeatherModel.SeasonFor(month));

    [Theory]
    [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void SeasonFor_AutumnMonths_ReturnAutumn(int month)
        => Assert.Equal(WeatherSeason.Autumn, WeatherModel.SeasonFor(month));

    [Theory]
    [InlineData(TournamentKind.Summer, WeatherSeason.Summer)]
    [InlineData(TournamentKind.SummerKoshien, WeatherSeason.Summer)]
    [InlineData(TournamentKind.SenbatsuKoshien, WeatherSeason.Spring)]
    [InlineData(TournamentKind.Autumn, WeatherSeason.Autumn)]
    [InlineData(TournamentKind.AutumnDistrict, WeatherSeason.Autumn)]
    [InlineData(TournamentKind.Regional, WeatherSeason.Autumn)]
    [InlineData(TournamentKind.Jingu, WeatherSeason.Autumn)]
    public void SeasonFor_TournamentKind_MapsToExpectedSeason(TournamentKind kind, WeatherSeason expected)
        => Assert.Equal(expected, WeatherModel.SeasonFor(kind));

    [Fact]
    public void ForSeason_ReturnsDistinctTemperatureProfilesPerSeason()
    {
        // 決定4: 季節別分布・地域差なし。平均気温は 春 < 秋 < 夏。
        var spring = WeatherModel.ForSeason(WeatherSeason.Spring);
        var summer = WeatherModel.ForSeason(WeatherSeason.Summer);
        var autumn = WeatherModel.ForSeason(WeatherSeason.Autumn);
        Assert.True(spring.MeanTemperatureC < autumn.MeanTemperatureC);
        Assert.True(autumn.MeanTemperatureC < summer.MeanTemperatureC);
        Assert.True(spring.Enabled && summer.Enabled && autumn.Enabled);
    }
}
