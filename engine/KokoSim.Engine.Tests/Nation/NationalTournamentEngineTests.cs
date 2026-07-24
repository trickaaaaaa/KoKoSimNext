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
    public void SharedContainers_AreThreadSafe_AcrossParallelPrefectures()
    {
        // 全国裏試合の背景スレッド化（設計書05 §1.4）を模す: 同一の rosters/stats へ複数県が並行に積む。
        // 県キーは disjoint なので論理競合はなく、辞書構造の破損だけをロックが防ぐ（例外なく完走すること）。
        var deps = new AiRosterDeps();
        var rosters = new NationRosters(deps);
        var stats = new NationTournamentStats();
        var ctx = new GameContext();

        var prefs = Enumerable.Range(1, 6).Select(p => MakePrefectureSchools(p * 1000, 8)).ToList();

        System.Threading.Tasks.Parallel.ForEach(prefs.Select((s, i) => (s, i)), pair =>
        {
            var bg = new BackgroundMatchResolver(rosters, ctx, yearIndex: 1, stats: stats);
            var runner = new TournamentRunner(pair.s, pair.s[0], Coeff, new Xoshiro256Random((ulong)(pair.i + 1)),
                Schedule, $"P{pair.i}", playerResolver: null, backgroundResolver: bg);
            while (!runner.Finished) runner.PlayNextPlayerMatch();
        });

        Assert.True(stats.Schools.Count >= 6);
        var hits = stats.Schools.Sum(id => stats.ForSchool(id)!.Players.Values.Sum(p => p.Batting.Hits));
        Assert.True(hits > 0);
    }

    private static List<School> MakePrefectureSchools(int baseId, int count)
        => Enumerable.Range(0, count).Select(i => new School
        {
            Id = baseId + i, Name = $"校{baseId + i}", PrefectureId = baseId / 1000,
            Strength = 44 + (i * 31) % 38, Fame = 45,
        }).ToList();

    /// <summary>学校ごとの通算成績を並び順非依存の文字列へ畳む（決定論回帰の digest）。</summary>
    private static string StatsDigest(NationTournamentStats stats)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var schoolId in stats.Schools.OrderBy(x => x))
        {
            var book = stats.ForSchool(schoolId)!;
            sb.Append(schoolId).Append(':');
            foreach (var (pid, line) in book.Players.OrderBy(kv => kv.Key))
                sb.Append(pid).Append('#')
                  .Append(line.Batting.Hits).Append('/')
                  .Append(line.Batting.AtBats).Append('/')
                  .Append(line.Pitching.StrikeOuts).Append('/')
                  .Append(line.Pitching.Outs).Append(';');
            sb.Append('|');
        }
        return sb.ToString();
    }

    [Fact]
    public void BeginSummer_SlicedDriver_MatchesMonolithicRunSummer()
    {
        // 一括版（RunSummer）と、ジョブを1本ずつ順に実行する版（Shell の逐次スロットル駆動を模す）で、
        // champion 列・stats digest が完全一致すること（#208 スライス化前後の決定論不変）。
        var nation = MakeMiniNation();

        var monoStats = new NationTournamentStats();
        var monoResults = NationalTournamentEngine.RunSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, monoStats, new Xoshiro256Random(99));

        var slicedStats = new NationTournamentStats();
        var run = NationalTournamentEngine.BeginSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, slicedStats, new Xoshiro256Random(99));
        var slicedResults = run.Jobs.Select(j => j()).ToList();   // 1本ずつ実行＝逐次スライス駆動

        Assert.Equal(run.Total, run.Jobs.Count);                   // 母数＝ジョブ数（進捗バーの整合）
        Assert.Equal(monoResults.Count, slicedResults.Count);
        Assert.Equal(
            monoResults.Select(r => (r.PrefectureId, r.ChampionName)),
            slicedResults.Select(r => (r.PrefectureId, r.ChampionName)));
        Assert.Equal(StatsDigest(monoStats), StatsDigest(slicedStats));
    }

    [Fact]
    public void BeginSummer_ParallelJobs_MatchesSequential()
    {
        // ジョブを複数スレッドで並列実行しても（Shell の Boost 時の並列消化を模す）、逐次実行と
        // champion・stats digest が完全一致すること（#208 並列化の決定論不変＝区画は独立 Fork）。
        var nation = MakeMiniNation();

        var seqStats = new NationTournamentStats();
        var seqResults = NationalTournamentEngine.RunSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, seqStats, new Xoshiro256Random(123));

        var parStats = new NationTournamentStats();
        var run = NationalTournamentEngine.BeginSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, parStats, new Xoshiro256Random(123));
        System.Threading.Tasks.Parallel.ForEach(run.Jobs, job => job());

        Assert.Equal(StatsDigest(seqStats), StatsDigest(parStats));   // 並列でも積算結果は不変
    }

    [Fact]
    public void BeginSummer_Total_EqualsPrefectureCount_AndOneJobPerPrefecture()
    {
        // スライス単位の上限＝1区画/ジョブ（確保フットプリントの刻み保証）。分割区画も1区画1ジョブ。
        var nation = MakeNationWithHokkaidoAndTokyo();
        var run = NationalTournamentEngine.BeginSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, new NationTournamentStats(), new Xoshiro256Random(3));

        Assert.Equal(4, run.Total);                       // 北海道2区画＋東京2区画
        Assert.Equal(4, run.Jobs.Count);                  // 母数ぶんきっかりジョブがある
    }

    [Fact]
    public void BeginSummer_ExcludesPrefecture_FromTotalAndJobs()
    {
        var nation = MakeMiniNation();
        var run = NationalTournamentEngine.BeginSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, new NationTournamentStats(), new Xoshiro256Random(7), excludePrefectureId: 2);

        Assert.Equal(2, run.Total);
        Assert.DoesNotContain(run.Jobs.Select(j => j()), r => r.PrefectureId == 2);
    }

    [Fact]
    public void RunSummer_SkipsExcludedPrefecture()
    {
        var nation = MakeMiniNation();
        var (results, _) = RunAll(nation, 7, exclude: 2);
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.PrefectureId == 2);
    }

    // 北海道(0)・東京(12)は夏の地方大会だけ2区画に分割される（設計書05 §1.1 / issue #65）。
    private static NationModel MakeNationWithHokkaidoAndTokyo()
    {
        var prefs = new List<Prefecture> { new(0, "北海道", 8), new(12, "東京", 8) };
        var schools = new List<School>();
        var id = 0;
        foreach (var pref in prefs)
        {
            for (var s = 0; s < 8; s++)
            {
                schools.Add(new School
                {
                    Id = id++, Name = $"{pref.Name}校{s}", PrefectureId = pref.Id, Strength = 40 + s, Fame = 45,
                });
            }
        }
        return new NationModel(prefs, schools);
    }

    [Fact]
    public void RunSummer_SplitsHokkaidoAndTokyoIntoTwoRegionsEach()
    {
        var nation = MakeNationWithHokkaidoAndTokyo();
        var (results, _) = RunAll(nation, 11);

        Assert.Equal(4, results.Count);
        var names = results.Select(r => r.PrefectureName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "北北海道", "南北海道", "東東京", "西東京" }.OrderBy(n => n), names);
        Assert.All(results, r => Assert.True(r.PrefectureId == 0 || r.PrefectureId == 12));
    }
}
