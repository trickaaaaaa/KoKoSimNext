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
            Assert.Equal(expected.Playoff is null, actual.Playoff is null);
            if (expected.Playoff is { } ep)
            {
                Assert.Equal(ep.FromYear, actual.Playoff!.FromYear);
                Assert.Equal(ep.CandidateRank, actual.Playoff.CandidateRank);
                Assert.Equal(ep.BerthsPerPref, actual.Playoff.BerthsPerPref);
            }
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
        Assert.Null(kinki.Playoff);
    }

    [Fact]
    public void Parse_PlayoffBlock_FromRawYaml()
    {
        const string yaml = @"
regions:
  - region: kinki
    fixed: { osaka: 3, hyogo: 3 }
    biennial_rotation:
      odd_year:  { shiga: 3, nara: 3 }
      even_year: { shiga: 2, nara: 2 }
    playoff:                 # 2026年から
      from_year: 2026
      candidate_rank: 3
      berths_per_pref: { shiga: 2, nara: 2 }
    host_prefecture_bonus: false
    champion_to_jingu: true
  - region: tohoku
    berths_per_pref: { aomori: 3 }
";
        var set = RegionalFormatParser.Parse(yaml);
        var kinki = set.ForRegion("kinki")!;
        Assert.Equal(2026, kinki.Playoff!.FromYear);
        Assert.Equal(3, kinki.Playoff.CandidateRank);
        Assert.Equal(2, kinki.Playoff.BerthsPerPref["nara"]);
        Assert.Equal(3, kinki.BerthsFor("nara", 2025));  // 決定戦の前は隔年制
        Assert.Equal(2, kinki.BerthsFor("nara", 2027));  // 決定戦の年は各2校
        Assert.Equal(2, kinki.PlayoffCandidateIndex("nara", 2027));
        Assert.Null(set.ForRegion("tohoku")!.Playoff);
        Assert.Equal(3, set.ForRegion("tohoku")!.BerthsFor("aomori", 2027));
    }
}

/// <summary>開催県の表が尽きた年は、表の最終年を起点に地区内の県をJIS順で持ち回る（issue #1221）。</summary>
public sealed class RegionalFormatHostRotationTests
{
    private static RegionalFormat Region(string name) =>
        KokoSim.Config.RegionalFormatLoader.LoadFromFile(
            Balance.BalanceRegressionTests.FindDataFile("pref-formats/regional-tournaments.yaml")).ForRegion(name)!;

    [Fact]
    public void HostFor_UsesTableYearsAsIs_AndRotatesBeyondTheTable()
    {
        var kanto = Region("kanto");
        Assert.Equal("yamanashi", kanto.HostFor(2025));
        Assert.Equal("gunma", kanto.HostFor(2026));
        Assert.Equal("saitama", kanto.HostFor(2027));
        Assert.Equal("chiba", kanto.HostFor(2028));
        Assert.Equal("gunma", kanto.HostFor(2033)); // 7県で一巡して戻る
    }

    [Fact]
    public void BerthsFor_HostBonusKeepsGoingAfterTheTableEnds()
    {
        foreach (var region in new[] { "hokushinetsu", "chugoku" })
        {
            var fmt = Region(region);
            Assert.True(fmt.HostPrefectureBonus);
            for (var year = 2026; year <= 2040; year++)
            {
                var host = fmt.HostFor(year)!;
                var extra = fmt.BerthsPerPref.Keys.Count(p => fmt.BerthsFor(p, year) == fmt.BerthsPerPref[p] + 1);
                Assert.Equal(1, extra);
                Assert.Equal(fmt.BerthsPerPref[host] + 1, fmt.BerthsFor(host, year));
            }
        }
    }

    [Fact]
    public void Shikoku_HostIsEhimeIn2025AndKagawaIn2026()
    {
        var shikoku = Region("shikoku");
        Assert.Equal("ehime", shikoku.HostFor(2025));   // 2025年の四国大会は愛媛開催（issue #1290）
        Assert.Equal("kagawa", shikoku.HostFor(2026));
    }

    [Fact]
    public void Hyogo_DistrictNamesHaveNoNonexistentEastWestHarima()
    {
        var hyogo = KokoSim.Config.PrefFormatLoader.LoadFromFile(
            Balance.BalanceRegressionTests.FindDataFile("pref-formats/hyogo.yaml"));
        Assert.Equal(5, hyogo.Districts.Count);
        Assert.DoesNotContain("東西播", hyogo.Districts);
        Assert.Contains("西播", hyogo.Districts);
    }

    [Fact]
    public void HostFor_VisitsEveryPrefectureOverOneCycle()
    {
        var fmt = Region("hokushinetsu");
        var hosts = Enumerable.Range(2026, fmt.BerthsPerPref.Count).Select(y => fmt.HostFor(y)).Distinct().Count();
        Assert.Equal(fmt.BerthsPerPref.Count, hosts);
    }
}
