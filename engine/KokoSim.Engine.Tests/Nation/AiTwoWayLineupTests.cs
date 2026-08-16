using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の二刀流運用（issue ai-two-way-hitting-pitchers-start-as-fielders）。マウンド組に入った選手を野手8枠から
/// 完全に外していたため「打撃A/Bの2年投手4人が控えに座り、打撃E/F/Gの1年8人が先発する」打線が出ていた。
/// 打てる控え投手は守備位置／DHで先発させ、余った投手はベンチ外へ捨てず野手候補へ戻す。
/// </summary>
public sealed class AiTwoWayLineupTests
{
    private readonly ITestOutputHelper _out;
    public AiTwoWayLineupTests(ITestOutputHelper output) => _out = output;

    private static School MakeSchool(int id, double strength)
        => new() { Id = id, Name = $"架空{id}", PrefectureId = 13, Strength = strength, Fame = 45 };

    private static AiRosterDeps NoPhenomDeps() => new()
    {
        Persistent = new PersistentRosterCoefficients
        {
            Phenom = new PhenomCoefficients { SpikeRatePerSchoolYear = 0, AllRoundRatePerSchoolYear = 0 },
        },
    };

    private static double Batting(Player p) => (p.Contact + p.Power + p.Discipline) / 3.0;

    /// <summary>issue の実測ハーネス: Strength 30/45/60 × 200校・夏引退後の2学年で Team を組む。</summary>
    private static IEnumerable<(School School, Team Team, AiSchoolRoster Roster)> SampleTeams(AiRosterDeps deps, int perStrength = 200)
    {
        var id = 1;
        foreach (var strength in new[] { 30.0, 45.0, 60.0 })
            for (var i = 0; i < perStrength; i++, id++)
            {
                var school = MakeSchool(id, strength);
                var roster = AiRosterFactory.Bootstrap(school, 1, deps);
                AiRosterCycle.RetireGraduates(roster);
                yield return (school, AiTeamBuilder.Build(roster, school, 1, deps), roster);
            }
    }

    [Fact]
    public void StartingFielders_OutBatTheMoundGroup_OnAverage_AndSurplusPitchersAreNotThrownAway()
    {
        var deps = NoPhenomDeps();
        double starterSum = 0, moundSum = 0, benchSum = 0;
        int starterN = 0, moundN = 0, benchN = 0;
        var moundBetterThanWorstStarter = 0;
        var moundNonStarters = 0;
        var teams = 0;
        var benchOutTotal = 0;   // ベンチ外へ消えた在校生（20人枠を割った人数は数えない）

        foreach (var (school, team, roster) in SampleTeams(deps))
        {
            teams++;
            var starters = team.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher).ToList();
            var mound = new List<Player>();
            if (team.StartingPitcher is { } sp) mound.Add(sp);
            else mound.Add(team.BattingOrder[team.PitcherSlot]);
            var benchPitchers = team.Reserves.Where(p => p.Position == FieldPosition.Pitcher).ToList();
            var benchFielders = team.Reserves.Where(p => p.Position != FieldPosition.Pitcher).ToList();
            mound.AddRange(benchPitchers);

            starterSum += starters.Sum(Batting); starterN += starters.Count;
            moundSum += mound.Sum(Batting); moundN += mound.Count;
            benchSum += benchFielders.Sum(Batting); benchN += benchFielders.Count;

            var worstStarter = starters.Min(Batting);
            moundNonStarters += benchPitchers.Count;
            moundBetterThanWorstStarter += benchPitchers.Count(p => Batting(p) > worstStarter);

            var active = roster.Players.Count(p => p.Grade is >= 1 and <= 3);
            var benched = team.BattingOrder.Count + team.Reserves.Count + (team.StartingPitcher is null ? 0 : 1);
            benchOutTotal += System.Math.Max(0, System.Math.Min(active, 20) - benched);
        }

        var starterAvg = starterSum / starterN;
        var moundAvg = moundSum / moundN;
        var benchAvg = benchSum / benchN;
        var betterShare = (double)moundBetterThanWorstStarter / moundNonStarters;
        _out.WriteLine($"先発野手 {starterAvg:F1} / 投手組 {moundAvg:F1} / 控え野手 {benchAvg:F1} （平均打撃＝(ミート＋パワー＋選球眼)/3）");
        _out.WriteLine($"投手組（先発投手を除く）のうち最も打てない先発野手より打てる者: {betterShare:P0}（{moundBetterThanWorstStarter}/{moundNonStarters}）");
        _out.WriteLine($"ベンチ外の在校生: {benchOutTotal}（{teams}校。2学年22人−20人枠＝構造上 2/校 まで出る）");

