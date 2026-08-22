using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// data/stadiums.yaml → Stadium 読込の検証（設計書13-stadiums §1、不変条件#3/#4）。
/// </summary>
public sealed class StadiumLoaderTests
{
    [Fact]
    public void ParsesBothSections_AndAppliesDefaults()
    {
        const string yaml = """
            stadiums:
              - id: municipal_small_01
                name: 市営球場（狭い）
                left: 91
                center: 115
                fence_height: 2.0
                tier: municipal
            prefecture_finals:
              - id: hiroshima_final
                name: 広島市民球場
                left: 101
                right: 100
                center: 122
                fence_height: 3.0
                tier: prefectural
              - id: some_final
                name: どこかの球場
                left: 98
                center: 122
                tier: prefectural
            """;

        var list = StadiumCatalogLoader.Parse(yaml);
        Assert.Equal(3, list.Count);

        var muni = list.Single(s => s.Id == "municipal_small_01");
        Assert.Equal(StadiumTier.Municipal, muni.Tier);
        Assert.Equal(91, muni.LeftM, 6);
        Assert.Equal(91, muni.RightM, 6);   // right 省略 → left フォールバック
        Assert.Equal(2.0, muni.FenceHeightM, 6);

        var hiro = list.Single(s => s.Id == "hiroshima_final");
        Assert.Equal(101, hiro.LeftM, 6);
        Assert.Equal(100, hiro.RightM, 6);  // 非対称

        var some = list.Single(s => s.Id == "some_final");
        Assert.Equal(2.5, some.FenceHeightM, 6);  // fence_height 省略 → 2.5m
    }

    [Fact]
    public void ToFieldGeometry_CarriesDimensions()
    {
        var stadium = new Stadium { Id = "x", LeftM = 91, RightM = 100, CenterM = 115, FenceHeightM = 2.0 };
        var geom = stadium.ToFieldGeometry();
        Assert.Equal(91, geom.LeftFenceM, 6);
        Assert.Equal(100, geom.RightFenceM, 6);
        Assert.Equal(115, geom.CenterFenceM, 6);
        Assert.Equal(2.0, geom.FenceHeightM, 6);
    }

    [Fact]
    public void LoadsRealDataFile_WithUniqueIds()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("stadiums.yaml");
        var catalog = StadiumCatalogLoader.LoadCatalog(path);

        // 架空タイプ・聖地・47県決勝が揃う
        Assert.True(catalog.ContainsKey("municipal_small_01"));
        Assert.True(catalog.ContainsKey("standard_01"));
        Assert.True(catalog.ContainsKey("pref_large_01"));
        Assert.True(catalog.ContainsKey("koshien"));
        Assert.True(catalog.ContainsKey("kanagawa_final"));
        Assert.True(catalog.Count >= 50);

        // 甲子園は両翼狭め＋中間深めの変則
        var koshien = catalog["koshien"];
        Assert.Equal(StadiumTier.National, koshien.Tier);
        Assert.Equal(95, koshien.LeftM, 6);
        Assert.Equal(118, koshien.CenterM, 6);
    }

    /// <summary>
    /// 甲子園の C#既定（<see cref="Stadium.Koshien"/>）と data/stadiums.yaml が一致すること（不変条件#4）。
    /// 実ゲームは YAML を読まず C#既定で走るので、片方だけ動かすと<b>実ゲームだけ別の球場で試合をする</b>。
    /// </summary>
    [Fact]
    public void KoshienDefault_MatchesTheRepositoryYaml()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("stadiums.yaml");
        var yaml = StadiumCatalogLoader.LoadCatalog(path)["koshien"];
        var code = Stadium.Koshien;

        Assert.Equal(yaml.Id, code.Id);
        Assert.Equal(yaml.Name, code.Name);
        Assert.Equal(yaml.Tier, code.Tier);
        Assert.Equal(yaml.LeftM, code.LeftM, 6);
        Assert.Equal(yaml.RightM, code.RightM, 6);
        Assert.Equal(yaml.CenterM, code.CenterM, 6);
        Assert.Equal(yaml.FenceHeightM, code.FenceHeightM, 6);
    }

    /// <summary>
    /// 甲子園と既定の球場（<see cref="FieldGeometry"/> の初期値）の関係を固定する。
    ///
    /// <para><b>距離は既に同じ</b>（95/95/118）＝既定の球場がもともと甲子園の寸法で書かれている。
    /// 違うのは<b>フェンス高だけ</b>（既定4.0m に対し甲子園2.6m）で、実ゲームで甲子園の寸法を使うと
    /// 効くのはこの1.4mである。「地方は広い → 甲子園は狭い」という差はまだ存在しない
    /// （地方大会の球場割当＝設計書13 の <c>StadiumPlan</c> が実ゲームへ未配線なため）。
    /// この事実を知らずに距離だけ見ると「注入しても何も変わらないはず」と誤読する。</para>
    /// </summary>
    [Fact]
    public void Koshien_DiffersFromTheDefaultField_OnlyInFenceHeight()
    {
        var koshien = Stadium.Koshien.ToFieldGeometry();
        var standard = new FieldGeometry();

        Assert.Equal(standard.LeftFenceM, koshien.LeftFenceM, 6);
        Assert.Equal(standard.RightFenceM, koshien.RightFenceM, 6);
        Assert.Equal(standard.CenterFenceM, koshien.CenterFenceM, 6);
        Assert.True(koshien.FenceHeightM < standard.FenceHeightM,
            $"甲子園のフェンス({koshien.FenceHeightM}m)が既定({standard.FenceHeightM}m)より低くない");
    }
}
