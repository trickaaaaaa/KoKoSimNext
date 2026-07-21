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

    // --- 新チーム発足と指名ウィンドウ（設計書09 §8） ---

    [Fact]
    public void IsDesignationWindow_OpensAfterRetireWeek_ClosesAtAutumnTournament()
    {
        var cal = new SeasonCalendar();   // 引退=17週 / 秋季県大会=23週
        Assert.False(CaptainSelector.IsDesignationWindow(cal.SummerRetireWeek, cal));       // 引退当週はまだ旧チーム
        Assert.True(CaptainSelector.IsDesignationWindow(cal.SummerRetireWeek + 1, cal));    // 新チーム初週
        Assert.True(CaptainSelector.IsDesignationWindow(cal.AutumnTournamentStartWeek - 1, cal));
        Assert.False(CaptainSelector.IsDesignationWindow(cal.AutumnTournamentStartWeek, cal)); // 初公式戦以降は不可
        Assert.False(CaptainSelector.IsDesignationWindow(0, cal));                          // 年度頭は不可
    }

    [Fact]
    public void IsNewTeamStartWeek_IsTheWeekAfterRetirement()
    {
        var cal = new SeasonCalendar();
        Assert.True(CaptainSelector.IsNewTeamStartWeek(cal.SummerRetireWeek + 1, cal));
        Assert.False(CaptainSelector.IsNewTeamStartWeek(cal.SummerRetireWeek, cal));
        // 引退の翌週＝夏合宿（新チーム結成直後）と一致していること。
        Assert.Equal(cal.SummerCampWeek, cal.SummerRetireWeek + 1);
    }

    [Fact]
    public void BeginNewTeam_MovesCaptainToNewTopGrade_AndDropsRetiredCaptain()
    {
        var roster = new List<DevelopingPlayer>
        {
            Guy("引退主将", 3, 95, 95),
            Guy("新主将", 2, 80, 80),   // 統率力 64
            Guy("新副将", 2, 60, 60),   // 統率力 36
            Guy("1年", 1, 99, 99),      // 下級生は対象外
        };
        CaptainSelector.SelectAuto(roster);
        Assert.True(roster[0].IsCaptain);

        var cap = CaptainSelector.BeginNewTeam(roster);
        Assert.Equal("新主将", cap!.Name);
        Assert.False(roster[0].IsCaptain);            // 引退3年のフラグは残らない
        Assert.Single(roster, p => p.IsCaptain);      // 0名・複数名にならない
    }

    [Fact]
    public void DesignationCandidates_AreNewTopGradeOnly()
    {
        var roster = new List<DevelopingPlayer>
        {
            Guy("引退3年", 3, 95, 95),
            Guy("新3年A", 2, 80, 80),
            Guy("新3年B", 2, 60, 60),
            Guy("新2年", 1, 99, 99),
        };
        var names = CaptainSelector.DesignationCandidates(roster).Select(p => p.Name).ToList();
        Assert.Equal(new[] { "新3年A", "新3年B" }, names);
    }

    [Fact]
    public void CanDesignate_RequiresWindowAndTopGrade()
    {
        var cal = new SeasonCalendar();
        var roster = new List<DevelopingPlayer>
        {
            Guy("引退3年", 3, 95, 95),
            Guy("新3年", 2, 60, 60),
            Guy("新2年", 1, 99, 99),
        };
        var inWindow = cal.SummerRetireWeek + 1;
        Assert.True(CaptainSelector.CanDesignate(roster, roster[1], inWindow, cal));
        Assert.False(CaptainSelector.CanDesignate(roster, roster[2], inWindow, cal));   // 下級生は不可
        Assert.False(CaptainSelector.CanDesignate(roster, roster[0], inWindow, cal));   // 引退3年は不可
        Assert.False(CaptainSelector.CanDesignate(roster, roster[1], 0, cal));          // 期間外は不可
    }

    [Fact]
    public void ManualDesignation_InWindow_SurvivesEnsureCaptain()
    {
        var roster = new List<DevelopingPlayer>
        {
            Guy("引退主将", 3, 95, 95),
            Guy("自動候補", 2, 80, 80),
            Guy("指名選手", 2, 50, 50),
        };
        CaptainSelector.BeginNewTeam(roster);                 // 暫定＝自動候補
        CaptainSelector.Designate(roster, roster[2]);         // プレイヤーが指名選手へ差し替え

        CaptainSelector.EnsureCaptain(roster);                // 以降の自動補充で上書きされない
        Assert.True(roster[2].IsCaptain);
        Assert.Single(roster, p => p.IsCaptain);
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
