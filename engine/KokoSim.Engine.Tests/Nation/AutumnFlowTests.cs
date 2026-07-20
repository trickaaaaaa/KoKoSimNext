using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;
using NationState = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 秋の大会フロー配線（設計書05 §1.5/§4, A-1）。秋季県大会→地区大会→明治神宮→センバツ選考を
/// 決定論で回すオーケストレータ（AutumnFlowEngine）と、47県→地区の対応表（prefectures.yaml）を検証する。
/// </summary>
public sealed class AutumnFlowTests
{
    private static readonly NationCoefficients Coeff = new();
    private static string DataFile(string name) => Balance.BalanceRegressionTests.FindDataFile(name);

    private static PrefectureTable PrefTable()
        => KokoSim.Config.PrefectureTableLoader.LoadFromFile(DataFile("prefectures.yaml"));

    private static RegionalFormatSet Regionals()
        => KokoSim.Config.RegionalFormatLoader.LoadFromFile(DataFile("pref-formats/regional-tournaments.yaml"));

    // 47県×schoolsPerPref校の軽量な合成全国（生成器非依存・決定論）。
    private static NationState SyntheticNation(int schoolsPerPref, ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var prefs = Enumerable.Range(0, 47)
            .Select(i => new Prefecture(i, $"第{i + 1}県", schoolsPerPref)).ToList();
        var schools = new List<School>();
        var id = 0;
        foreach (var p in prefs)
        {
            for (var j = 0; j < schoolsPerPref; j++)
            {
                schools.Add(new School
                {
                    Id = id++,
                    Name = $"校{id}",
                    PrefectureId = p.Id,
                    Strength = MathUtil.Clamp(rng.NextGaussian(44, 13), 8, 99),
                });
            }
        }
        return new NationState(prefs, schools);
    }

    private static AutumnFlowResult Run(ulong seed, int year = 2025)
        => AutumnFlowEngine.Run(
            SyntheticNation(16, seed), PrefTable(), Regionals(), new SenbatsuBerths(),
            Coeff, year, new Xoshiro256Random(seed));

    // ===== 対応表（prefectures.yaml） =====

    [Fact]
    public void PrefectureTable_Loads47PrefsCoveringAllRegions()
    {
        var t = PrefTable();
        Assert.Equal(47, t.Prefectures.Count);
        Assert.Equal(47, t.Prefectures.Select(p => p.Id).Distinct().Count());
        Assert.Equal(47, t.Prefectures.Select(p => p.Name).Distinct().Count());
        // Id は 0..46 を隙間なく覆う。
        Assert.Equal(Enumerable.Range(0, 47), t.Prefectures.Select(p => p.Id).OrderBy(x => x));
        // prefTable の地区名が regional-tournaments.yaml の地区集合と完全一致（2データファイルの整合ガード）。
        var regionNames = Regionals().Regions.Select(r => r.Region).ToHashSet();
        Assert.All(t.Prefectures, p => Assert.Contains(p.Region, regionNames));
        Assert.Equal(regionNames.OrderBy(x => x), t.Regions().OrderBy(x => x));
    }

    // ===== 神宮動線（地区王者10校） =====

    [Fact]
    public void JinguField_HasTenRegionChampions()
    {
        var r = Run(1);
        Assert.Equal(10, r.JinguField.Count);
        Assert.Equal(10, r.JinguField.Select(s => s.Id).Distinct().Count());
        Assert.Contains(r.JinguChampion, r.JinguField);
        // 各地区の優勝校が神宮に出場している。
        var ids = r.JinguField.Select(s => s.Id).ToHashSet();
        foreach (var reg in r.Regions.Where(x => x.ToJingu))
            Assert.Contains(reg.Champion.Id, ids);
    }

    // ===== センバツ選考枠 =====

