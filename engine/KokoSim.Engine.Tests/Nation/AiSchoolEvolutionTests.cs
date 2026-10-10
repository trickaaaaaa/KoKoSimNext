using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Stats;
using KokoSim.Engine.Tests.Balance;
using KokoSim.Engine.Tests.Saves;
using KokoSim.Config;
using Xunit;
using Xunit.Abstractions;
using NationModel = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の年次進化を実ゲームへ配線するための engine 部品（issue wire-ai-school-evolve-into-game）。
/// 台帳（<see cref="AiSchoolYearLedger"/>）・年度末適用（<see cref="AiSchoolEvolution.ApplyYear"/>）・
/// 保存往復（<see cref="SchoolEvolutionState"/>）・専用ストリームで他の抽選が動かないこと、を固定する。
/// </summary>
public sealed class AiSchoolEvolutionTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();
    private readonly ITestOutputHelper _out;
    public AiSchoolEvolutionTests(ITestOutputHelper output) => _out = output;

    private static NationModel MakeMiniNation(int prefs = 3, int perPref = 8)
    {
        var ps = new List<Prefecture>();
        var schools = new List<School>();
        var id = 0;
        for (var p = 1; p <= prefs; p++)
        {
            ps.Add(new Prefecture(p, $"県{p}", perPref));
            for (var s = 0; s < perPref; s++)
                schools.Add(new School
                {
                    Id = id++, Name = $"県{p}校{s}", PrefectureId = p,
                    Strength = 40 + (s * 29) % 40, Fame = 45,
                });
        }
        return new NationModel(ps, schools);
    }

    [Fact]
    public void Ledger_CountsWinnersFromBracketMatches_AndPrefectureResults()
    {
        var ledger = new AiSchoolYearLedger();
        ledger.AddMatches(new[]
        {
            new BracketMatch("1回戦", 3, "A", "B", 1, 2, 5, 3, false),
            new BracketMatch("1回戦", 3, "C", "D", 3, 4, 2, 1, false),
            new BracketMatch("決勝", 1, "A", "C", 1, 3, 4, 2, false),
        });
        ledger.AddWins(new Dictionary<int, int> { [1] = 1, [9] = 4 });

        Assert.Equal(3, ledger.WinsOf(1));
        Assert.Equal(1, ledger.WinsOf(3));
        Assert.Equal(4, ledger.WinsOf(9));
        Assert.Equal(0, ledger.WinsOf(2));
        Assert.Equal(3, ledger.Count);

        ledger.Clear();
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void PrefectureResult_CarriesWinsBySchool_EvenInChampionsOnlyResolution()
    {
        var nation = MakeMiniNation(prefs: 1);
        var results = NationalTournamentEngine.RunSummer(
            nation, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff, Schedule, yearIndex: 1,
            new NationTournamentStats(), new Xoshiro256Random(7), resolution: SummerResolution.ChampionsOnly);

        var r = Assert.Single(results);
        // 8校のノックアウト＝7試合＝勝数の合計は7。優勝校は3勝。
        Assert.Equal(7, r.Wins.Values.Sum());
        var champ = nation.Schools.Single(s => s.Name == r.ChampionName);
        Assert.Equal(3, r.Wins[champ.Id]);
    }

    [Fact]
    public void ApplyYear_IsDeterministicForSameSeedAndYear_AndDiffersAcrossYears()
    {
        var wins = new Dictionary<int, int> { [0] = 3, [5] = 2 };
        var a = MakeMiniNation();
        var b = MakeMiniNation();
        var c = MakeMiniNation();
        AiSchoolEvolution.ApplyYear(a, wins, -1, Coeff, masterSeed: 99, endedYearIndex: 1);
        AiSchoolEvolution.ApplyYear(b, wins, -1, Coeff, masterSeed: 99, endedYearIndex: 1);
        AiSchoolEvolution.ApplyYear(c, wins, -1, Coeff, masterSeed: 99, endedYearIndex: 2);

        Assert.Equal(a.Schools.Select(s => s.Strength), b.Schools.Select(s => s.Strength));
        Assert.NotEqual(a.Schools.Select(s => s.Strength), c.Schools.Select(s => s.Strength));
        // 勝った校は名声が上がる（減衰0.9＋2.5×勝）。
        Assert.True(a.Schools[0].Fame > 45 * Coeff.FameDecay + 1);
    }

    [Fact]
    public void ApplyYear_DoesNotTouchAnyOtherRandomStream()
    {
        // 進化は母種×年度から導く専用ストリーム＝呼び出し側の乱数源を渡さないので構造的に不変。
        // ここでは「同じ母種で進化を挟んでも、別の抽選（全国裏試合）の結果が変わらない」ことで固定する。
        var n1 = MakeMiniNation();
        var n2 = MakeMiniNation();
        AiSchoolEvolution.ApplyYear(n2, new Dictionary<int, int>(), -1, Coeff, 5, 1);
        // 進化後の Strength を n1 へ写して同じ入力にする（進化そのものの効果を除いて比べる）。
        AiSchoolEvolution.Restore(n1, AiSchoolEvolution.Snapshot(n2));

        var r1 = NationalTournamentEngine.RunSummer(n1, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff,
            Schedule, 2, new NationTournamentStats(), new Xoshiro256Random(11), resolution: SummerResolution.ChampionsOnly);
        var r2 = NationalTournamentEngine.RunSummer(n2, new NationRosters(new AiRosterDeps()), new GameContext(), Coeff,
            Schedule, 2, new NationTournamentStats(), new Xoshiro256Random(11), resolution: SummerResolution.ChampionsOnly);
        Assert.Equal(r1.Select(r => r.ChampionName), r2.Select(r => r.ChampionName));
    }

    [Fact]
    public void Snapshot_Restore_RoundTripsStrengthAndFame_OntoRegeneratedSchools()
    {
        var evolved = MakeMiniNation();
        AiSchoolEvolution.ApplyYear(evolved, new Dictionary<int, int> { [3] = 4 }, -1, Coeff, 1, 1);
        var snap = AiSchoolEvolution.Snapshot(evolved);

        var regenerated = MakeMiniNation();   // 生成値のまま（別インスタンス）
        Assert.NotEqual(evolved.Schools[3].Strength, regenerated.Schools[3].Strength);
        var applied = AiSchoolEvolution.Restore(regenerated, snap);

        Assert.Equal(evolved.Schools.Count, applied);
        for (var i = 0; i < evolved.Schools.Count; i++)
        {
            Assert.Equal(evolved.Schools[i].Strength, regenerated.Schools[i].Strength);
            Assert.Equal(evolved.Schools[i].Fame, regenerated.Schools[i].Fame);
        }
    }

    [Fact]
    public void RosterGrowthTarget_FollowsEvolvedStrength()
    {
        // 永続ロスターは Strength 主・成長従属（#80）。進化で Strength が動けば、年度替わりの逆算配分の
        // 目標が動く＝同じ School インスタンスを進化させれば AdvanceExistingYear がそれを読む。
        // 同じロスター（年度1でブートストラップ）を2本用意し、片方だけ Strength を上げてから年度替わりする。
        var deps = new AiRosterDeps();
        var flat = MakeMiniNation(prefs: 1, perPref: 2).Schools[0];
        var evolved = MakeMiniNation(prefs: 1, perPref: 2).Schools[0];
        var rFlat = new NationRosters(deps);
        var rEvolved = new NationRosters(deps);
        rFlat.GetOrBootstrap(flat, 1);
        rEvolved.GetOrBootstrap(evolved, 1);

        evolved.Strength = 95;   // 進化で大きく強くなった校
        rFlat.AdvanceExistingYear(id => flat, 2);
        rEvolved.AdvanceExistingYear(id => evolved, 2);

        var before = TeamOverallOf(rFlat.TeamFor(flat, 2));
        var after = TeamOverallOf(rEvolved.TeamFor(evolved, 2));
        Assert.True(after > before, $"flat={before:F1} evolved={after:F1}");
    }

    private static double TeamOverallOf(Team team)
        => team.BattingOrder.Average(p => (p.Contact + p.Power + p.Fielding) / 3.0)
         + team.BattingOrder.Average(p => p.Pitching.MaxVelocityKmh);

    [Fact]
    public void SaveCodec_RoundTripsNationSchoolsAndYearWins()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.NationSchools = new List<SchoolEvolutionState>
        {
            new(5, 61.25, 48.5), new(2, 39.0, 12.75), new(9, 88.5, 95.0),
        };
        state.NationYearWins = new Dictionary<int, int> { [5] = 3, [9] = 6 };

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        // 校ID順で戻る。
        Assert.Equal(new[] { 2, 5, 9 }, s.NationSchools.Select(x => x.SchoolId));
        Assert.Equal(61.25, s.NationSchools[1].Strength);
        Assert.Equal(48.5, s.NationSchools[1].Fame);
        Assert.Equal(3, s.NationYearWins[5]);
        Assert.Equal(6, s.NationYearWins[9]);
    }

    [Fact]
    public void V12Save_WithoutNationSchools_StillLoads_AsEmpty()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"nationSchools\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = json.IndexOf(",\"sessionYear\":", System.StringComparison.Ordinal);
        Assert.True(end > start);
        var v12 = json.Remove(start, end - start)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":12");
        Assert.DoesNotContain("\"nationSchools\"", v12);
        Assert.DoesNotContain("\"nationYearWins\"", v12);

        Assert.True(SaveCodec.TryDeserialize(v12, out var s, out var err), err);
        Assert.Empty(s.NationSchools);
        Assert.Empty(s.NationYearWins);
        Assert.Equal(2, s.SessionYear);
    }

    /// <summary>
    /// 実ゲームと同じ部品（他県＝ChampionsOnly 裏試合→台帳→年度末に ApplyYear）で全国4000校を20年回し、
    /// ①県の覇者が入れ替わる ②県1位の強さの定常が 70 前後 ③全国の平均・sd がドリフトしない、を数値で確認する。
    /// 帯4本は大会・生成を測らないので、この実測が安全網（issue 完了条件）。
    /// </summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void TwentyYears_PowerMapChurns_AndNationalStatsStayStationary()
    {
        var nation = NationGenerator.Generate(new SchoolNameVocab(), Coeff, new Xoshiro256Random(2026));
        var rosters = new NationRosters(new AiRosterDeps());
        const int miyazaki = 44; // JIS 45 → 0基点
        var champions = new List<string>();
        var topStrengths = new List<double>();
        var mean0 = nation.Schools.Average(s => s.Strength);
        var sd0 = Sd(nation.Schools.Select(s => s.Strength));

        for (var year = 1; year <= 20; year++)
        {
            var ledger = new AiSchoolYearLedger();
            var results = NationalTournamentEngine.RunSummer(
                nation, rosters, new GameContext(), Coeff, Schedule, year, new NationTournamentStats(),
                new Xoshiro256Random(0xABCDUL ^ (ulong)year), resolution: SummerResolution.ChampionsOnly);
            foreach (var r in results) ledger.AddWins(r.Wins);
            champions.Add(results.Single(r => r.PrefectureId == miyazaki).ChampionName ?? "");
            topStrengths.Add(nation.InPrefecture(miyazaki).Max(s => s.Strength));

            AiSchoolEvolution.ApplyYear(nation, ledger.Snapshot(), -1, Coeff, masterSeed: 2026, endedYearIndex: year);
        }

        var meanN = nation.Schools.Average(s => s.Strength);
        var sdN = Sd(nation.Schools.Select(s => s.Strength));
        _out.WriteLine($"宮崎の夏の優勝校（20年）: {string.Join(" / ", champions)}");
        _out.WriteLine($"宮崎1位の強さ: {string.Join(", ", topStrengths.Select(t => t.ToString("F1")))}");
        _out.WriteLine($"全国 平均/sd: 初年 {mean0:F2}/{sd0:F2} → 20年後 {meanN:F2}/{sdN:F2}");

        Assert.True(champions.Distinct().Count() >= 3, "20年で宮崎の覇者が入れ替わらない: " + string.Join(",", champions));
        Assert.InRange(topStrengths.Skip(10).Average(), 60, 80);
        Assert.InRange(meanN, mean0 - 3, mean0 + 3);
        Assert.InRange(sdN, sd0 - 3, sd0 + 3);
    }

    /// <summary>
    /// 県内の寡占の帯（issue prefecture-dominance・<c>balance-targets.yaml nation_dominance</c>）。実ゲームの他県裏試合と同じ部品
    /// （ChampionsOnly＋ApplyYear）で全国4000校を20年×3母種回し、49地区の「最多優勝校の優勝回数」の分布と、初期の県1位 vs 2位の
    /// 単試合勝率を固定する。校正前（膝60・rev 0.10・churn 5.5・per_win 0.12・古豪+8）は中央値 8〜9／10回以上 15〜17地区／
    /// 最大 19〜20（20年全部）／1位vs2位 ≈0.85 で「同じ校が毎年優勝」が帯が全部緑のまま起きていた。
    /// </summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void TwentyYears_PrefectureDominance_StaysWithinBands()
    {
        var targets = BalanceTargetsLoader.LoadNationDominanceFromFile(
            BalanceRegressionTests.FindDataFile("balance-targets.yaml"));

        var medians = new List<double>();
        var tenPlus = new List<int>();
        var maxes = new List<int>();
        var topTwo = new List<double>();
        foreach (var seed in new ulong[] { 2026, 7, 42 })
        {
            var nation = NationGenerator.Generate(new SchoolNameVocab(), Coeff, new Xoshiro256Random(seed));
            var rosters = new NationRosters(new AiRosterDeps());
            var champions = new Dictionary<string, List<string>>();

            // 初期の県1位 vs 2位の単試合勝率（集計モデル＝圧縮後の差で引く）。
            foreach (var region in SummerRegions.Build(nation.Prefectures))
            {
                var top = SummerRegions.Entrants(nation, region).OrderByDescending(s => s.Strength).Take(2).ToList();
                if (top.Count == 2) topTwo.Add(AggregateMatch.WinProbability(top[0].Strength, top[1].Strength, Coeff));
            }

            for (var year = 1; year <= 20; year++)
            {
                var ledger = new AiSchoolYearLedger();
                var results = NationalTournamentEngine.RunSummer(
                    nation, rosters, new GameContext(), Coeff, Schedule, year, new NationTournamentStats(),
                    new Xoshiro256Random(0xABCDUL ^ seed ^ ((ulong)year << 8)), resolution: SummerResolution.ChampionsOnly);
                foreach (var r in results)
                {
                    ledger.AddWins(r.Wins);
                    if (!champions.TryGetValue(r.PrefectureName, out var list)) champions[r.PrefectureName] = list = new List<string>();
                    list.Add(r.ChampionName ?? "");
                }
                AiSchoolEvolution.ApplyYear(nation, ledger.Snapshot(), -1, Coeff, masterSeed: seed, endedYearIndex: year);
            }

            var maxCounts = champions.Values
                .Select(c => c.GroupBy(x => x).Max(g => g.Count()))
                .OrderBy(x => x).ToList();
            medians.Add(maxCounts[maxCounts.Count / 2]);
            tenPlus.Add(maxCounts.Count(x => x >= 10));
            maxes.Add(maxCounts.Max());
            _out.WriteLine($"seed {seed}: 最多優勝校の回数 中央値 {maxCounts[maxCounts.Count / 2]} / 平均 {maxCounts.Average():F1}"
                + $" / 最大 {maxCounts.Max()} / 10回以上 {maxCounts.Count(x => x >= 10)}地区 / 5回以下 {maxCounts.Count(x => x <= 5)}地区");
        }
        _out.WriteLine($"初期 県1位vs2位 勝率（49地区×3母種 平均）: {topTwo.Average():F3}");

        var failures = new List<string>();
        var median = medians.OrderBy(x => x).ElementAt(medians.Count / 2);
        if (!targets.MaxChampionCountMedian.Contains(median)) failures.Add($"中央値 {median}");
        if (!targets.RegionsWithTenPlus.Contains(tenPlus.Max())) failures.Add($"10回以上の地区 {tenPlus.Max()}");
        if (!targets.MaxChampionCountMax.Contains(maxes.Max())) failures.Add($"最大 {maxes.Max()}");
        if (!targets.TopTwoWinProbabilityMean.Contains(topTwo.Average())) failures.Add($"1位vs2位 {topTwo.Average():F3}");
        Assert.True(failures.Count == 0, "県内の寡占が帯外: " + string.Join(" / ", failures));
    }

    private static double Sd(IEnumerable<double> xs)
    {
        var list = xs.ToList();
        var m = list.Average();
        return System.Math.Sqrt(list.Sum(x => (x - m) * (x - m)) / list.Count);
    }
}
