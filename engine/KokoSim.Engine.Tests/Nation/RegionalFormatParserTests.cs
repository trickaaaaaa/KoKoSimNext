using System.Linq;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// data/pref-formats/regional-tournaments.yaml を読む純パーサ（<see cref="RegionalFormatParser"/>）。
/// Unity（YamlDotNet非依存）側で使う。Balance/CLIが使う <see cref="KokoSim.Config.RegionalFormatLoader"/>
/// （YamlDotNet）と同じ値を返すことを実ファイルで確認する（設計書05 §1.5b）。
/// </summary>
public sealed class RegionalFormatParserTests
{
    [Fact]
    public void Parse_AgreesWithConfigLoader_ForTheRealFile()
    {
        var path = Balance.BalanceRegressionTests.FindDataFile("pref-formats/regional-tournaments.yaml");
        var text = System.IO.File.ReadAllText(path);

        var viaConfig = KokoSim.Config.RegionalFormatLoader.LoadFromFile(path);
        var viaParser = RegionalFormatParser.Parse(text);

        Assert.Equal(viaConfig.Regions.Count, viaParser.Regions.Count);
        foreach (var expected in viaConfig.Regions)
        {
            var actual = viaParser.ForRegion(expected.Region);
            Assert.NotNull(actual);
            Assert.Equal(expected.SinglePrefecture, actual!.SinglePrefecture);
            Assert.Equal(expected.HostPrefectureBonus, actual.HostPrefectureBonus);
            Assert.Equal(expected.ChampionToJingu, actual.ChampionToJingu);
            Assert.Equal(expected.FixedBerths, actual.FixedBerths);
            Assert.Equal(expected.BerthsPerPref, actual.BerthsPerPref);
            Assert.Equal(expected.OddYearBerths, actual.OddYearBerths);
            Assert.Equal(expected.EvenYearBerths, actual.EvenYearBerths);
            Assert.Equal(expected.HostByYear, actual.HostByYear);
        }
    }

    [Fact]
    public void Parse_TenRegions_AllPresent()
    {
        var path = Balance.BalanceRegressionTests.FindDataFile("pref-formats/regional-tournaments.yaml");
        var set = RegionalFormatParser.Parse(System.IO.File.ReadAllText(path));

        var expected = new[]
        {
            "hokkaido", "tohoku", "kanto", "tokyo", "hokushinetsu",
            "tokai", "kinki", "chugoku", "shikoku", "kyushu",
        };
        Assert.Equal(10, set.Regions.Count);
        foreach (var r in expected) Assert.NotNull(set.ForRegion(r));
    }

    [Fact]
    public void Parse_InlineMapsAndBiennialRotation_FromRawYaml()
    {
        const string yaml = @"
regions:
  - region: kanto
    berths_per_pref: { ibaraki: 2, tochigi: 2 }
    host_prefecture_bonus: true
    host_by_year: { 2025: yamanashi, 2026: gunma }
    champion_to_jingu: true
  - region: tokyo
    single_prefecture: true
    champion_to_jingu: true
    host_by_year: { 2025: tokyo }
  - region: kinki
    fixed: { osaka: 3, hyogo: 3 }
    biennial_rotation:
      odd_year:  { shiga: 3, nara: 3 }
      even_year: { shiga: 2, nara: 2 }
    host_prefecture_bonus: false
    host_by_year: { 2025: nara }
    champion_to_jingu: true
    notes:
      - some note that must be ignored
";
        var set = RegionalFormatParser.Parse(yaml);
        Assert.Equal(3, set.Regions.Count);

        var kanto = set.ForRegion("kanto")!;
        Assert.Equal(2, kanto.BerthsPerPref["ibaraki"]);
        Assert.True(kanto.HostPrefectureBonus);
        Assert.Equal("yamanashi", kanto.HostByYear[2025]);
        Assert.Equal("gunma", kanto.HostByYear[2026]);
        Assert.Equal(2, kanto.BerthsFor("ibaraki", 2027));   // 非開催年・非在籍県は素の枠のまま
        Assert.Equal(2, kanto.BerthsFor("ibaraki", 2025));   // 素の枠はあるが開催県ではない年
        Assert.Equal(1, kanto.BerthsFor("yamanashi", 2025)); // 素の枠は無いが開催県+1だけ乗る（BerthsForの仕様どおり）

        var tokyo = set.ForRegion("tokyo")!;
        Assert.True(tokyo.SinglePrefecture);
        Assert.Empty(tokyo.BerthsPerPref);

        var kinki = set.ForRegion("kinki")!;
        Assert.Equal(3, kinki.FixedBerths["osaka"]);
        Assert.NotNull(kinki.OddYearBerths);
        Assert.Equal(3, kinki.OddYearBerths!["shiga"]);
        Assert.Equal(2, kinki.EvenYearBerths!["shiga"]);
        Assert.Equal(3, kinki.BerthsFor("shiga", 2025)); // 奇数年
        Assert.Equal(2, kinki.BerthsFor("shiga", 2026)); // 偶数年
    }
}