    [Fact]
    public void SenbatsuField_MatchesBerthsPlusJinguBonus()
    {
        var berths = new SenbatsuBerths();
        var r = Run(2);
        // 地区別一般枠の総和＋神宮枠+1。各地区の順位表は枠数を満たすだけの校数を持つ。
        var expected = r.Regions.Sum(x => berths.GeneralFor(x.Region)) + 1;
        Assert.Equal(expected, r.SenbatsuField.Count);
        Assert.Equal(r.SenbatsuField.Count, r.SenbatsuField.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void SenbatsuBerths_DefaultTotal_MatchesDesignTarget_32()
    {
        // 設計書05 §4「例年32校規模・21世紀枠非採用」。一般枠31＋神宮枠1＝32を固定（枠の数え間違い回帰ガード）。
        var berths = new SenbatsuBerths();
        var generalTotal = berths.GeneralByRegion.Values.Sum();
        Assert.Equal(31, generalTotal);

        // 十分な校数の合成全国では実出場数も32になる（各地区が枠を満たす順位表を持つ）。
        var r = Run(3);
        Assert.Equal(32, r.SenbatsuField.Count);
    }

    // ===== 決定論 =====

    [Fact]
    public void Run_IsDeterministic()
    {
        var a = Run(7);
        var b = Run(7);
        Assert.Equal(a.SenbatsuField.Select(s => s.Id), b.SenbatsuField.Select(s => s.Id));
        Assert.Equal(a.JinguChampion.Id, b.JinguChampion.Id);
        Assert.Equal(a.Regions.Select(x => x.Champion.Id), b.Regions.Select(x => x.Champion.Id));
    }

    // ===== 専用フォーマットの解決 =====

    // ===== NationEngine への配線（1b） =====

    [Fact]
    public void NationEngine_AutumnFlow_RecordsJinguAndSenbatsuEachYear()
    {
        var history = NationEngine.Run(
            5, new SchoolNameVocab(), Coeff, new Xoshiro256Random(42),
            PrefTable(), Regionals());
        Assert.All(history.Years, y =>
        {
            Assert.NotNull(y.Autumn);
            Assert.NotEmpty(y.Autumn!.Senbatsu);
            Assert.False(string.IsNullOrEmpty(y.Autumn.JinguChampionRegion));
        });
    }

    [Fact]
    public void NationEngine_AutumnFlow_DoesNotPerturbSummerStream()
    {
        // 秋フローは Fork で回すので、夏の優勝列・平均強さは秋オフの既存挙動と1ビットも変わらない。
        var vocab = new SchoolNameVocab();
        var baseline = NationEngine.Run(8, vocab, Coeff, new Xoshiro256Random(7));
        var withAutumn = NationEngine.Run(
            8, vocab, Coeff, new Xoshiro256Random(7), PrefTable(), Regionals());
        for (var i = 0; i < baseline.Years.Count; i++)
        {
            Assert.Equal(baseline.Years[i].ChampionId, withAutumn.Years[i].ChampionId);
            Assert.Equal(baseline.Years[i].AverageStrength, withAutumn.Years[i].AverageStrength, 9);
            Assert.Null(baseline.Years[i].Autumn);       // 既定オフ
            Assert.NotNull(withAutumn.Years[i].Autumn);  // 有効時のみ記録
        }
    }

    // ===== A-3: 県内地区 DistrictId 付与 =====

    [Fact]
    public void NationGenerator_AssignsDistrictId_ForGeographicPrefs_Only()
    {
        // 県5(=Id5)に4地区、他は地区なし、の計画で生成。
        var plan = new Dictionary<int, int> { [5] = 4 };
        var nation = NationGenerator.Generate(new SchoolNameVocab(), Coeff, new Xoshiro256Random(1), plan);

        var geo = nation.InPrefecture(5).ToList();
        Assert.NotEmpty(geo);
        Assert.All(geo, s => Assert.NotNull(s.DistrictId));
        Assert.All(geo, s => Assert.InRange(s.DistrictId!.Value, 0, 3));
        Assert.True(geo.Select(s => s.DistrictId).Distinct().Count() > 1, "4地区に分かれていない");

        // 計画外の県は null のまま。
        Assert.All(nation.InPrefecture(6), s => Assert.Null(s.DistrictId));
    }

    [Fact]
    public void DistrictAssignment_DoesNotPerturbGeneration()
    {
        // DistrictId は Fork で付与＝強さ・名声・校風の生成列は地区計画の有無で1ビットも変わらない。
        var vocab = new SchoolNameVocab();
        var baseline = NationGenerator.Generate(vocab, Coeff, new Xoshiro256Random(9));
        var withPlan = NationGenerator.Generate(vocab, Coeff, new Xoshiro256Random(9),
            new Dictionary<int, int> { [5] = 4, [13] = 5 });
        Assert.Equal(baseline.Schools.Count, withPlan.Schools.Count);
        for (var i = 0; i < baseline.Schools.Count; i++)
        {
            Assert.Equal(baseline.Schools[i].Strength, withPlan.Schools[i].Strength, 9);
            Assert.Equal(baseline.Schools[i].Name, withPlan.Schools[i].Name);
            Assert.Equal(baseline.Schools[i].Style, withPlan.Schools[i].Style);
            Assert.Equal(baseline.Schools[i].TacticalSense, withPlan.Schools[i].TacticalSense);
            Assert.Null(baseline.Schools[i].DistrictId); // 計画なしは常に null
        }
    }

    [Fact]
    public void DistrictPlan_Build_UsesGeographicGroupCount()
    {
        var prefTable = PrefTable();
        var formats = new Dictionary<string, PrefFormat>
        {
            ["kanagawa"] = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/kanagawa.yaml")),
            ["hyogo"] = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/hyogo.yaml")),
            ["nara"] = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/nara.yaml")),
        };
        var plan = DistrictPlan.Build(prefTable, formats);

        var kanagawaId = prefTable.Prefectures.First(p => p.Name == "kanagawa").Id;
        var hyogoId = prefTable.Prefectures.First(p => p.Name == "hyogo").Id;
        var naraId = prefTable.Prefectures.First(p => p.Name == "nara").Id;
        Assert.Equal(4, plan[kanagawaId]);       // group_split groups=4（districts一覧は7だが運用4）
        Assert.Equal(5, plan[hyogoId]);           // group_split groups=5
        Assert.False(plan.ContainsKey(naraId));   // nara は district_assignment=none
    }

    [Fact]
    public void SpecificPrefFormat_IsUsed_AndFlowCompletes()
    {
        // nara は district_assignment=none の knockout（合成校でそのまま実行可能）。
        var formats = new Dictionary<string, PrefFormat>
        {
            ["nara"] = KokoSim.Config.PrefFormatLoader.LoadFromFile(DataFile("pref-formats/nara.yaml")),
        };
        var r = AutumnFlowEngine.Run(
            SyntheticNation(24, 3), PrefTable(), Regionals(), new SenbatsuBerths(),
            Coeff, 2025, new Xoshiro256Random(3), formats);
        Assert.Equal(10, r.JinguField.Count);
        Assert.NotEmpty(r.SenbatsuField);
    }
}
