using System.IO;
using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// 球場カタログの純パーサ（Unity 用・issue #905）。Config 側の YamlDotNet ローダと解釈が1件残らず一致すること。
/// パーサが2本あるので、ずれたら「Unity だけ別の寸法で試合をする」＝ここで赤にして止める。
/// </summary>
public sealed class StadiumCatalogParserTests
{
    [Fact]
    public void RepositoryYaml_ParsesIdenticallyToTheConfigLoader()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("stadiums.yaml");
        var text = File.ReadAllText(path);
        var expected = StadiumCatalogLoader.Parse(text);
        var actual = StadiumCatalogParser.Parse(text);

        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual!.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], actual[i]);   // record の値等価＝id・名前・tier・寸法・風の全部
    }

    [Fact]
    public void AppliesTheSameDefaults_AndIgnoresComments()
    {
        const string yaml = """
            # 見出し前のコメント
            stadiums:
              - id: a          # 行末コメント
                name: "球場#1"
                left: 91
                tier: municipal
              # 項目間のコメント
            other_section:
              - id: ignored
                name: 拾わない
            prefecture_finals:
              - id: b
                name: 非対称
                left: 101
                right: 100
                center: 122
                fence_height: 3.0
            """;

        var list = StadiumCatalogParser.Parse(yaml)!;
        Assert.Equal(new[] { "a", "b" }, list.Select(s => s.Id));
        Assert.Equal(StadiumCatalogLoader.Parse(yaml), list);

        var a = list[0];
        Assert.Equal("球場#1", a.Name);          // 引用符の中の # はコメントではない
        Assert.Equal(StadiumTier.Municipal, a.Tier);
        Assert.Equal(91, a.RightM, 6);           // right 省略＝left
        Assert.Equal(118, a.CenterM, 6);         // center 省略＝118
        Assert.Equal(2.5, a.FenceHeightM, 6);    // fence_height 省略＝2.5
        Assert.Equal(StadiumTier.Prefectural, list[1].Tier);   // tier 省略＝県営級
    }

    [Fact]
    public void BrokenEntriesAreSkipped_AndEmptyIsNull()
    {
        const string yaml = """
            stadiums:
              - name: idが無い
                left: 90
              - id: bad_tier
                tier: galaxy
              - id: ok
            """;
        Assert.Equal(new[] { "ok" }, StadiumCatalogParser.Parse(yaml)!.Select(s => s.Id));
        Assert.Null(StadiumCatalogParser.Parse("stadiums:\n"));
    }
}