        // 施策後の固定（2026-08-16 実測: 施策前 38.5/37.1/35.7・82%、施策後 42.0/35.8/34.3・42%）:
        // 先発野手の平均打撃 ≥ 投手組の平均打撃、"最も打てない先発より打てる控え投手" は少数派
        // （最も打てない先発はたいてい捕手・遊撃＝守備で選ぶ枠なので 0 にはならない）。
        Assert.True(starterAvg >= moundAvg + 3, $"starters {starterAvg:F1} < mound {moundAvg:F1}");
        Assert.True(betterShare < 0.5, $"share={betterShare:P0}");
    }

    [Fact]
    public void SurplusPitchers_ReturnToTheFielderPool_AndCanStartInTheField()
    {
        // マウンド組は先発＋控え投手3までに絞られ（背番号18〜20 は最大3人）、余りは野手候補へ戻る。
        // 打てる控え投手が守備位置で先発する校が実際に出る（二刀流運用）。
        var deps = NoPhenomDeps();
        var twoWaySchools = 0;
        var teams = 0;
        foreach (var (school, team, roster) in SampleTeams(deps))
        {
            teams++;
            var benchPitchers = team.Reserves.Where(p => p.Position == FieldPosition.Pitcher).ToList();
            Assert.True(benchPitchers.Count <= 3, $"校{school.Id}: 控え投手 {benchPitchers.Count}");
            Assert.All(benchPitchers, p => Assert.InRange(p.UniformNumber, 18, 20));
            // 生成時に投手寄りだった選手が控え野手に座るとき「投」ラベルを持ち越していない。
            Assert.DoesNotContain(team.Reserves, p => p.Position == FieldPosition.Pitcher && p.UniformNumber < 18);

            // 二刀流: 守備に就いた先発野手のうち、投手能力がベンチの控え投手より高い者が居る校を数える。
            var fieldStarters = team.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher);
            var bestBenchArm = benchPitchers.Count > 0 ? benchPitchers.Max(p => p.Pitching.ReliefScore()) : double.NegativeInfinity;
            if (fieldStarters.Any(p => p.Pitching.ReliefScore() > bestBenchArm)) twoWaySchools++;
        }
        _out.WriteLine($"二刀流（控え投手より投げられる野手先発が居る）校: {twoWaySchools}/{teams}");
        Assert.True(twoWaySchools > teams / 10, $"two-way schools={twoWaySchools}/{teams}");
    }

    [Fact]
    public void TwoWayFieldStarter_CanRelieveFromTheField_WithBackfill()
    {
        // 野手で先発した控え投手（マウンド組）が試合中に登板するとき、在フィールドからの投野交代
        // （TeamState.RelieveWith → PromoteFielderToMound）で守備穴が埋まり、9守備位置が保たれる。
        var deps = NoPhenomDeps();
        var checkedTeams = 0;
        foreach (var (school, team, _) in SampleTeams(deps))
        {
            var benchPitchers = team.Reserves.Where(p => p.Position == FieldPosition.Pitcher).ToList();
            var bestBenchArm = benchPitchers.Count > 0 ? benchPitchers.Max(p => p.Pitching.ReliefScore()) : double.NegativeInfinity;
            var twoWay = team.BattingOrder
                .Where(p => p.Position != FieldPosition.Pitcher && p.Pitching.ReliefScore() > bestBenchArm)
                .OrderByDescending(p => p.Pitching.ReliefScore()).FirstOrDefault();
            if (twoWay is null) continue;

            var state = new TeamState(team);
            var onField = state.ReliefRankingCandidates
                .First(q => q.Name == twoWay.Name && q.UniformNumber == twoWay.UniformNumber);
            Assert.True(state.RelieveWith(onField), $"校{school.Id}: 在フィールドの二刀流を上げられない");
            Assert.Equal(twoWay.Name, state.CurrentPitcher.Name);
            // 守備9枠が埋まっている（投野交代 or ベンチ補充のどちらでも）。
            var positions = state.CurrentLineup.Select(p => p.Position).ToHashSet();
            Assert.Equal(9, positions.Count);
            checkedTeams++;
        }
        Assert.True(checkedTeams > 50, $"checked={checkedTeams}");
    }

    [Fact]
    public void AllNinePositions_StillCovered_AndAceKeepsNumberOne()
    {
        var deps = NoPhenomDeps();
        foreach (var (school, team, _) in SampleTeams(deps, perStrength: 50))
        {
            var covered = team.BattingOrder.Select(p => p.Position).ToHashSet();
            Assert.Equal(9, covered.Count);
            var ace = team.BattingOrder[team.PitcherSlot];
            Assert.Equal(1, ace.UniformNumber);
            Assert.Equal(FieldPosition.Pitcher, ace.Position);
        }
    }

    [Fact]
    public void DhCandidates_IncludeBenchPitchers()
    {
        // DH制では控え全員（投手枠を含む）から最強打者を DH へ繰り上げる。打てる控え投手を DH に置いた校が出る。
        var deps = NoPhenomDeps();
        var rules = new ModernRules();
        var dhFromBullpen = 0;
        var dhTeams = 0;
        foreach (var (school, _, roster) in SampleTeams(deps, perStrength: 100))
        {
            var team = AiTeamBuilder.Build(roster, school, 1, deps, rules, calendarYear: 2026);
            if (!team.UsesDh) continue;
            dhTeams++;
            var dh = team.BattingOrder[team.DhSlot];
            if (dh.Position == FieldPosition.Pitcher) dhFromBullpen++;
        }
        _out.WriteLine($"DH採用 {dhTeams} 校中、DHが控え投手: {dhFromBullpen}");
        Assert.True(dhTeams > 0);
        Assert.True(dhFromBullpen > 0);
    }

    [Fact]
    public void SixFactorProfile_BeforeAfter_Report()
    {
        // 参考値（issue の完了条件「前後で TeamStrengthProfile の6指標平均を実測して報告する」）。
        var deps = NoPhenomDeps();
        var acc = new double[7];
        var n = 0;
        foreach (var (_, team, _) in SampleTeams(deps, perStrength: 100))
        {
            var s = ScoutedTeamProfile.Compute(team, deps.TeamStrength);
            acc[0] += s.Batting; acc[1] += s.Pitching; acc[2] += s.Defense; acc[3] += s.Mobility;
            acc[4] += s.Depth; acc[5] += s.Mental; acc[6] += s.Overall;
            n++;
        }
        _out.WriteLine($"6指標平均（{n}校）: 打撃 {acc[0] / n:F1} 投手 {acc[1] / n:F1} 守備 {acc[2] / n:F1} 機動 {acc[3] / n:F1} 選手層 {acc[4] / n:F1} 精神 {acc[5] / n:F1} 総合 {acc[6] / n:F1}");
    }
}
