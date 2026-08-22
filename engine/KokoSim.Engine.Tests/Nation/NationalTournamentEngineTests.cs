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
            runner.DrainBracketResolution();   // issue #529: 敗退後の残り消化は明示的に最後まで回す
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

    /// <summary>
    /// 分割県（北海道/東京）は excludeSplit を添えると<b>片方の区画だけ</b>除外される＝もう片方は
    /// 通常どおりジョブに残り、独自の代表を出す（issue 北海道・東京分割。自校が分割県でも49代表になる）。
    /// </summary>
    [Fact]
    public void BeginSummer_ExcludesOnlyTheMatchingSplit_LeavesTheOtherHalf()
    {
        var nation = MakeNationWithHokkaidoAndTokyo();
        var run = NationalTournamentEngine.BeginSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, new NationTournamentStats(), new Xoshiro256Random(3),
            excludePrefectureId: 0, excludeSplit: 0);

        Assert.Equal(3, run.Total);   // 北北海道だけ除外＝南北海道＋東東京＋西東京が残る
        var results = run.Jobs.Select(j => j()).ToList();
        Assert.DoesNotContain(results, r => r.PrefectureName == "北北海道");
        Assert.Contains(results, r => r.PrefectureName == "南北海道");
        Assert.Contains(results, r => r.PrefectureName == "東東京");
        Assert.Contains(results, r => r.PrefectureName == "西東京");
    }

    /// <summary>excludeSplit を渡さない（null）と従来どおり分割県の両区画とも除外される
    /// ＝非分割プレフィクチャーを1区画丸ごと除外する既存呼び出し（秋の背景除外等）は挙動不変。</summary>
    [Fact]
    public void BeginSummer_ExcludeSplitNull_ExcludesBothHalvesOfASplitPrefecture()
    {
        var nation = MakeNationWithHokkaidoAndTokyo();
        var run = NationalTournamentEngine.BeginSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, new NationTournamentStats(), new Xoshiro256Random(3),
            excludePrefectureId: 0);

        Assert.Equal(2, run.Total);   // 北海道の2区画とも除外＝東京の2区画だけ残る
        var results = run.Jobs.Select(j => j()).ToList();
        Assert.All(results, r => Assert.Equal(12, r.PrefectureId));
    }

    // ── 甲子園（49代表）の入力 ────────────────────────────

    /// <summary>
    /// 代表は<b>校IDで</b>返る（校名だけでは同定できない＝甲子園のブラケットも成績の帰属キーもID）。
    /// </summary>
    [Fact]
    public void RunSummer_ReturnsChampionIdMatchingTheChampionName()
    {
        var nation = MakeMiniNation();
        var (results, _) = RunAll(nation, 42);

        Assert.All(results, r =>
        {
            Assert.True(r.HasChampion);
            Assert.Equal(r.ChampionName, nation.Schools.Single(s => s.Id == r.ChampionId).Name);
        });
    }

    /// <summary>
    /// 代表校の勝ち上がり（<see cref="ChampionMatchup"/> の並び）が、ブラケット上でその校が勝った
    /// 全カードと一致すること。県予選成績の再シムはこの並びをそのまま打ち直すので、
    /// <b>1試合でも欠けると成績が足りなくなる</b>。
    /// </summary>
    [Fact]
    public void RunSummer_ChampionPath_ListsEveryGameTheChampionWon()
    {
        var nation = MakeMiniNation();
        var (results, _) = RunAll(nation, 42);

        foreach (var r in results)
        {
            // 6校＝3ラウンド。不戦勝があるので実際の試合数はラウンド数以下。
            Assert.NotEmpty(r.Path);
            Assert.True(r.Path.Count <= 3);
            // 決勝（残り1）は必ず含む＝優勝までの最後の1勝。
            Assert.Contains(r.Path, m => m.RoundsRemaining == 1);
            // 相手に自分自身は入らない。ラウンドは重複しない（1ラウンド1試合）。
            Assert.DoesNotContain(r.Path, m => m.OpponentId == r.ChampionId);
            Assert.Equal(r.Path.Count, r.Path.Select(m => m.RoundsRemaining).Distinct().Count());
            // 積まれた順＝初戦から決勝へ（残りラウンド数は単調減少）。
            Assert.Equal(
                r.Path.Select(m => m.RoundsRemaining),
                r.Path.Select(m => m.RoundsRemaining).OrderByDescending(x => x));
        }
    }

    /// <summary>
    /// 自校が出場しない大会（<see cref="TournamentRunner.Spectated"/>）は、生成直後から
    /// 「残りブラケット消化待ち」になり、どのカードも自校扱いされない
    /// （＝樹形図で無関係な校がアンバー強調されない）。日程に合わせて1ラウンドずつ刻める。
    /// </summary>
    [Fact]
    public void Spectated_ResolvesRoundByRound_WithNoManagerHighlight()
    {
        var field = MakePrefectureSchools(1000, 8);
        var runner = TournamentRunner.Spectated(
            field, Coeff, new Xoshiro256Random(11), Schedule, "甲子園", isNationalTournament: true);

        Assert.False(runner.PlayerActive);
        Assert.True(runner.BracketResolutionPending);
        Assert.Equal(3, runner.RemainingBracketRounds);   // 8校＝3ラウンド

        runner.StepBracketResolution();                    // 1ラウンドだけ刻む
        Assert.True(runner.BracketResolutionPending);
        Assert.Null(runner.ChampionName);

        runner.DrainBracketResolution();
        Assert.False(runner.BracketResolutionPending);
        Assert.False(string.IsNullOrEmpty(runner.ChampionName));

        var view = runner.BuildBracketView();
        Assert.All(view.Matches, m => Assert.False(m.ManagerInvolved));
        Assert.False(view.ManagerIsChampion);
    }

    /// <summary>観るだけの大会も決定論（同シード同結果）。</summary>
    [Fact]
    public void Spectated_IsDeterministic()
    {
        static string Run(ulong seed)
        {
            var r = TournamentRunner.Spectated(
                MakePrefectureSchools(1000, 8), Coeff, new Xoshiro256Random(seed), Schedule, "甲子園");
            r.DrainBracketResolution();
            return r.ChampionName!;
        }
        Assert.Equal(Run(11), Run(11));
    }

    /// <summary>
    /// 代表校の勝ち上がりは<b>集計モデル（ChampionsOnly）でも</b>得られる。
    /// ここが空だと甲子園の県予選成績を作る材料が無くなる。
    /// </summary>
    [Fact]
    public void BeginSummer_ChampionsOnly_StillProducesChampionIdAndPath()
    {
        var nation = MakeMiniNation();
        var run = NationalTournamentEngine.BeginSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule,
            yearIndex: 1, new NationTournamentStats(), new Xoshiro256Random(42),
            resolution: SummerResolution.ChampionsOnly);

        foreach (var r in run.Jobs.Select(j => j()))
        {
            Assert.True(r.HasChampion);
            Assert.NotEmpty(r.Path);
        }
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
