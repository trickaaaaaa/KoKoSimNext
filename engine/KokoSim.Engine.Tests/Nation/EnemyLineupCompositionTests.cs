using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 敵校オーダーの適正化（issue #54）: StrengthTeamFactory への配線。能力ベースの打順編成は
/// 常時適用（展望と実戦の単一ソース）、DH使用判断は modernRules/calendarYear を渡したときだけ検討する
/// （既定 null＝従来挙動と完全一致）。
/// </summary>
public sealed class EnemyLineupCompositionTests
{
    private static readonly LineupCoefficients C = new();

    private static School Sch(int id, double strength = 70) => new()
    { Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength };

    [Fact]
    public void Create_WithoutModernRules_NeverUsesDh_RegressionOfDefaultBehavior()
    {
        foreach (var id in Enumerable.Range(1, 20))
        {
            var team = StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1);
            Assert.False(team.UsesDh);
            Assert.Equal(8, team.PitcherSlot);
            Assert.Equal(FieldPosition.Pitcher, team.BattingOrder[team.PitcherSlot].Position);
        }
    }

    [Fact]
    public void ForSchool_ModernRulesBeforeIntroYear_NeverUsesDh()
    {
        // DhIntroYear既定=2025。年代未到達なら投手がどれだけ非力でもDHは検討されない。
        foreach (var id in Enumerable.Range(1, 20))
        {
            var team = StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1,
                modernRules: new ModernRules(), calendarYear: 2020);
            Assert.False(team.UsesDh);
        }
    }

    [Fact]
    public void ForSchool_ModernRulesAfterIntroYear_MostTeamsUseDh()
    {
        // 投手打撃は strength-20 中心・野手は strength 中心で生成されるため、平均的には
        // 既定のgapしきい値(15)を超える。統計的な傾向として大半の校でDHが起動することを確認する
        // （安全マージンを大きくとり、稀な逆転個体差でテストが揺れないようにする）。
        var count = Enumerable.Range(1, 40)
            .Count(id => StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1,
                modernRules: new ModernRules(), calendarYear: 2026).UsesDh);

        Assert.True(count >= 25, $"DH起動校数が少なすぎる: {count}/40");
    }

    [Fact]
    public void ForSchool_DhTeam_BattingOrderHasNoPitcher_AndRosterCountStays20()
    {
        var dhSchool = Enumerable.Range(1, 40)
            .Select(id => (Id: id, Team: StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1,
                modernRules: new ModernRules(), calendarYear: 2026)))
            .First(x => x.Team.UsesDh);

        var team = dhSchool.Team;
        Assert.Equal(9, team.BattingOrder.Count);
        Assert.All(team.BattingOrder, p => Assert.Null(p.Pitching));
        Assert.NotNull(team.StartingPitcher);
        Assert.NotNull(team.StartingPitcher!.Pitching);
        Assert.Equal(-1, team.PitcherSlot);
        Assert.InRange(team.DhSlot, 0, 8);

        var all = team.BattingOrder.Concat(team.Bullpen).Concat(team.Bench).Append(team.StartingPitcher).ToList();
        Assert.Equal(20, all.Count);
        Assert.Equal(20, all.Select(p => p!.UniformNumber).Distinct().Count());
    }

    [Fact]
    public void ForSchool_IsDeterministic_BattingOrderAndDhSlot_SameAcrossCalls()
    {
        var dhSchoolId = Enumerable.Range(1, 40)
            .First(id => StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1,
                modernRules: new ModernRules(), calendarYear: 2026).UsesDh);

        var a = StrengthTeamFactory.ForSchool(Sch(dhSchoolId), yearIndex: 1,
            modernRules: new ModernRules(), calendarYear: 2026);
        var b = StrengthTeamFactory.ForSchool(Sch(dhSchoolId), yearIndex: 1,
            modernRules: new ModernRules(), calendarYear: 2026);

        Assert.Equal(a.BattingOrder.Select(p => p.Name), b.BattingOrder.Select(p => p.Name));
        Assert.Equal(a.DhSlot, b.DhSlot);
        Assert.Equal(a.UsesDh, b.UsesDh);
        Assert.Equal(a.StartingPitcher?.Name, b.StartingPitcher?.Name);
    }

    [Fact]
    public void Create_AbilityOrdering_LeadoffIsBestOnBaseAmongFielders()
    {
        double OnBase(Player p) => p.Discipline * C.LeadoffDisciplineWeight
            + p.Contact * C.LeadoffContactWeight + p.Speed * C.LeadoffSpeedWeight;

        foreach (var id in Enumerable.Range(1, 15))
        {
            var team = StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1);
            var fielders = team.BattingOrder.Take(8).ToList();
            var leadoffScore = OnBase(team.BattingOrder[0]);
            Assert.All(fielders, p => Assert.True(leadoffScore >= OnBase(p) - 1e-9));
        }
    }

    [Fact]
    public void Create_AbilityOrdering_HeartAndResidualSlots_AreNonIncreasingByOverallScore()
    {
        // 中軸(4→3→5番)・残り(6→7→8番)は打撃総合の高い順に「補充」される保証がある
        // （leadoff/2番が出塁力・小技で先取りすることはあっても、補充順そのものは崩れない）。
        foreach (var id in Enumerable.Range(1, 15))
        {
            var team = StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1);
            var pickOrder = new[] { 3, 2, 4, 5, 6, 7 };
            var scores = pickOrder.Select(i => LineupOrderer.BattingScore(team.BattingOrder[i], C)).ToList();
            for (var i = 1; i < scores.Count; i++)
            {
                Assert.True(scores[i - 1] >= scores[i] - 1e-9,
                    $"校{id}: 補充順の打撃総合が降順でない: {string.Join(", ", scores)}");
            }
        }
    }
}
