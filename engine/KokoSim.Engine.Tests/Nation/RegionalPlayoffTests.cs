using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 近畿の秋季地区大会「出場校決定戦」（2026年〜・issue #1290）。4府県は各2校＋各県3位どうしの決定戦2試合の勝者2校で計16校。
/// </summary>
public sealed class RegionalPlayoffTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly string[] FourPrefs = { "shiga", "kyoto", "nara", "wakayama" };

    private static RegionalFormat Kinki()
        => KokoSim.Config.RegionalFormatLoader.LoadFromFile(
            Balance.BalanceRegressionTests.FindDataFile("pref-formats/regional-tournaments.yaml")).ForRegion("kinki")!;

    private static School Sch(int id, double strength = 50) => new() { Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength };

    /// <summary>府県ごとに6校（Id=県番号*100+順位）。</summary>
    private static List<(string, IReadOnlyList<School>)> KinkiPlacements()
    {
        var prefs = new[] { "osaka", "hyogo", "shiga", "kyoto", "nara", "wakayama" };
        return prefs.Select((p, pi) =>
            (p, (IReadOnlyList<School>)Enumerable.Range(0, 6).Select(r => Sch(pi * 100 + r, 70 - r * 5)).ToList())).ToList();
    }

    [Fact]
    public void Kinki_From2026_FourPrefsGetTwoDirectBerthsEach()
    {
        var kinki = Kinki();
        Assert.True(kinki.PlayoffActive(2026));
        Assert.False(kinki.PlayoffActive(2025));
        foreach (var p in FourPrefs)
        {
            Assert.Equal(2, kinki.BerthsFor(p, 2026));
            Assert.Equal(2, kinki.BerthsFor(p, 2027));   // 隔年制は2026年以降もう効かない
            Assert.Equal(2, kinki.PlayoffCandidateIndex(p, 2026));   // 3位＝添字2
            Assert.Null(kinki.PlayoffCandidateIndex(p, 2025));
        }
        Assert.Equal(3, kinki.BerthsFor("osaka", 2026));
        Assert.Equal(3, kinki.BerthsFor("hyogo", 2027));
        Assert.Null(kinki.PlayoffCandidateIndex("osaka", 2026));
    }

    [Fact]
    public void Kinki_Through2025_KeepsBiennialRotationWithoutPlayoff()
    {
        var kinki = Kinki();
        Assert.Equal(3, kinki.BerthsFor("kyoto", 2024));
        Assert.Equal(2, kinki.BerthsFor("kyoto", 2025));
        Assert.Equal(3, kinki.BerthsFor("nara", 2025));
        var (direct, candidates) = RegionalPlayoff.Split(kinki, 2025, KinkiPlacements());
        Assert.Equal(16, direct.Count);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Split_2026_TakesThirdPlacesOfTheFourPrefsAsCandidates()
    {
        var (direct, candidates) = RegionalPlayoff.Split(Kinki(), 2026, KinkiPlacements());
        Assert.Equal(14, direct.Count);   // 大阪3＋兵庫3＋4府県×2
        Assert.Equal(4, candidates.Count);
        Assert.Equal(new[] { 202, 302, 402, 502 }, candidates.Select(c => c.Id).OrderBy(i => i));
        Assert.DoesNotContain(direct, d => candidates.Contains(d));
    }

    [Fact]
    public void Qualify_2026_Gives16SchoolsWithTwoPlayoffWinners()
    {
        var q = RegionalPlayoff.Qualify(Kinki(), 2026, KinkiPlacements(), Coeff, new Xoshiro256Random(5));
        Assert.Equal(16, q.Count);
        Assert.Equal(16, q.Select(s => s.Id).Distinct().Count());
        // 4府県の3位（Id 202/302/402/502）のうち残るのは2校だけ
        Assert.Equal(2, q.Count(s => s.Id is 202 or 302 or 402 or 502));
    }

    [Fact]
    public void Resolve_PairsEachCandidateOnceAndWinnersCameFromTheirPair()
    {
        var cands = new[] { Sch(1, 90), Sch(2, 10), Sch(3, 80), Sch(4, 20) };
        var pairs = RegionalPlayoff.Pair(cands, new Xoshiro256Random(3));
        Assert.Equal(2, pairs.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, pairs.SelectMany(p => new[] { p.A.Id, p.B!.Id }).OrderBy(i => i));

        var winners = RegionalPlayoff.Resolve(cands, Coeff, new Xoshiro256Random(3));
        Assert.Equal(2, winners.Count);
        for (var i = 0; i < 2; i++) Assert.Contains(winners[i].Id, new[] { pairs[i].A.Id, pairs[i].B!.Id });
    }

    [Fact]
    public void Pair_DoesNotDependOnInputOrder()
    {
        var cands = new[] { Sch(1), Sch(2), Sch(3), Sch(4) };
        var a = RegionalPlayoff.Pair(cands, new Xoshiro256Random(9));
        var b = RegionalPlayoff.Pair(cands.Reverse().ToArray(), new Xoshiro256Random(9));
        Assert.Equal(a.Select(p => (p.A.Id, p.B?.Id)), b.Select(p => (p.A.Id, p.B?.Id)));
    }

    [Fact]
    public void Resolve_OddCandidateCount_LastOneAdvancesByBye()
    {
        var winners = RegionalPlayoff.Resolve(new[] { Sch(1), Sch(2), Sch(3) }, Coeff, new Xoshiro256Random(1));
        Assert.Equal(2, winners.Count);
    }

    [Fact]
    public void Play_IsPerPairStream_SoOnePairResultIgnoresTheOthers()
    {
        // 自校の組だけ対話で戦い、残りの組を裏で解くため、組 i の勝者は他の組を解いたかどうかに依らない。
        var a = Sch(1, 55); var b = Sch(2, 50);
        var alone = RegionalPlayoff.Play(a, b, 1, Coeff, new Xoshiro256Random(21));
        var rng = new Xoshiro256Random(21);
        RegionalPlayoff.Play(Sch(8, 60), Sch(9, 40), 0, Coeff, rng);
        var afterOther = RegionalPlayoff.Play(a, b, 1, Coeff, rng);
        Assert.Equal(alone.Id, afterOther.Id);
    }

    [Fact]
    public void Qualify_IsDeterministic()
    {
        var a = RegionalPlayoff.Qualify(Kinki(), 2027, KinkiPlacements(), Coeff, new Xoshiro256Random(8));
        var b = RegionalPlayoff.Qualify(Kinki(), 2027, KinkiPlacements(), Coeff, new Xoshiro256Random(8));
        Assert.Equal(a.Select(s => s.Id), b.Select(s => s.Id));
    }

    [Fact]
    public void RunOtherRegions_Kinki2028_HasSixteenWithPlayoffAppliedOnce()
    {
        var formats = KokoSim.Config.RegionalFormatLoader.LoadFromFile(
            Balance.BalanceRegressionTests.FindDataFile("pref-formats/regional-tournaments.yaml"));
        var placements = new Dictionary<int, IReadOnlyList<int>>();
        var byId = new Dictionary<int, School>();
        for (var pref = 0; pref < 47; pref++)
        {
            var ids = Enumerable.Range(0, 6).Select(s => pref * 100 + s).ToList();
            foreach (var id in ids) byId[id] = Sch(id, 70 - (id % 100) * 5);
            placements[pref] = ids;
        }
        var results = RegionalNationEngine.RunOtherRegions(
            formats, placements, PrefectureNames.RegionOf, id => byId[id], Coeff, new Xoshiro256Random(2), calendarYear: 2028);

        var kinki = results.Single(r => r.Region == "kinki");
        Assert.Equal(16, kinki.PlacementIds.Count);
        var jis = PrefectureNames.JisOrder.ToList();
        var thirdOfFour = FourPrefs.Select(p => jis.IndexOf(p) * 100 + 2).ToHashSet();
        Assert.Equal(2, kinki.PlacementIds.Count(thirdOfFour.Contains));
    }
}
