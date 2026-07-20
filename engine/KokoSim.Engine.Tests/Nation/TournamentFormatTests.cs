using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会フォーマット（設計書05 §1.5, CHANGELOG 26-31）。型3種（knockout/round_robin/group_split）、
/// 県フォーマット実行、地区大会の枠配分、センバツ選考、現代ルールトグル、YAMLローダを検証する。
/// </summary>
public sealed class TournamentFormatTests
{
    private static readonly NationCoefficients Coeff = new();

    private static School Sch(int id, double strength, int? district = null) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength, DistrictId = district,
    };

    private static List<School> Schools(int n, int? districtMod = null)
        => Enumerable.Range(0, n)
            .Select(i => Sch(i, 20 + (i * 61 % 70), districtMod is { } m ? i % m : (int?)null))
            .ToList();

    private static string DataFile(string name) => Balance.BalanceRegressionTests.FindDataFile(name);

    // ===== knockout（順位付き・3位決定戦・決定論） =====

    [Fact]
    public void Knockout_PlacementCoversAllEntrants_ChampionFirst()
    {
        var r = Knockout.Run(Schools(12), Coeff, new Xoshiro256Random(1));
        Assert.Equal(12, r.Placement.Count);
        Assert.Equal(12, r.Placement.Distinct().Count());
        Assert.Equal(r.Champion, r.Placement[0]);
    }

    [Fact]
    public void Knockout_StrongestUsuallyWins()
    {
        var schools = Schools(16);
        var top = schools.OrderByDescending(s => s.Strength).First();
        var titles = 0;
        for (ulong s = 0; s < 60; s++)
            if (Knockout.Run(schools, Coeff, new Xoshiro256Random(s)).Champion == top) titles++;
        Assert.True(titles > 20, $"最強校の優勝が少なすぎる: {titles}/60");
    }

    [Fact]
    public void Knockout_IsDeterministic()
    {
        var a = Knockout.Run(Schools(12), Coeff, new Xoshiro256Random(9));
        var b = Knockout.Run(Schools(12), Coeff, new Xoshiro256Random(9));
        Assert.Equal(a.Placement.Select(s => s.Id), b.Placement.Select(s => s.Id));
    }

    [Fact]
    public void Knockout_ThirdPlaceMatch_OrdersSemifinalLosers()
    {
        // 3位決定戦ありでは、上位4校の並びが seed 次第で入れ替わり得る（プレーオフが機能している）。
        var schools = Schools(4);
        var thirds = new HashSet<int>();
        for (ulong s = 0; s < 30; s++)
            thirds.Add(Knockout.Run(schools, Coeff, new Xoshiro256Random(s), thirdPlaceMatch: true).Placement[2].Id);
        Assert.True(thirds.Count >= 2, "3位が常に同じ＝プレーオフが効いていない");
    }

    // ===== round_robin（総当たり・勝敗合計・決定論） =====

    [Fact]
    public void RoundRobin_EveryonePlaysEveryone()
    {
        var teams = Schools(5);
        var standings = RoundRobin.Run(teams, Coeff, new Xoshiro256Random(3));
        Assert.Equal(5, standings.Count);
        foreach (var st in standings)
            Assert.Equal(4, st.Wins + st.Losses); // n-1 試合
        // 勝点は降順に並ぶ。
        for (var i = 0; i + 1 < standings.Count; i++)
            Assert.True(standings[i].Wins >= standings[i + 1].Wins);
    }

    [Fact]
    public void RoundRobin_IsDeterministic()
    {
        var teams = Schools(6);
        var a = RoundRobin.Run(teams, Coeff, new Xoshiro256Random(4));
        var b = RoundRobin.Run(teams, Coeff, new Xoshiro256Random(4));
        Assert.Equal(a.Select(s => s.School.Id), b.Select(s => s.School.Id));
    }

    // ===== group_split（地区割・進出数・敗者復活） =====

    [Fact]
    public void GroupSplit_Geographic_AdvancesOnePerGroup()
    {
        var stage = new StageFormat
        {
            Type = StageType.GroupSplit, Groups = 2, Grouping = GroupingMode.Geographic,
            Child = new ChildStage(StageType.Knockout, null), AdvancePerGroup = 1,
        };
        var teams = Schools(8, districtMod: 2); // DistrictId 0/1 が4校ずつ
        var advancers = GroupSplit.Run(teams, stage, Coeff, new Xoshiro256Random(2), s => s.DistrictId);
        Assert.Equal(2, advancers.Count);
        Assert.Equal(2, advancers.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void GroupSplit_LoserBracket_AddsRevivedTeams()
    {
        var stage = new StageFormat
        {
            Type = StageType.GroupSplit, Groups = 2, Grouping = GroupingMode.Geographic,
            Child = new ChildStage(StageType.Knockout, null), AdvancePerGroup = 1,
            LoserBracket = new LoserBracketRule(true, 1),
        };
        var teams = Schools(8, districtMod: 2);
        var advancers = GroupSplit.Run(teams, stage, Coeff, new Xoshiro256Random(5), s => s.DistrictId);
        Assert.Equal(4, advancers.Count); // 各ブロック 正規1＋敗者復活1
    }

    // ===== PrefTournamentEngine（3県サンプルをYAMLから実行） =====

    [Fact]
    public void PrefEngine_Nara_KnockoutWithThirdPlace_YieldsThreeQualifiers()
    {
        var fmt = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/nara.yaml"));
        var r = PrefTournamentEngine.Run(fmt, Schools(24), Coeff, new Xoshiro256Random(1));
        Assert.Equal(3, r.Qualifiers.Count);          // regional_berths: 3
        Assert.Equal(r.Champion, r.Qualifiers[0]);
        Assert.Equal(3, r.Qualifiers.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void PrefEngine_Kanagawa_SeedExemption_RecommendedEntersFinal()
    {
        var fmt = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/kanagawa.yaml"));
        Assert.True(fmt.SeedExemption);
        var teams = Schools(24, districtMod: 4);
        // 推薦校（夏代表）は圧倒的に強く、県大会から登場 → 上位進出しやすい。
        var recommended = Sch(999, 100.0, district: 0);
        var qualified = 0;
        for (ulong s = 0; s < 20; s++)
        {
            var r = PrefTournamentEngine.Run(fmt, teams, Coeff, new Xoshiro256Random(s), recommended);
            Assert.Equal(2, r.Qualifiers.Count);       // regional_berths: 2
            if (r.Qualifiers.Any(q => q.Id == 999)) qualified++;
        }
        Assert.True(qualified > 10, $"予選免除の最強推薦校が地区大会へ届く回数が少なすぎる: {qualified}/20");
    }

    [Fact]
    public void PrefEngine_Hyogo_GroupSplitWithLoserBracket_YieldsThreeQualifiers()
    {
        var fmt = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/hyogo.yaml"));
        var teams = Schools(60, districtMod: 5); // 5地区
        var recommended = Sch(999, 95.0, district: 0);
        var r = PrefTournamentEngine.Run(fmt, teams, Coeff, new Xoshiro256Random(7), recommended);
        Assert.Equal(3, r.Qualifiers.Count);           // regional_berths: 3
    }

    // ===== 地区大会の枠配分（regional-tournaments.yaml） =====

    [Fact]
    public void Regional_BerthsFor_FixedBiennialAndHostBonus()
    {
        var set = KokoSim.Config.RegionalFormatLoader.LoadFromFile(DataFile("pref-formats/regional-tournaments.yaml"));
        var kinki = set.ForRegion("kinki");
        Assert.NotNull(kinki);
        Assert.Equal(3, kinki!.BerthsFor("osaka", 2025));    // 固定3
        Assert.Equal(3, kinki.BerthsFor("nara", 2025));      // 奇数年3
        Assert.Equal(2, kinki.BerthsFor("nara", 2026));      // 偶数年2

        var kanto = set.ForRegion("kanto")!;
        Assert.Equal(2, kanto.BerthsFor("kanagawa", 2025));  // 通常2
        Assert.Equal(3, kanto.BerthsFor("yamanashi", 2025)); // 開催県+1
    }

    // ===== 神宮動線＋センバツ選考 =====

    [Fact]
    public void Senbatsu_GeneralBerthsPlusJinguBonus()
    {
        var kinki = Schools(6).Select(s => s).ToList();
        var kanto = Schools(6).Select(s => Sch(s.Id + 100, s.Strength)).ToList();
        var placements = new Dictionary<string, IReadOnlyList<School>>
        {
            ["kinki"] = kinki,
            ["kanto"] = kanto,
        };
        var berths = new SenbatsuBerths();
        var withoutJingu = SenbatsuSelection.Select(placements, berths);
        // 既定: kinki 6 + kanto 4 = 10。
        Assert.Equal(berths.GeneralFor("kinki") + berths.GeneralFor("kanto"), withoutJingu.Count);

        var withJingu = SenbatsuSelection.Select(placements, berths, jinguChampionRegion: "kanto");
        Assert.Equal(withoutJingu.Count + 1, withJingu.Count); // 神宮枠 +1
    }

    [Fact]
    public void Jingu_TenRegionChampions_ProduceOneWinner()
    {
        // 神宮大会は10地区王者のトーナメント（knockout の再利用）。
        var champions = Schools(10);
        var r = Knockout.Run(champions, Coeff, new Xoshiro256Random(3));
        Assert.Contains(r.Champion, champions);
    }

    // ===== 現代ルールトグル（年代連動・手動OFF） =====

    [Fact]
    public void ModernRules_YearLinked_AutoOnFromIntroYear()
    {
        var rules = new ModernRules();
        Assert.False(rules.DhEnabled(2024));
        Assert.True(rules.DhEnabled(2025));
        Assert.True(rules.TieBreakEnabled(2018));
        Assert.False(rules.TieBreakEnabled(2017));
    }

    [Fact]
    public void ModernRules_ForcedOff_OverridesYear()
    {
        var rules = new ModernRules { DhForcedOff = true };
        Assert.False(rules.DhEnabled(2030));
    }

    [Fact]
    public void ModernRules_PitchLimit_UnlimitedBeforeIntro()
    {
        var rules = new ModernRules();
        Assert.Equal(int.MaxValue, rules.EffectiveWeeklyPitchLimit(2019));
        Assert.Equal(500, rules.EffectiveWeeklyPitchLimit(2021));
    }

    // ===== YAMLローダ（不変条件#4） =====

    [Fact]
    public void Loader_ParsesAllThreeSampleFormats()
    {
        var nara = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/nara.yaml"));
        Assert.Equal("nara", nara.Pref);
        Assert.Equal(DistrictAssignment.None, nara.DistrictAssignment);
        Assert.Single(nara.Stages);
        Assert.Equal(StageType.Knockout, nara.Stages[0].Type);
        Assert.True(nara.Stages[0].ThirdPlaceMatch);

        var kanagawa = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/kanagawa.yaml"));
        Assert.Equal(DistrictAssignment.Geographic, kanagawa.DistrictAssignment);
        Assert.Equal(StageType.GroupSplit, kanagawa.Stages[0].Type);
        Assert.Equal(StageType.RoundRobin, kanagawa.Stages[0].Child!.Type);

        var hyogo = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/hyogo.yaml"));
        Assert.True(hyogo.Stages[0].LoserBracket!.Enabled);
        Assert.Equal(5, hyogo.Stages[0].Groups);
    }
}
