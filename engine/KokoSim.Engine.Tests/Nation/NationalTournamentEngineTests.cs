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

/// <summary>全国裏試合フルシム・オーケストレータ（#43 全国47県）のテスト。</summary>
public sealed class NationalTournamentEngineTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    // 小さな全国（3県 × 6校）を組む（本番4000校の性能実測は Heavy へ）。
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

    private static (IReadOnlyList<PrefectureResult> Results, NationTournamentStats Stats) RunAll(
        NationModel nation, ulong seed, int? exclude = null)
    {
        var rosters = new NationRosters(new AiRosterDeps());
        var stats = new NationTournamentStats();
        var results = NationalTournamentEngine.RunSummer(
            nation, rosters, new GameContext(), Coeff, Schedule, yearIndex: 1, stats,
            new Xoshiro256Random(seed), excludePrefectureId: exclude);
        return (results, stats);
    }

    [Fact]
    public void RunSummer_ProducesChampionPerPrefecture_AndNationalStats()
    {
        var nation = MakeMiniNation();
        var (results, stats) = RunAll(nation, 42);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.ChampionName)));

        // 全国集計に3県ぶんの学校が積まれている。
        Assert.True(stats.Schools.Count >= 3);
        var totalHits = stats.Schools.Sum(id => stats.ForSchool(id)!.Players.Values.Sum(p => p.Batting.Hits));
        Assert.True(totalHits > 0);
    }

    [Fact]
    public void RunSummer_IsDeterministic()
    {
        var a = RunAll(MakeMiniNation(), 5);
        var b = RunAll(MakeMiniNation(), 5);
        Assert.Equal(
            a.Results.Select(r => r.ChampionName),
            b.Results.Select(r => r.ChampionName));
    }

    [Fact]
    public void RunSummer_SkipsExcludedPrefecture()
    {
        var nation = MakeMiniNation();
        var (results, _) = RunAll(nation, 7, exclude: 2);
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.PrefectureId == 2);
    }
}
