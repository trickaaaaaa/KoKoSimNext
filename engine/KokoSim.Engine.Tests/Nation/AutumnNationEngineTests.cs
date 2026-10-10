using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;
using Xunit;
using NationModel = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>秋季全国裏試合オーケストレータ（<see cref="AutumnNationEngine"/>）。夏の47県版＋順位付き。</summary>
public sealed class AutumnNationEngineTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static NationModel MakeMiniNation()
    {
        var prefs = new List<Prefecture>();
        var schools = new List<School>();
        var id = 0;
        for (var p = 1; p <= 3; p++)
        {
            prefs.Add(new Prefecture(p, $"県{p}", 6));
            for (var s = 0; s < 6; s++)
                schools.Add(new School
                {
                    Id = id++, Name = $"県{p}校{s}", PrefectureId = p,
                    Strength = 42 + (s * 29) % 40, Fame = 45,
                });
        }
        return new NationModel(prefs, schools);
    }

    private static (IReadOnlyList<AutumnPrefectureResult> Results, NationTournamentStats Stats) RunAll(
        NationModel nation, ulong seed, int? exclude = null)
    {
        var rosters = new NationRosters(new AiRosterDeps());
        var stats = new NationTournamentStats();
        var results = AutumnNationEngine.RunAutumn(
            nation, rosters, new GameContext(), Coeff, Schedule, yearIndex: 1, stats,
            new Xoshiro256Random(seed), excludePrefectureId: exclude);
        return (results, stats);
    }

    [Fact]
    public void RunAutumn_ProducesPlacementPerPrefecture()
    {
        var nation = MakeMiniNation();
        var (results, _) = RunAll(nation, 42);

        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.HasChampion);
            Assert.Equal(6, r.PlacementIds.Count);                       // 6校＝全員が順位に載る
            Assert.Equal(r.PlacementIds.Distinct().Count(), r.PlacementIds.Count); // 重複なし
        });
    }

    [Fact]
    public void RunAutumn_ExcludesPrefecture()
    {
        var nation = MakeMiniNation();
        var (results, _) = RunAll(nation, 7, exclude: 2);
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.PrefectureId == 2);
    }

    [Fact]
    public void RunAutumn_IsDeterministic()
    {
        var nation = MakeMiniNation();
        var (a, _) = RunAll(nation, 99);
        var (b, _) = RunAll(nation, 99);
        Assert.Equal(
            a.Select(r => (r.PrefectureId, string.Join(",", r.PlacementIds))),
            b.Select(r => (r.PrefectureId, string.Join(",", r.PlacementIds))));
    }

    [Fact]
    public void RunAutumn_RegionRomaji_MatchesPrefectureNames()
    {
        var nation = MakeMiniNation();
        var (results, _) = RunAll(nation, 5);
        Assert.All(results, r => Assert.Equal(PrefectureNames.RegionOf(r.PrefectureId), r.RegionRomaji));
    }

    [Fact]
    public void BeginAutumn_ChampionsOnly_StillProducesFullPlacement()
    {
        var nation = MakeMiniNation();
        var run = AutumnNationEngine.BeginAutumn(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, new NationTournamentStats(), new Xoshiro256Random(42),
            resolution: SummerResolution.ChampionsOnly);

        foreach (var r in run.Jobs.Select(j => j()))
        {
            Assert.True(r.HasChampion);
            Assert.Equal(6, r.PlacementIds.Count);
        }
    }

    [Fact]
    public void Top_ReturnsTopNInPlacementOrder()
    {
        var nation = MakeMiniNation();
        var (results, _) = RunAll(nation, 3);
        var r = results[0];
        var top2 = r.Top(2);
        Assert.Equal(2, top2.Count);
        Assert.Equal(r.PlacementIds.Take(2), top2);
    }
}
