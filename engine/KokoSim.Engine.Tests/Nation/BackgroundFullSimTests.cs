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
/// 裏試合フルシム化（#43 / Q15）の単体テスト。背景カードを永続ロスターの GameEngine.Play で解決し、
/// 全国通算成績へ両校のボックススコアを積むこと・決定論・相手今大会成績APIを検証する。
/// </summary>
public sealed class BackgroundFullSimTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static List<School> MakePrefecture(int baseId, int count)
    {
        var schools = new List<School>(count);
        for (var i = 0; i < count; i++)
            schools.Add(new School
            {
                Id = baseId + i,
                Name = $"架空{baseId + i}",
                PrefectureId = 13,
                Strength = 40 + (i * 37) % 45,   // 40〜84 に散らす
                Fame = 40 + (i * 13) % 30,
            });
        return schools;
    }

    private static (string? Champion, NationTournamentStats Stats) RunTournament(
        List<School> schools, ulong seed, NationRosters? sharedRosters = null,
        NationTournamentStats? sharedStats = null)
    {
        var rosters = sharedRosters ?? new NationRosters(new AiRosterDeps());
        var stats = sharedStats ?? new NationTournamentStats();
        var bg = new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1, stats: stats);
        var runner = new TournamentRunner(
            schools, schools[0], Coeff, new Xoshiro256Random(seed), Schedule, "テスト大会",
            playerResolver: null, backgroundResolver: bg);

        // playerResolver 無し＝自校カードも背景フルシムで解決される。大会を最後まで消化する。
        while (!runner.Finished) runner.PlayNextPlayerMatch();
        return (runner.ChampionName, stats);
    }

    [Fact]
    public void FullSim_ProducesChampion_AndAccumulatesStats()
    {
        var schools = MakePrefecture(100, 8);
        var (champion, stats) = RunTournament(schools, 42);

        Assert.NotNull(champion);
        // 成績を持つ学校が複数あり、少なくとも1校は安打を記録している。
        Assert.NotEmpty(stats.Schools);
        var totalHits = stats.Schools.Sum(id => stats.ForSchool(id)!.Players.Values.Sum(p => p.Batting.Hits));
        Assert.True(totalHits > 0, "全国集計に安打が1本も積まれていない");
    }

    [Fact]
    public void FullSim_IsDeterministic_ForSameSeed()
    {
        var a = RunTournament(MakePrefecture(200, 8), 7);
        var b = RunTournament(MakePrefecture(200, 8), 7);

        Assert.Equal(a.Champion, b.Champion);

        // 同一校の総安打・総打数が一致（全ボックススコアの再現性）。
        int Sum(NationTournamentStats s, System.Func<PlayerStats, int> pick)
            => s.Schools.OrderBy(x => x).Sum(id => s.ForSchool(id)!.Players.Values.Sum(pick));
        Assert.Equal(Sum(a.Stats, p => p.Batting.Hits), Sum(b.Stats, p => p.Batting.Hits));
        Assert.Equal(Sum(a.Stats, p => p.Batting.AtBats), Sum(b.Stats, p => p.Batting.AtBats));
        Assert.Equal(Sum(a.Stats, p => p.Pitching.StrikeOuts), Sum(b.Stats, p => p.Pitching.StrikeOuts));
    }

    [Fact]
    public void OpponentStats_AreQueryable_BeforeAndAfterMatches()
    {
        var schools = MakePrefecture(300, 16);
        var (_, stats) = RunTournament(schools, 99);

        // 勝ち上がった校は複数試合ぶんの成績が積み上がる＝「今大会打ってる」が読める。
        var withStats = stats.Schools.Where(id => stats.ForSchool(id)!.Players.Values.Any(p => p.Batting.AtBats > 0)).ToList();
        Assert.NotEmpty(withStats);
        var book = stats.ForSchool(withStats[0])!;
        Assert.All(book.Players.Values, p => Assert.True(p.SourceId > 0));
    }

    [Fact]
    public void NationalStats_AccumulateAcrossPrefectures_WithoutClearing()
    {
        // 全国＝複数県が同一の NationTournamentStats へ積む（県間でクリアしない）。
        var deps = new AiRosterDeps();
        var pref1 = MakePrefecture(400, 8);
        var pref2 = MakePrefecture(500, 8);
        var rosters = new NationRosters(deps);
        var stats = new NationTournamentStats();

        RunTournament(pref1, 1, rosters, stats);
        var afterPref1 = stats.Schools.Count;
        RunTournament(pref2, 2, rosters, stats);
        var afterPref2 = stats.Schools.Count;

        Assert.True(afterPref2 > afterPref1, "2県目の成績が全国集計に加算されていない");
        // 両県の学校がそれぞれ集計に存在する。
        Assert.Contains(stats.Schools, id => id is >= 400 and < 408);
        Assert.Contains(stats.Schools, id => id is >= 500 and < 508);
    }
}
