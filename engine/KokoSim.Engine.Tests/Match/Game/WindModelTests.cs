using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 試合ごとの風モデル（issue #1614）。強さ・向きの抽選、球場固有の癖（甲子園の浜風）、
/// 専用Forkによる決定論・帯不変（Weather無効時）を固定する。
/// </summary>
public sealed class WindModelTests
{
    private static readonly WeatherCoefficients WeatherOn = new() { Enabled = true };
    private static readonly WindCoefficients WindOn = new() { Enabled = true };

    // ===== 強さ（0〜6 m/s・強風は稀）=====

    [Fact]
    public void SampleSpeed_IsClampedToRange()
    {
        for (ulong seed = 0; seed < 2000; seed++)
        {
            var s = WindModel.SampleSpeedMps(new Xoshiro256Random(seed), WindOn);
            Assert.InRange(s, 0.0, WindOn.MaxSpeedMps);
        }
    }

    [Fact]
    public void SampleSpeed_MostlyLight_RarelyStrong()
    {
        // 2026-11-28 オーナー決定: ほとんどの日は0〜3m/s、たまに5〜6m/s。
        int light = 0, strong = 0;
        const int n = 20000;
        var rng = new Xoshiro256Random(3);
        for (var i = 0; i < n; i++)
        {
            var s = WindModel.SampleSpeedMps(rng, WindOn);
            if (s <= 3.0) light++;
            if (s >= 5.0) strong++;
        }
        Assert.True((double)light / n > 0.8, $"0〜3m/sが大半のはず: {(double)light / n:P1}");
        Assert.True((double)strong / n < 0.05, $"5〜6m/sは稀のはず: {(double)strong / n:P1}");
        Assert.True(strong > 0, "たまには5〜6m/sも出るはず（0件は振り過ぎ）");
    }

    [Fact]
    public void SampleSpeed_IsDeterministic()
    {
        var a = WindModel.SampleSpeedMps(new Xoshiro256Random(42), WindOn);
        var b = WindModel.SampleSpeedMps(new Xoshiro256Random(42), WindOn);
        Assert.Equal(a, b);
    }

    // ===== 向き（球場の癖が無ければ一様乱数＝平均すると無風と同じ）=====

    [Fact]
    public void SampleBearing_WithoutStadiumBias_IsUniform()
    {
        var field = new FieldGeometry();   // WindBiasBearingDeg = null
        var rng = new Xoshiro256Random(11);
        double sumX = 0, sumZ = 0;
        const int n = 20000;
        for (var i = 0; i < n; i++)
        {
            var b = WindModel.SampleBearingRad(rng, WindOn, field);
            Assert.InRange(b, 0.0, 2.0 * System.Math.PI);
            sumX += System.Math.Sin(b);
            sumZ += System.Math.Cos(b);
        }
        // 一様分布なら単位円上のベクトル平均はほぼゼロ（打ち消し合う＝平均すると無風と同じ）。
        Assert.InRange(sumX / n, -0.03, 0.03);
        Assert.InRange(sumZ / n, -0.03, 0.03);
    }

    [Fact]
    public void SampleBearing_WithStadiumBias_ClustersAroundIt()
    {
        var field = new FieldGeometry { WindBiasBearingDeg = -90.0 };   // 甲子園の浜風相当
        var rng = new Xoshiro256Random(21);
        var biasRad = -90.0 * System.Math.PI / 180.0;
        var within = 0;
        const int n = 5000;
        for (var i = 0; i < n; i++)
        {
            var b = WindModel.SampleBearingRad(rng, WindOn, field);
            var diff = System.Math.Abs(Normalize(b - biasRad));
            if (diff < 60.0 * System.Math.PI / 180.0) within++;
        }
        Assert.True((double)within / n > 0.7, $"癖のある球場は平均方位角の±60°に大半が収まるはず: {(double)within / n:P1}");
    }

    private static double Normalize(double rad)
    {
        while (rad > System.Math.PI) rad -= 2.0 * System.Math.PI;
        while (rad < -System.Math.PI) rad += 2.0 * System.Math.PI;
        return rad;
    }

    [Fact]
    public void SampleWind_IsDeterministic()
    {
        var field = new FieldGeometry();
        var a = WindModel.SampleWind(new Xoshiro256Random(99), WindOn, field);
        var b = WindModel.SampleWind(new Xoshiro256Random(99), WindOn, field);
        Assert.Equal(a, b);
    }

