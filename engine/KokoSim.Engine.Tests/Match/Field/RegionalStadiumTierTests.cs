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
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), prefectureId: 10));   // 埼玉（秋の会場が夏と同じ県）
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
        // 秋は保土ケ谷球場（issue #1020。地区予選は県を渡さない＝県営級）
        Assert.Same(Stadium.AutumnFinals[kanagawa], RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), kanagawa, summer: false));
        Assert.Same(Stadium.AutumnFinals[kanagawa], RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), kanagawa));
        Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), null, summer: false));
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
        // 秋は札幌ドーム（issue #1020。地区予選は県を渡さない＝県営級）
        Assert.Same(Stadium.AutumnFinals[hokkaido], RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), hokkaido, summer: false));
        Assert.Same(Stadium.AutumnFinals[hokkaido], RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), hokkaido));
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
        // 秋は仙台市民球場（issue #1020）・県不明は県営級
        Assert.Same(Stadium.AutumnFinals[miyagi], RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), miyagi, summer: false));
        Assert.Same(Stadium.AutumnFinals[miyagi], RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), miyagi));
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

    /// <summary>広島の夏の準決勝・決勝は広島市民球場。秋・他県・県不明は従来どおり（issue #963）。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HiroshimaSummerFinalRounds_UseHiroshima_OthersUnchanged(int roundsRemaining)
    {
        const int hiroshima = 33;
        Assert.Same(Stadium.Hiroshima, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), hiroshima, summer: true));
        // 秋はみよし運動公園野球場（issue #1020）
        Assert.Same(Stadium.AutumnFinals[hiroshima], RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), hiroshima, summer: false));
        Assert.Same(Stadium.AutumnFinals[hiroshima], RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), hiroshima));
        // 他県の夏は変わらない
        Assert.Same(Stadium.Miyagi, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 3, summer: true));
        Assert.Same(Stadium.Yokohama, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 13, summer: true));
        Assert.Same(Stadium.Jingu, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), 12, summer: true));
        foreach (var pref in new int?[] { 1, 2, 4, 32, 34, 46 })
            Assert.Same(Stadium.PrefLarge, RegionalStadiumTier.ForStadium(roundsRemaining, new Xoshiro256Random(1), pref, summer: true));
    }

    [Fact]
    public void HiroshimaSummerStadium_UsesItsDimensions()
    {
        var geom = RegionalStadiumTier.For(1, new Xoshiro256Random(1), prefectureId: 33, summer: true);
        Assert.Equal(101.0, geom.LeftFenceM, 6);
        Assert.Equal(100.0, geom.RightFenceM, 6);
        Assert.Equal(122.0, geom.CenterFenceM, 6);
        Assert.Equal(3.0, geom.FenceHeightM, 6);
    }

    [Fact]
    public void HiroshimaSummerEarlyRounds_DrawTheSameAsOtherPrefectures_AndFinalsDoNotConsumeRandomness()
    {
        for (ulong seed = 1; seed <= 10; seed++)
            Assert.Same(RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), 12, summer: true),
                        RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), 33, summer: true));
        var before = new Xoshiro256Random(99).NextInt(0, int.MaxValue);
        var rng = new Xoshiro256Random(99);
        RegionalStadiumTier.ForStadium(1, rng, 33, summer: true);
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

    // ── 秋季県大会の準決勝・決勝（issue #1020・2026-11-15 監督決定 (a)）──

    /// <summary>
    /// 秋の準決勝・決勝の会場の表（県ID → 期待する球場名。null＝汎用の県営級）。調査（stadiums-research.md・data/stadiums.yaml の
    /// <c>&lt;県&gt;_autumn_final</c>）で夏と会場が違った17県だけが実在の会場、東京は神宮、残りは県営級。
    /// </summary>
    public static TheoryData<int, string?> AutumnVenues() => new()
    {
        { 0, "札幌ドーム" }, { 1, null }, { 2, null }, { 3, "仙台市民球場" }, { 4, null }, { 5, null },
        { 6, "いわきグリーンスタジアム" }, { 7, "ひたちなか市民球場" }, { 8, "宇都宮清原球場" }, { 9, "桐生球場" },
        { 10, null }, { 11, "千葉県総合スポーツセンター野球場" }, { 12, "明治神宮野球場" }, { 13, "保土ケ谷球場" },
        { 14, null }, { 15, null }, { 16, null }, { 17, null }, { 18, null }, { 19, "長野オリンピックスタジアム" },
        { 20, null }, { 21, null }, { 22, "小牧市民球場" }, { 23, "津市営球場" }, { 24, null }, { 25, null }, { 26, null },
        { 27, "明石公園第一野球場" }, { 28, null }, { 29, null }, { 30, "米子市民球場" }, { 31, null }, { 32, null },
        { 33, "みよし運動公園野球場" }, { 34, "下関球場" }, { 35, null }, { 36, null }, { 37, null }, { 38, null },
        { 39, "久留米市野球場" }, { 40, null }, { 41, null }, { 42, null }, { 43, null }, { 44, null }, { 45, null },
        { 46, "名護市営球場" },
    };

    [Theory]
    [MemberData(nameof(AutumnVenues))]
    public void AutumnFinalRounds_UseThePrefecturesAutumnVenue(int prefectureId, string? expectedName)
    {
        foreach (var rr in new[] { 1, 2 })
        {
            var st = RegionalStadiumTier.ForStadium(rr, new Xoshiro256Random(1), prefectureId, summer: false);
            if (expectedName is null) Assert.Same(Stadium.PrefLarge, st);
            else Assert.Equal(expectedName, st.Name);
        }
    }

    /// <summary>秋の会場を足しても夏は変わらない（夏に実在の会場を使うのは東京・神奈川・北海道・宮城・広島だけ）。</summary>
    [Fact]
    public void SummerFinalRounds_AreUnchangedByTheAutumnVenues()
    {
        for (var pref = 0; pref < 47; pref++)
        {
            var st = RegionalStadiumTier.FinalStadiumFor(pref, summer: true);
            var expected = pref switch
            {
                0 => Stadium.HokkaidoBallpark, 3 => Stadium.Miyagi, 12 => Stadium.Jingu,
                13 => Stadium.Yokohama, 33 => Stadium.Hiroshima, _ => Stadium.PrefLarge,
            };
            Assert.Same(expected, st);
        }
    }

    /// <summary>札幌ドームだけが屋内。秋の会場は 3D モデルを持たない（standard を判定寸法へ伸縮して描く）。</summary>
    [Fact]
    public void AutumnVenues_OnlySapporoDomeIsIndoor_AndNoneHasAModel()
    {
        foreach (var (pref, st) in Stadium.AutumnFinals)
        {
            Assert.Equal(pref == 0, st.Indoor);
            Assert.Null(st.Model);
            Assert.Equal(StadiumTier.Prefectural, st.Tier);
            Assert.Equal(st.Indoor, st.ToFieldGeometry().Indoor);
        }
        Assert.Equal(17, Stadium.AutumnFinals.Count);
    }

    [Fact]
    public void KanagawaAutumnHodogaya_UsesItsDimensions()
    {
        var geom = RegionalStadiumTier.For(1, new Xoshiro256Random(1), prefectureId: 13, summer: false);
        Assert.Equal(95.0, geom.LeftFenceM, 6);
        Assert.Equal(95.0, geom.RightFenceM, 6);
        Assert.Equal(120.0, geom.CenterFenceM, 6);
        Assert.Equal(3.0, geom.FenceHeightM, 6);   // 資料なし＝監督決定の 3.0m（県営級と同じ）
        Assert.False(geom.Indoor);
    }

    /// <summary>
    /// フェンス高の資料が無い秋の会場（札幌ドーム 5.75・長野OS 4.0 以外の15か所）は 3.0m＝汎用の県営級と同じ
    /// （2026-11-15 監督決定）。夏の会場の「省略時 2.5m」を秋へ持ち込まない。
    /// </summary>
    [Fact]
    public void AutumnVenuesWithoutDocumentedFence_UseThePrefecturalDefault()
    {
        var documented = new System.Collections.Generic.Dictionary<int, double> { [0] = 5.75, [19] = 4.0 };
        foreach (var (pref, st) in Stadium.AutumnFinals)
            Assert.Equal(documented.TryGetValue(pref, out var h) ? h : Stadium.PrefLarge.FenceHeightM, st.FenceHeightM, 6);
        Assert.Equal(15, Stadium.AutumnFinals.Count - documented.Count);
    }

    [Fact]
    public void AutumnEarlyRounds_DrawTheSameAsBefore_AndFinalsDoNotConsumeRandomness()
    {
        foreach (var pref in Stadium.AutumnFinals.Keys)
        {
            for (ulong seed = 1; seed <= 5; seed++)
                Assert.Same(RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), null),
                            RegionalStadiumTier.ForStadium(6, new Xoshiro256Random(seed), pref, summer: false));
            var before = new Xoshiro256Random(99).NextInt(0, int.MaxValue);
            var rng = new Xoshiro256Random(99);
            RegionalStadiumTier.ForStadium(1, rng, pref, summer: false);
            Assert.Equal(before, rng.NextInt(0, int.MaxValue));
        }
    }
}
