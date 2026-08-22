using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 自地区を除いた秋季地区大会の計算（<see cref="RegionalNationEngine"/>）。
/// Shell が「自地区は対話プレイ、他9地区はここでまとめて解決」する構成の要（設計書05 §1.5b）。
/// </summary>
public sealed class RegionalNationEngineTests
{
    private static readonly NationCoefficients Coeff = new();

    private static RegionalFormatSet LoadRealFormats()
        => KokoSim.Config.RegionalFormatLoader.LoadFromFile(
            Balance.BalanceRegressionTests.FindDataFile("pref-formats/regional-tournaments.yaml"));

    /// <summary>47県ぶんの秋季県大会プレースメントを合成する（各県6校、県ごとに校IDをずらす）。</summary>
    private static (Dictionary<int, IReadOnlyList<int>> Placements, Dictionary<int, School> Byid) MakePlacements()
    {
        var placements = new Dictionary<int, IReadOnlyList<int>>();
        var byId = new Dictionary<int, School>();
        for (var pref = 0; pref < 47; pref++)
        {
            var ids = new List<int>();
            for (var s = 0; s < 6; s++)
            {
                var id = pref * 100 + s;
                ids.Add(id);
                byId[id] = new School { Id = id, Name = $"P{pref}-{s}", PrefectureId = pref, Strength = 40 + s * 5.0 };
            }
            placements[pref] = ids;
        }
        return (placements, byId);
    }

    [Fact]
    public void RunOtherRegions_Covers9RegionsWhenExcludingOne()
    {
        var formats = LoadRealFormats();
        var (placements, byId) = MakePlacements();

        var results = RegionalNationEngine.RunOtherRegions(
            formats, placements, PrefectureNames.RegionOf, id => byId[id],
            Coeff, new Xoshiro256Random(11), calendarYear: 2028, excludeRegion: "kanto");

        Assert.Equal(9, results.Count);
        Assert.DoesNotContain(results, r => r.Region == "kanto");
        Assert.All(results, r => Assert.True(r.HasChampion));
    }

    [Fact]
    public void RunOtherRegions_NoExclusion_Covers10Regions()
    {
        var formats = LoadRealFormats();
        var (placements, byId) = MakePlacements();

        var results = RegionalNationEngine.RunOtherRegions(
            formats, placements, PrefectureNames.RegionOf, id => byId[id],
            Coeff, new Xoshiro256Random(3), calendarYear: 2028);

        Assert.Equal(10, results.Count);
        // 単独県地区（東京・北海道）はそのまま県大会の順位＝プレースメントが県のものと一致する。
        var tokyo = results.Single(r => r.Region == "tokyo");
        Assert.Equal(placements[12], tokyo.PlacementIds); // Prefecture.Id=12 は東京
        var hokkaido = results.Single(r => r.Region == "hokkaido");
        Assert.Equal(placements[0], hokkaido.PlacementIds);
    }

    [Fact]
    public void RunOtherRegions_ChampionIsAmongTheAggregatedEntrants()
    {
        var formats = LoadRealFormats();
        var (placements, byId) = MakePlacements();

        var results = RegionalNationEngine.RunOtherRegions(
            formats, placements, PrefectureNames.RegionOf, id => byId[id],
            Coeff, new Xoshiro256Random(9), calendarYear: 2028);

        var kanto = results.Single(r => r.Region == "kanto");
        // 関東の全出場校（各県の出場枠ぶん）に地区王者が含まれること。
        var kantoPrefs = Enumerable.Range(0, 47).Where(p => PrefectureNames.RegionOf(p) == "kanto").ToList();
        var allEntrants = new HashSet<int>();
        foreach (var p in kantoPrefs)
        {
            var fmt = formats.ForRegion("kanto")!;
            var berths = System.Math.Max(1, fmt.BerthsFor(PrefectureNames.JisOrder[p], 2028));
            foreach (var id in placements[p].Take(berths)) allEntrants.Add(id);
        }
        Assert.Contains(kanto.ChampionId, allEntrants);
        Assert.Equal(kanto.PlacementIds.Count, kanto.PlacementIds.Distinct().Count());
    }

    [Fact]
    public void RunOtherRegions_IsDeterministic()
    {
        var formats = LoadRealFormats();

        IReadOnlyList<RegionalResult> Run()
        {
            var (placements, byId) = MakePlacements();
            return RegionalNationEngine.RunOtherRegions(
                formats, placements, PrefectureNames.RegionOf, id => byId[id],
                Coeff, new Xoshiro256Random(77), calendarYear: 2029);
        }

        var a = Run();
        var b = Run();
        Assert.Equal(
            a.Select(r => (r.Region, string.Join(",", r.PlacementIds))),
            b.Select(r => (r.Region, string.Join(",", r.PlacementIds))));
    }

    [Fact]
    public void RunOtherRegions_AllChampionToJingu_MatchesRealFile()
    {
        var formats = LoadRealFormats();
        var (placements, byId) = MakePlacements();

        var results = RegionalNationEngine.RunOtherRegions(
            formats, placements, PrefectureNames.RegionOf, id => byId[id],
            Coeff, new Xoshiro256Random(1), calendarYear: 2028);

        // 実データは10地区とも神宮動線を持つ（regional-tournaments.yaml 全件 champion_to_jingu: true）。
        Assert.All(results, r => Assert.True(r.ChampionToJingu));
    }
}