    [Fact]
    public void WindSample_VelocityMatchesFieldGeometryBearingConvention()
    {
        // FieldGeometry.FenceDistance と同じ規約: 0=中堅(+Z), +90°=右翼(+X), -90°=左翼(-X)。
        var right = new WindSample(System.Math.PI / 2.0, 4.0);
        Assert.Equal(4.0, right.Velocity.X, 9);
        Assert.Equal(0.0, right.Velocity.Z, 9);

        var center = new WindSample(0.0, 4.0);
        Assert.Equal(0.0, center.Velocity.X, 9);
        Assert.Equal(4.0, center.Velocity.Z, 9);
    }

    // ===== 配線（ApplyForMatch）: Weather 無効なら不変・屋内は無風・Fork は本体の乱数順を進めない =====

    [Fact]
    public void ApplyForMatch_WhenWeatherNull_ReturnsSameContext()
    {
        var ctx = new GameContext();
        Assert.Same(ctx, WindModel.ApplyForMatch(ctx, new Xoshiro256Random(1)));
    }

    [Fact]
    public void ApplyForMatch_WhenWeatherDisabled_ReturnsSameContext()
    {
        var ctx = new GameContext { Weather = new WeatherCoefficients { Enabled = false } };
        Assert.Same(ctx, WindModel.ApplyForMatch(ctx, new Xoshiro256Random(1)));
    }

    [Fact]
    public void ApplyForMatch_WhenWindDisabled_ReturnsSameContext()
    {
        // 天候(気温)は有効でも風だけ個別に切れる。
        var ctx = new GameContext { Weather = WeatherOn, Wind = new WindCoefficients { Enabled = false } };
        Assert.Same(ctx, WindModel.ApplyForMatch(ctx, new Xoshiro256Random(1)));
    }

    [Fact]
    public void ApplyForMatch_IndoorStadium_IgnoresWind()
    {
        var dome = Stadium.AutumnFinals[0];
        Assert.True(dome.ToFieldGeometry().Indoor);
        var ctx = new GameContext { Weather = WeatherOn, Field = dome.ToFieldGeometry() };
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var applied = WindModel.ApplyForMatch(ctx, new Xoshiro256Random(seed));
            Assert.Same(ctx, applied);
            Assert.Null(applied.ResolvedWind);
        }
    }

    [Fact]
    public void ApplyForMatch_WhenEnabled_SetsFieldWindAndResolvedWind()
    {
        var ctx = new GameContext { Weather = WeatherOn };
        var applied = WindModel.ApplyForMatch(ctx, new Xoshiro256Random(7));
        Assert.NotNull(applied.ResolvedWind);
        Assert.Equal(applied.ResolvedWind!.Velocity.X, applied.Field.Wind.X, 9);
        Assert.Equal(applied.ResolvedWind!.Velocity.Z, applied.Field.Wind.Z, 9);
    }

    [Fact]
    public void ApplyForMatch_DoesNotAdvanceMainRngStream()
    {
        // 不変条件#2: 風生成の Fork は親 rng の状態を進めない＝本体の乱数順・帯は不変。
        var rng = new Xoshiro256Random(12345);
        var ctx = new GameContext { Weather = WeatherOn };
        _ = WindModel.ApplyForMatch(ctx, rng);

        var reference = new Xoshiro256Random(12345);
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(reference.NextUInt64(), rng.NextUInt64());
        }
    }

    [Fact]
    public void SampleForMatch_IsWhatTheMatchUses()
    {
        // 画面に出す風（SampleForMatch）と、試合に効く風（ApplyForMatch）は同じストリーム＝同じ値。
        var ctx = new GameContext { Weather = WeatherOn };
        var sample = WindModel.SampleForMatch(new Xoshiro256Random(99), WindOn, ctx.Field);
        var applied = WindModel.ApplyForMatch(ctx, new Xoshiro256Random(99));
        Assert.Equal(sample, applied.ResolvedWind);
    }

    // ===== 球場固有の風の癖（甲子園の浜風） =====

    [Fact]
    public void Koshien_HasWindBias_OtherStadiumsDoNot()
    {
        Assert.Equal(-90.0, Stadium.Koshien.WindBearingDeg);
        Assert.Null(Stadium.PrefLarge.WindBearingDeg);
        Assert.Null(Stadium.MunicipalSmall01.WindBearingDeg);
        Assert.Null(Stadium.Jingu.WindBearingDeg);
    }

    [Fact]
    public void Koshien_ToFieldGeometry_CarriesTheWindBias()
    {
        Assert.Equal(Stadium.Koshien.WindBearingDeg, Stadium.Koshien.ToFieldGeometry().WindBiasBearingDeg);
    }
}
