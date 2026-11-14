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

    /// <summary>東京の準決勝・決勝は明治神宮野球場、それ以外の県は県営級（issue #904）。序盤は県に依らない。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TokyoFinalRounds_UseJingu_OthersPrefLarge(int roundsRemaining)
    {
        Assert.Same(Stadium.Jingu, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), prefectureId: 12));
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), prefectureId: 13));
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1)));
    }

    /// <summary>神奈川の夏の準決勝・決勝は横浜スタジアム。秋・他県・県不明は従来どおり（issue #956）。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void KanagawaSummerFinalRounds_UseYokohama_OthersUnchanged(int roundsRemaining)
    {
        const int kanagawa = 13;
        Assert.Same(Stadium.Yokohama, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), kanagawa, summer: true));
        // 秋（地区予選を含む）は汎用の県営級のまま
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), kanagawa, summer: false));
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), kanagawa));
        // 他県・県不明は夏でも県営級、東京は夏秋とも神宮
        foreach (var pref in new int?[] { null, 11, 14, 46 })
            Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), pref, summer: true));
        Assert.Same(Stadium.Jingu, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 12, summer: true));
        Assert.Same(Stadium.Jingu, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 12, summer: false));
    }

    [Fact]
    public void KanagawaSummerYokohama_UsesTheKanagawaFinalDimensions()
    {
        var geom = RegionalStadiumTier.For(1, new Xoshiro256Random(1), prefectureId: 13, summer: true);
        Assert.Equal(94.2, geom.LeftFenceM, 6);
        Assert.Equal(94.2, geom.RightFenceM, 6);
        Assert.Equal(117.7, geom.CenterFenceM, 6);
        Assert.Equal(5.0, geom.FenceHeightM, 6);
    }

    [Fact]
    public void KanagawaSummerEarlyRounds_DrawTheSameAsOtherPrefectures_AndFinalsDoNotConsumeRandomness()
    {
        for (ulong seed = 1; seed <= 10; seed++)
            Assert.Same(RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), 12, summer: true),
                        RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), 13, summer: true));
        var before = new Xoshiro256Random(99).NextInt(0, int.MaxValue);
        var rng = new Xoshiro256Random(99);
        RegionalStadiumTier.ForStadium(1, rng, 13, summer: true);
        Assert.Equal(before, rng.NextInt(0, int.MaxValue));
    }

    /// <summary>北海道（北北海道・南北海道）の夏の準決勝・決勝は北海道ボールパーク。秋・他県・県不明は従来どおり（issue #959）。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HokkaidoSummerFinalRounds_UseHokkaidoBallpark_OthersUnchanged(int roundsRemaining)
    {
        const int hokkaido = 0;
        Assert.Same(Stadium.HokkaidoBallpark, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), hokkaido, summer: true));
        // 秋（地区予選は県を渡さない）は汎用の県営級のまま
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), hokkaido, summer: false));
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), hokkaido));
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), null, summer: true));
        // 他県の夏は変わらない
        Assert.Same(Stadium.Yokohama, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 13, summer: true));
        Assert.Same(Stadium.Jingu, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 12, summer: true));
        foreach (var pref in new int?[] { 1, 11, 14, 46 })
            Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), pref, summer: true));
    }

    [Fact]
    public void HokkaidoSummerBallpark_UsesAsymmetricDimensions()
    {
        var geom = RegionalStadiumTier.For(1, new Xoshiro256Random(1), prefectureId: 0, summer: true);
        Assert.Equal(97.0, geom.LeftFenceM, 6);
        Assert.Equal(99.0, geom.RightFenceM, 6);
        Assert.Equal(121.0, geom.CenterFenceM, 6);
        Assert.Equal(2.8, geom.FenceHeightM, 6);
    }

    [Fact]
    public void HokkaidoSummerEarlyRounds_DrawTheSameAsOtherPrefectures_AndFinalsDoNotConsumeRandomness()
    {
        for (ulong seed = 1; seed <= 10; seed++)
            Assert.Same(RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), 12, summer: true),
                        RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), 0, summer: true));
        var before = new Xoshiro256Random(99).NextInt(0, int.MaxValue);
        var rng = new Xoshiro256Random(99);
        RegionalStadiumTier.ForStadium(1, rng, 0, summer: true);
        Assert.Equal(before, rng.NextInt(0, int.MaxValue));
    }

    /// <summary>宮城の夏の準決勝・決勝は宮城球場。秋・他県・県不明は従来どおり（issue #961）。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MiyagiSummerFinalRounds_UseMiyagi_OthersUnchanged(int roundsRemaining)
    {
        const int miyagi = 3;
        Assert.Same(Stadium.Miyagi, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), miyagi, summer: true));
        // 秋（決勝は仙台市民球場だが今回は汎用のまま）・県不明は県営級
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), miyagi, summer: false));
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), miyagi));
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), null, summer: true));
        // 他県の夏は変わらない
        Assert.Same(Stadium.HokkaidoBallpark, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 0, summer: true));
        Assert.Same(Stadium.Yokohama, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 13, summer: true));
        Assert.Same(Stadium.Jingu, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 12, summer: true));
        foreach (var pref in new int?[] { 1, 2, 4, 11, 14, 46 })
            Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), pref, summer: true));
    }

    [Fact]
    public void MiyagiSummerStadium_UsesItsDimensions()
    {
        var geom = RegionalStadiumTier.For(1, new Xoshiro256Random(1), prefectureId: 3, summer: true);
        Assert.Equal(100.1, geom.LeftFenceM, 6);
        Assert.Equal(100.1, geom.RightFenceM, 6);
        Assert.Equal(122.0, geom.CenterFenceM, 6);
        Assert.Equal(2.8, geom.FenceHeightM, 6);
    }

    [Fact]
    public void MiyagiSummerEarlyRounds_DrawTheSameAsOtherPrefectures_AndFinalsDoNotConsumeRandomness()
    {
        for (ulong seed = 1; seed <= 10; seed++)
            Assert.Same(RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), 12, summer: true),
                        RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), 3, summer: true));
        var before = new Xoshiro256Random(99).NextInt(0, int.MaxValue);
        var rng = new Xoshiro256Random(99);
        RegionalStadiumTier.ForStadium(1, rng, 3, summer: true);
        Assert.Equal(before, rng.NextInt(0, int.MaxValue));
    }

    [Fact]
    public void TokyoEarlyRounds_StayMunicipal_AndDrawTheSameAsOtherPrefectures()
    {
        // 序盤の抽選は県に依らない＝東京を足しても乱数の引き方・結果が他県と同じ
        for (ulong seed = 1; seed <= 10; seed++)
            Assert.Same(RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), prefectureId: 13),
                        RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), prefectureId: 12));
    }

    [Fact]
    public void FinalRounds_DoNotConsumeRandomness()
    {
        // 準決勝・決勝は乱数を引かない＝県ごとの球場に差し替えても乱数列が変わらない
        var rng = new Xoshiro256Random(99);
        var before = rng.NextInt(0, int.MaxValue);
        var rng2 = new Xoshiro256Random(99);
        RegionalStadiumTier.ForStadium(1, rng2, prefectureId: 12);
        Assert.Equal(before, rng2.NextInt(0, int.MaxValue));
    }
}
