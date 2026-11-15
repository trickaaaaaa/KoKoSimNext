using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Nation.Tournaments;
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

    /// <summary>
    /// 屋内の球場（札幌ドーム・issue #1020）は天候・気温を効かせない＝ctx をそのまま返す（基準の空気密度・消耗、
    /// ResolvedWeather も null＝天気欄を出さない）。真夏の分布を渡しても同じ。
    /// </summary>
    [Fact]
    public void ApplyForMatch_IndoorStadium_IgnoresWeather()
    {
        var dome = KokoSim.Engine.Match.Field.Stadium.AutumnFinals[0];
        Assert.True(dome.Indoor);
        foreach (var month in new[] { 1, 8, 9 })
        {
            var ctx = new GameContext { Weather = WeatherModel.ForMonth(month), Field = dome.ToFieldGeometry() };
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var applied = WeatherModel.ApplyForMatch(ctx, new Xoshiro256Random(seed));
                Assert.Same(ctx, applied);
                Assert.Null(applied.ResolvedWeather);
            }
        }
    }

    /// <summary>
    /// 屋内の試合は「天候なし」の試合と1ビットも違わない（気温の分布が何であっても）。屋外では違う＝球場の屋内フラグが効いている。
    /// </summary>
    [Fact]
    public void IndoorMatch_IsIdenticalToAMatchWithoutWeather()
    {
        var dome = KokoSim.Engine.Match.Field.Stadium.AutumnFinals[0].ToFieldGeometry();
        var outdoor = dome with { Indoor = false };
        var hot = new WeatherCoefficients { MeanTemperatureC = 36, MinTemperatureC = 36, MaxTemperatureC = 36 };
        string Play(GameContext ctx, ulong seed)
        {
            var g = new Xoshiro256Random(seed);
            var away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "先攻", g.Fork(1));
            var home = KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "後攻", g.Fork(2));
            return GameResultDigest.Sha256Of(GameEngine.Play(away, home, ctx, g.Fork(3)));
        }
        var outdoorDiffers = false;
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var none = Play(new GameContext { Field = dome }, seed);
            Assert.Equal(none, Play(new GameContext { Field = dome, Weather = hot }, seed));
            outdoorDiffers |= Play(new GameContext { Field = outdoor }, seed) != Play(new GameContext { Field = outdoor, Weather = hot }, seed);
        }
        Assert.True(outdoorDiffers, "屋外で36℃を渡しても4試合とも結果が同じ＝気温が効いていない疑い");
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

    /// <summary>
    /// Issue #803: 試合開始時に一度だけ抽選した天気（状態＋気温）を <see cref="GameContext.ResolvedWeather"/> へ
    /// 写す＝ホーム画面・電光掲示板はこのコピーを読むだけで済み、再抽選しない（二重に抽選すると値がずれる、
    /// <see cref="WeatherModel.SampleWeather"/> は呼ぶたびに親のFork元状態から引き直すため）。
    /// </summary>
    [Fact]
    public void ApplyForMatch_StoresResolvedWeather_MatchingSampleForMatch()
    {
        var ctx = new GameContext { Weather = Summer };
        var expected = WeatherModel.SampleForMatch(new Xoshiro256Random(55), Summer);
        var applied = WeatherModel.ApplyForMatch(ctx, new Xoshiro256Random(55));
        Assert.Equal(expected, applied.ResolvedWeather);
    }

    [Fact]
    public void ApplyForMatch_WhenWeatherNull_ResolvedWeatherStaysNull()
    {
        var ctx = new GameContext();
        var applied = WeatherModel.ApplyForMatch(ctx, new Xoshiro256Random(1));
        Assert.Null(applied.ResolvedWeather);
    }

    // ===== Issue #971: 月別の表（試合日の月で引く）=====

    [Fact]
    public void ForMonth_AugustIsTheSummerKoshienDistribution_UnchangedFromBefore()
    {
        // games_10k_summer が測る8月・甲子園の 32℃・σ2.5・28〜36℃ は動かさない（帯を動かさない決定）。
        var aug = WeatherModel.ForMonth(8);
        Assert.Equal(32.0, aug.MeanTemperatureC, 6);
        Assert.Equal(2.5, aug.TemperatureStdDevC, 6);
        Assert.Equal(28.0, aug.MinTemperatureC, 6);
        Assert.Equal(36.0, aug.MaxTemperatureC, 6);
        Assert.Equal(0.15, aug.RainProb, 6);
        Assert.Equal(WeatherModel.Base, aug);
    }

    [Fact]
    public void ForMonth_AutumnMonthsFollowTheCalendar_SeptemberHotterThanOctoberThanNovember()
    {
        // 9月は約27℃・10月は約22℃・11月は約17℃（気象庁の平年値・日最高）。秋を1つの分布（22℃）にしていた不具合の回帰。
        Assert.True(WeatherModel.ForMonth(9).MeanTemperatureC > WeatherModel.ForMonth(10).MeanTemperatureC);
        Assert.True(WeatherModel.ForMonth(10).MeanTemperatureC > WeatherModel.ForMonth(11).MeanTemperatureC);
        Assert.Equal(27.0, WeatherModel.ForMonth(9).MeanTemperatureC, 6);
        Assert.Equal(22.0, WeatherModel.ForMonth(10).MeanTemperatureC, 6);
        Assert.Equal(17.0, WeatherModel.ForMonth(11).MeanTemperatureC, 6);
    }

    [Fact]
    public void ForMonth_MidSeptemberMedianTemperature_IsNearDaytimeNormal()
    {
        // 完了条件: 9月中旬の秋季県大会の試合の気温の中央値が日中の平年値（約27℃前後）に近い。
        // 曇り・雨の平均ずれ込みでも 25〜28℃ に収まる。
        var c = WeatherModel.ForMonth(9);
        var temps = Enumerable.Range(0, 4001)
            .Select(i => WeatherModel.SampleWeather(new Xoshiro256Random((ulong)(i + 1)), c).TemperatureC)
            .OrderBy(t => t).ToArray();
        var median = temps[temps.Length / 2];
        Assert.InRange(median, 25.0, 28.0);
    }

    [Fact]
    public void ForMonth_TwelveMonthsCoverTheYear_NoGapAndSensibleBounds()
    {
        Assert.Equal(Enumerable.Range(1, 12), WeatherModel.DefaultMonths.Select(m => m.Month));
        for (var m = 1; m <= 12; m++)
        {
            var c = WeatherModel.ForMonth(m);
            Assert.True(c.Enabled);
            Assert.InRange(c.MeanTemperatureC, c.MinTemperatureC, c.MaxTemperatureC);
            Assert.True(c.TemperatureStdDevC > 0);
        }
        // 冬は涼しく、夏は暑い（1年を通して月の表）。6月は8月より涼しい（6月の練習試合が32℃だった件）。
        Assert.True(WeatherModel.ForMonth(1).MeanTemperatureC < WeatherModel.ForMonth(4).MeanTemperatureC);
        Assert.True(WeatherModel.ForMonth(6).MeanTemperatureC < WeatherModel.ForMonth(7).MeanTemperatureC);
        Assert.True(WeatherModel.ForMonth(7).MeanTemperatureC < WeatherModel.ForMonth(8).MeanTemperatureC);
    }

    [Theory]
    [InlineData(0, 12)] [InlineData(13, 1)] [InlineData(-1, 11)] [InlineData(5, 5)]
    public void NormalizeMonth_WrapsOutOfRangeMonths(int input, int expected)
        => Assert.Equal(expected, WeatherModel.NormalizeMonth(input));

    [Fact]
    public void ForMonth_PhysicsConstantsDoNotDependOnMonth()
    {
        var jan = WeatherModel.ForMonth(1);
        var aug = WeatherModel.ForMonth(8);
        Assert.Equal(aug.BaselineTemperatureC, jan.BaselineTemperatureC);
        Assert.Equal(aug.DensityDamping, jan.DensityDamping);
        Assert.Equal(aug.FatigueAlphaPerDegree, jan.FatigueAlphaPerDegree);
        Assert.Equal(aug.CloudyTemperatureOffsetC, jan.CloudyTemperatureOffsetC);
    }

    [Theory]
    [InlineData(TournamentKind.Summer, 7)]
    [InlineData(TournamentKind.SummerKoshien, 8)]
    [InlineData(TournamentKind.SenbatsuKoshien, 3)]
    [InlineData(TournamentKind.Autumn, 9)]
    [InlineData(TournamentKind.AutumnDistrict, 9)]
    [InlineData(TournamentKind.Regional, 10)]
    [InlineData(TournamentKind.Jingu, 11)]
    public void ForTournament_UsesTheOpeningMonth(TournamentKind kind, int expectedMonth)
        => Assert.Equal(WeatherModel.ForMonth(expectedMonth), WeatherModel.ForTournament(new TournamentSchedule(), kind));

    [Fact]
    public void TemperatureRange_CoversEveryMonthsClamp()
    {
        for (var m = 1; m <= 12; m++)
        {
            Assert.True(WeatherModel.CoolestTemperatureC <= WeatherModel.ForMonth(m).MinTemperatureC);
            Assert.True(WeatherModel.HottestTemperatureC >= WeatherModel.ForMonth(m).MaxTemperatureC);
        }
    }
}
