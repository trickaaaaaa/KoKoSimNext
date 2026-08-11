using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 47都道府県の対応表（<see cref="PrefectureNames"/>）。ローマ字名と日本語名は同じ JIS順で
/// 並んでいなければ、校名語彙（ローマ字キー）と表示（日本語）が別の県を指す。
/// ズレても例外は出ず「地名は青森なのに県名は岩手」になるだけなので、ここで固定する。
/// </summary>
public sealed class PrefectureNamesTests
{
    [Fact]
    public void JapaneseAndRomaji_HaveSameLengthAndOrder()
    {
        Assert.Equal(47, PrefectureNames.JisOrder.Count);
        Assert.Equal(47, PrefectureNames.Japanese.Count);

        // 抜き取りで並びの対応を確かめる（先頭・末尾・都府道・自校の本拠県）。
        Assert.Equal("北海道", PrefectureNames.JapaneseOf(PrefectureNames.IdOf("hokkaido")));
        Assert.Equal("東京都", PrefectureNames.JapaneseOf(PrefectureNames.IdOf("tokyo")));
        Assert.Equal("神奈川県", PrefectureNames.JapaneseOf(PrefectureNames.IdOf("kanagawa")));
        Assert.Equal("京都府", PrefectureNames.JapaneseOf(PrefectureNames.IdOf("kyoto")));
        Assert.Equal("大阪府", PrefectureNames.JapaneseOf(PrefectureNames.IdOf("osaka")));
        Assert.Equal("沖縄県", PrefectureNames.JapaneseOf(PrefectureNames.IdOf("okinawa")));
    }

    [Fact]
    public void JapaneseNames_AreDistinct()
        => Assert.Equal(47, PrefectureNames.Japanese.Distinct().Count());

    /// <summary>
    /// data/prefectures.yaml（大会フォーマットの正典）と engine の定数表が同じ並びであること。
    /// ズレると「秋季大会の枠は青森なのに画面の県名は岩手」になり、例外も出ないまま食い違う。
    /// </summary>
    [Fact]
    public void Yaml_AndConstantTable_AreAligned()
    {
        var table = KokoSim.Config.PrefectureTableLoader.LoadFromFile(
            Balance.BalanceRegressionTests.FindDataFile("prefectures.yaml"));

        Assert.Equal(47, table.Prefectures.Count);
        foreach (var info in table.Prefectures)
        {
            Assert.Equal(PrefectureNames.JisOrder[info.Id], info.Name);
            Assert.NotEqual("", PrefectureNames.JapaneseOf(info.Id));
        }
    }

    [Fact]
    public void JapaneseOf_OutOfRange_ReturnsEmpty()
    {
        Assert.Equal("", PrefectureNames.JapaneseOf(-1));
        Assert.Equal("", PrefectureNames.JapaneseOf(47));
    }

    /// <summary>生成される全国の県が実名を持つこと（旧「第N県」のプレースホルダを残さない）。</summary>
    [Fact]
    public void GeneratedNation_UsesRealPrefectureNames()
    {
        var nation = NationGenerator.Generate(
            new SchoolNameVocab(), new NationCoefficients(), new Xoshiro256Random(2026));

        Assert.Equal(47, nation.Prefectures.Count);
        foreach (var p in nation.Prefectures)
        {
            Assert.Equal(PrefectureNames.JapaneseOf(p.Id), p.Name);
            Assert.DoesNotContain("第", p.Name);
        }
    }

    /// <summary>
    /// 夏の地区名も実名になること。北海道・東京だけ分割名（北北海道／東東京…）を持ち、
    /// それ以外は県名をそのまま使う＝片方だけプレースホルダ、が起きない。
    /// </summary>
    [Fact]
    public void SummerRegions_UseRealNames()
    {
        var prefectures = new List<Prefecture>();
        for (var i = 0; i < 47; i++) prefectures.Add(new Prefecture(i, PrefectureNames.JapaneseOf(i), 80));

        var regions = SummerRegions.Build(prefectures);
        var names = regions.Select(r => r.Name).ToList();

        Assert.Contains("北北海道", names);
        Assert.Contains("南北海道", names);
        Assert.Contains("東東京", names);
        Assert.Contains("西東京", names);
        Assert.Contains("神奈川県", names);
        Assert.DoesNotContain(names, n => n.Contains("第"));
    }
}
