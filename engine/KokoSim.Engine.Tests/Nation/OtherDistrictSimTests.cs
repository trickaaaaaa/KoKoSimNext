using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 自校が居ない地区の秋季地区予選（issue #1106）。勝ち上がった相手校の選手成績が全国の成績帳に積まれること、
/// シムが成績を自前で積まない（呼び出し側が1回だけ積む）こと、決定論を確かめる。
/// </summary>
public sealed class OtherDistrictSimTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static List<School> MakeSchools(int baseId, int count)
        => Enumerable.Range(0, count).Select(i => new School
        {
            Id = baseId + i,
            Name = $"架空{baseId + i}",
            PrefectureId = 14,
            DistrictId = 1,
            Strength = 40 + (i * 37) % 45,
            Fame = 40 + (i * 13) % 30,
        }).ToList();

    private static BackgroundMatchResolver NewResolver(NationRosters rosters)
        => new(rosters, new GameContext(), yearIndex: 1, stats: null);

    private static IReadOnlyList<IReadOnlyList<School>> Blocks(List<School> schools)
        => new IReadOnlyList<School>[] { schools.Take(4).ToList(), schools.Skip(4).Take(4).ToList() };

    [Fact]
    public void BlockLeague_QualifiersHaveCareerStats_AfterFoldingGames()
    {
        var schools = MakeSchools(1000, 8);
        var rosters = new NationRosters(new AiRosterDeps());

        var result = OtherDistrictSim.RunBlockLeague(Blocks(schools), 2, Coeff, new Xoshiro256Random(11),
            Schedule, "秋季地区予選代表決定", NewResolver(rosters));

        Assert.Equal(4, result.Qualifiers.Count);
        // 4校ブロックの総当たり＝6試合×2ブロック
        Assert.Equal(12, result.Games.Count);

        var stats = new NationTournamentStats();
        foreach (var g in result.Games) stats.FoldMatch(g.AwayId, g.HomeId, g.Game);

        foreach (var q in result.Qualifiers)
        {
            var career = stats.CareerForSchool(q.Id);
            Assert.NotNull(career);
            Assert.Contains(career!.Players.Values, p => p.Batting.AtBats > 0);
            Assert.Contains(career.Players.Values, p => p.Pitching.Outs > 0);
        }
    }

    [Fact]
    public void Knockout_QualifiersHaveCareerStats_AfterFoldingGames()
    {
        var schools = MakeSchools(2000, 8);
        var rosters = new NationRosters(new AiRosterDeps());

        var result = OtherDistrictSim.RunKnockout(schools, 2, Coeff, new Xoshiro256Random(5),
            Schedule, "秋季地区予選代表決定", NewResolver(rosters));

        Assert.Equal(2, result.Qualifiers.Count);
        Assert.NotEmpty(result.Games);

        var stats = new NationTournamentStats();
        foreach (var g in result.Games) stats.FoldMatch(g.AwayId, g.HomeId, g.Game);
        Assert.All(result.Qualifiers, q =>
            Assert.Contains(stats.CareerForSchool(q.Id)!.Players.Values, p => p.Batting.AtBats > 0));
    }

    [Fact]
    public void Sim_DoesNotFoldStatsByItself()
    {
        var schools = MakeSchools(3000, 8);
        var stats = new NationTournamentStats();
        var rosters = new NationRosters(new AiRosterDeps());

        OtherDistrictSim.RunBlockLeague(Blocks(schools), 2, Coeff, new Xoshiro256Random(3),
            Schedule, "秋季地区予選代表決定", NewResolver(rosters));

        Assert.Empty(stats.CareerSchools);
    }

    [Fact]
    public void BlockLeague_IsDeterministic_ForSameSeed()
    {
        OtherDistrictResult Run()
            => OtherDistrictSim.RunBlockLeague(Blocks(MakeSchools(4000, 8)), 2, Coeff, new Xoshiro256Random(77),
                Schedule, "秋季地区予選代表決定", NewResolver(new NationRosters(new AiRosterDeps())));

        var a = Run();
        var b = Run();
        Assert.Equal(a.Qualifiers.Select(s => s.Id), b.Qualifiers.Select(s => s.Id));
        Assert.Equal(a.Games.Select(g => (g.AwayId, g.HomeId, g.Game.AwayRuns, g.Game.HomeRuns)),
            b.Games.Select(g => (g.AwayId, g.HomeId, g.Game.AwayRuns, g.Game.HomeRuns)));
    }

    [Fact]
    public void Knockout_SmallerThanAdvance_ReturnsEntrantsWithoutGames()
    {
        var schools = MakeSchools(5000, 2);
        var result = OtherDistrictSim.RunKnockout(schools, 2, Coeff, new Xoshiro256Random(1),
            Schedule, "t", NewResolver(new NationRosters(new AiRosterDeps())));
        Assert.Equal(2, result.Qualifiers.Count);
        Assert.Empty(result.Games);
    }
}
