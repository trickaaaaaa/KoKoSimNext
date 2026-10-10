using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

/// <summary>
/// 地方大会（夏・秋の県大会）の汎用ティア球場判定（設計書13 §1・「地方大会の球場割当」issue）。
/// 序盤=市営級（決定論抽選）、準決勝・決勝=県営級。純関数・決定論。
/// </summary>
public sealed class RegionalStadiumTierTests
{
    [Theory]
    [InlineData(1)]   // 決勝
    [InlineData(2)]   // 準決勝
    public void FinalRounds_UsePrefLarge(int roundsRemaining)
    {
        var geom = RegionalStadiumTier.For(roundsRemaining, new Xoshiro256Random(1));
        var expected = Stadium.PrefLarge.ToFieldGeometry();
        Assert.Equal(expected.LeftFenceM, geom.LeftFenceM, 6);
        Assert.Equal(expected.CenterFenceM, geom.CenterFenceM, 6);
        Assert.Equal(expected.FenceHeightM, geom.FenceHeightM, 6);
    }

    [Theory]
    [InlineData(3)]   // 準々決勝
    [InlineData(10)]  // 序盤
    public void EarlyRounds_UseOneOfTheMunicipalStadiums(int roundsRemaining)
    {
        var geom = RegionalStadiumTier.For(roundsRemaining, new Xoshiro256Random(1));
        var m1 = Stadium.MunicipalSmall01.ToFieldGeometry();
        var m2 = Stadium.MunicipalSmall02.ToFieldGeometry();
        var matchesM1 = geom.LeftFenceM == m1.LeftFenceM && geom.CenterFenceM == m1.CenterFenceM;
        var matchesM2 = geom.LeftFenceM == m2.LeftFenceM && geom.CenterFenceM == m2.CenterFenceM;
        Assert.True(matchesM1 || matchesM2, $"序盤なのに市営級2種のどちらでもない: L={geom.LeftFenceM} C={geom.CenterFenceM}");
    }

    [Fact]
    public void NullRoundsRemaining_TreatedAsEarly()
    {
        var geom = RegionalStadiumTier.For(null, new Xoshiro256Random(1));
        var pref = Stadium.PrefLarge.ToFieldGeometry();
        Assert.NotEqual(pref.CenterFenceM, geom.CenterFenceM);
    }

    [Fact]
    public void NullRng_FallsBackToFixedMunicipalStadium()
    {
        var geom = RegionalStadiumTier.For(roundsRemaining: 5, rng: null);
        var expected = Stadium.MunicipalSmall01.ToFieldGeometry();
        Assert.Equal(expected.LeftFenceM, geom.LeftFenceM, 6);
        Assert.Equal(expected.CenterFenceM, geom.CenterFenceM, 6);
    }

    [Fact]
    public void BothMunicipalStadiums_AreReachable_AcrossSeeds()
    {
        // 決定論抽選が両方を引けること（偏って片方しか出ないと「2種用意した意味」がない）。
        var seenM1 = false;
        var seenM2 = false;
        var m1 = Stadium.MunicipalSmall01.ToFieldGeometry();
        for (ulong seed = 1; seed <= 20 && !(seenM1 && seenM2); seed++)
        {
            var geom = RegionalStadiumTier.For(roundsRemaining: 8, new Xoshiro256Random(seed));
            if (geom.LeftFenceM == m1.LeftFenceM) seenM1 = true; else seenM2 = true;
        }
        Assert.True(seenM1 && seenM2, "20シード回しても市営級2種の片方しか出ていない");
    }

    [Fact]
    public void IsDeterministic_SameSeedSameStadium()
    {
        var a = RegionalStadiumTier.For(6, new Xoshiro256Random(42));
        var b = RegionalStadiumTier.For(6, new Xoshiro256Random(42));
        Assert.Equal(a.LeftFenceM, b.LeftFenceM, 6);
        Assert.Equal(a.CenterFenceM, b.CenterFenceM, 6);
    }
}
