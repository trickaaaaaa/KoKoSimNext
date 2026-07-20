using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 主将の選定と年度更新（設計書09 §8）。統率力=統率傾向×精神力/100、最上級生から選ぶ、
/// 3年生引退→選び直し、手動指名の尊重、Team への投影（在場判定の参照同一性）を検証する。
/// </summary>
public sealed class CaptainSelectorTests
{
    private static DevelopingPlayer Guy(string name, int grade, int leadership, int mental,
        bool pitcher = false, int level = 40) => new()
    {
        Name = name,
        Grade = grade,
        Leadership = leadership,
        Mental = mental,
        IsPitcher = pitcher,
    };

    private static DevelopingPlayer Regular(string name, int grade, int leadership, int mental, int contactLevel)
    {
        var p = Guy(name, grade, leadership, mental);
        // AverageLevel を押し上げてスタメン入りさせる（Build の能力順選抜）。
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, contactLevel); p.SetCap(k, 99); }
        return p;
    }

    [Fact]
    public void LeadershipPower_IsLeadershipTimesMentalOver100()
    {
        Assert.Equal(72.0, CaptainSelector.LeadershipPower(Guy("A", 3, 80, 90)), 6);
    }

    [Fact]
    public void SelectAuto_PicksHighestPower_ExclusiveFlag()
    {
        var roster = new List<DevelopingPlayer>
        {
            Guy("弱", 3, 40, 50),
            Guy("主", 3, 90, 80),  // 統率力 72
            Guy("中", 3, 70, 70),  // 統率力 49
        };
        var cap = CaptainSelector.SelectAuto(roster);
        Assert.Equal("主", cap!.Name);
        Assert.Single(roster, p => p.IsCaptain);
        Assert.True(roster.First(p => p.Name == "主").IsCaptain);
    }

    [Fact]
    public void SelectAuto_RestrictsToTopGrade()
    {
        var roster = new List<DevelopingPlayer>
        {
            Guy("怪物1年", 1, 99, 99),  // 統率力 98 だが下級生
            Guy("凡3年", 3, 55, 60),    // 統率力 33 でも最上級生
        };
        var cap = CaptainSelector.SelectAuto(roster);
        Assert.Equal("凡3年", cap!.Name);
    }

    [Fact]
    public void EnsureCaptain_KeepsExistingValidCaptain()
    {
        var roster = new List<DevelopingPlayer> { Guy("A", 3, 60, 60), Guy("B", 3, 90, 90) };
        CaptainSelector.Designate(roster, roster[0]); // 手動でAを指名（統率力は低い）
        var cap = CaptainSelector.EnsureCaptain(roster);
        Assert.Equal("A", cap!.Name); // 自動選定で上書きされない
    }

    [Fact]
    public void EnsureCaptain_ReselectsWhenCaptainGone()
    {
        var roster = new List<DevelopingPlayer> { Guy("旧主将", 3, 90, 90), Guy("後継", 2, 80, 80) };
        CaptainSelector.SelectAuto(roster); // 旧主将（3年）が主将
        // 年度更新: 3年引退を模して旧主将を除去。
        roster.RemoveAll(p => p.Name == "旧主将");
        var cap = CaptainSelector.EnsureCaptain(roster);
        Assert.Equal("後継", cap!.Name);
        Assert.Single(roster, p => p.IsCaptain);
    }

    [Fact]
    public void EnsureCaptain_NormalizesMultipleFlags()
    {
        var roster = new List<DevelopingPlayer> { Guy("A", 3, 60, 60), Guy("B", 3, 90, 90) };
        roster[0].IsCaptain = true;
        roster[1].IsCaptain = true; // 不整合（複数）
        CaptainSelector.EnsureCaptain(roster);
        Assert.Single(roster, p => p.IsCaptain); // 1名へ正規化（統率力最大のB）
        Assert.True(roster.First(p => p.Name == "B").IsCaptain);
    }

    // --- Team への投影（設計書09 §8: 在場×統率力の緩和） ---

    [Fact]
    public void Build_ProjectsCaptain_AsOnFieldInstance()
    {
        var roster = new List<DevelopingPlayer>
        {
            Regular("主将", 3, 90, 90, contactLevel: 70), // 能力高→スタメン
            Regular("二番", 3, 50, 50, contactLevel: 60),
            Regular("三番", 3, 50, 50, contactLevel: 55),
        };
        CaptainSelector.SelectAuto(roster);
        var team = RosterTeamBuilder.Build(roster, "テスト校");

        Assert.NotNull(team.Captain);
        Assert.Equal("主将", team.Captain!.Name);
        // 在場判定は参照同一性: 打順の中の同一インスタンスを差していること。
        Assert.Contains(team.BattingOrder, pl => ReferenceEquals(pl, team.Captain));
    }

    [Fact]
    public void Build_NoCaptain_LeavesCaptainNull()
    {
        var roster = new List<DevelopingPlayer> { Regular("A", 3, 50, 50, 60) };
        var team = RosterTeamBuilder.Build(roster, "無主将校");
        Assert.Null(team.Captain);
    }
}
