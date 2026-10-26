using System;
using System.IO;
using System.Linq;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 県内地区数の抽出（issue #585）。Unity は Config 層（YamlDotNet）を持たないので、
/// 新設校スタートの地区選択はこの純パーサで data/pref-formats/ の同じファイルから地区数だけを読む。
/// 正典は YAML のまま＝実ファイルを読んで固定する（テーブルをコードへ写さない）。
/// </summary>
public sealed class PrefFormatDistrictParserTests
{
    [Theory]
    [InlineData("kanagawa.yaml", "kanagawa", 4)]   // 地区予選 group_split / groups:4（districts の名前は7個だが運用は4）
    [InlineData("hyogo.yaml", "hyogo", 5)]         // 地区予選 group_split / groups:5
    [InlineData("nara.yaml", "nara", 0)]           // district_assignment: none ＝ 県内地区なし
    [InlineData("aichi.yaml", "aichi", 5)]         // 地区予選 group_split / groups:5
    [InlineData("hokkaido.yaml", "hokkaido", 10)]  // 支部予選 group_split / groups:10（夏の南北分割とは別物）
    public void Parse_RealFiles(string file, string pref, int districts)
    {
        var text = File.ReadAllText(FindPrefFormat(file));
        Assert.Equal((pref, districts), PrefFormatDistrictParser.Parse(text));
    }

    /// <summary>県大会へ進む校数と夏代表の推薦も同じファイルから読む（issue #688）。
    /// 地区なしの県（奈良）は規則を持たない＝1校・推薦なし。</summary>
    [Theory]
    [InlineData("kanagawa.yaml", 4, 8, true)]
    [InlineData("hyogo.yaml", 5, 8, true)]
    [InlineData("aichi.yaml", 5, 6, false)]
    [InlineData("hokkaido.yaml", 10, 3, false)]
    [InlineData("nara.yaml", 0, 1, false)]
    public void ParseRule_RealFiles(string file, int districts, int advance, bool seed)
    {
        var rule = PrefFormatDistrictParser.ParseRule(File.ReadAllText(FindPrefFormat(file)));
        Assert.Equal(districts, rule.Districts);
        Assert.Equal(advance, rule.AdvancePerDistrict);
        Assert.Equal(seed, rule.SeedExemption);
    }

    /// <summary>YAML の進出校数は PrefFormat（完全ロード）と同じ値でなければならない（2経路の乖離防止）。</summary>
    [Theory]
    [InlineData("kanagawa.yaml")]
    [InlineData("hyogo.yaml")]
    [InlineData("aichi.yaml")]
    [InlineData("hokkaido.yaml")]
    public void ParseRule_AdvanceMatchesFullLoad(string file)
    {
        var path = FindPrefFormat(file);
        var full = KokoSim.Config.PrefFormatLoader.LoadFromFile(path);
        var stage = full.Stages.First(s => s.Type == StageType.GroupSplit);
        var rule = PrefFormatDistrictParser.ParseRule(File.ReadAllText(path));
        Assert.Equal(stage.AdvancePerGroup ?? 1, rule.AdvancePerDistrict);
        Assert.Equal(full.SeedExemption, rule.SeedExemption);
    }

    /// <summary>入れ子（child: の type）をステージ自身の type と取り違えない。</summary>
    [Fact]
    public void Parse_IgnoresNestedChildKeys()
    {
        const string yaml = @"
pref: kanagawa
district_assignment: geographic
stages:
  - name: 地区予選
    type: group_split
    groups: 4
    grouping: geographic
    child:
      type: round_robin
      teams_per_group: 4
      groups: 99
  - name: 県大会
    type: knockout
";
        Assert.Equal(("kanagawa", 4), PrefFormatDistrictParser.Parse(yaml));
    }

    /// <summary>geographic でない割当（抽選など）は地区を持たない扱い＝0。</summary>
    [Fact]
    public void Parse_NonGeographicAssignment_HasNoDistricts()
    {
        const string yaml = @"
pref: nara
district_assignment: none
stages:
  - name: 県大会
    type: group_split
    groups: 3
    grouping: random
";
        Assert.Equal(("nara", 0), PrefFormatDistrictParser.Parse(yaml));
    }

    /// <summary>
    /// 県Id への解決。<see cref="DistrictPlan.Build"/>（Config 層の完全ロード版）と同じ意味の表を返す。
    /// </summary>
    [Fact]
    public void BuildPlan_MapsToPrefectureIds()
    {
        var texts = Directory.GetFiles(PrefFormatDir(), "*.yaml")
            .Where(p => Path.GetFileName(p) != "regional-tournaments.yaml")
            .Select(File.ReadAllText);
        var plan = PrefFormatDistrictParser.BuildPlan(texts);

        Assert.Equal(4, plan[PrefectureNames.IdOf("kanagawa")]);
        Assert.Equal(5, plan[PrefectureNames.IdOf("hyogo")]);
        Assert.Equal(5, plan[PrefectureNames.IdOf("aichi")]);
        Assert.Equal(10, plan[PrefectureNames.IdOf("hokkaido")]);
        Assert.False(plan.ContainsKey(PrefectureNames.IdOf("nara")));   // 地区なしの県は載せない
    }

    private static string PrefFormatDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "pref-formats");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("data/pref-formats がリポジトリ内に見つかりません。");
    }

    private static string FindPrefFormat(string fileName) => Path.Combine(PrefFormatDir(), fileName);
}
