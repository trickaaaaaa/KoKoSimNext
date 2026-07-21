using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 在籍ライフサイクル（設計書03 §2 / 09 §8）。夏の第17週で3年が引退するが、ロスターからは
/// 除去せず引退フラグを立てて残す。引退者は練習・試合・選手一覧（＝Active）から外れ、
/// 主将にもなれない。新チーム発足で暫定主将が最上級生から立つことを検証する。
/// </summary>
public sealed class RosterLifecycleTests
{
    private static DevelopingPlayer Guy(string name, int grade, int leadership = 50, int mental = 50)
        => new() { Name = name, Grade = grade, Leadership = leadership, Mental = mental };

    private static List<DevelopingPlayer> Roster() => new()
    {
        Guy("三年主将", 3, 90, 90),
        Guy("三年控え", 3, 40, 50),
        Guy("二年A", 2, 80, 70),   // 統率力 56
        Guy("二年B", 2, 60, 60),   // 統率力 36
        Guy("一年", 1, 99, 99),
    };

    [Fact]
    public void RetireThirdYears_FlagsThirdYears_KeepsThemInRoster()
    {
        var roster = Roster();
        var retired = RosterLifecycle.RetireThirdYears(roster);

        Assert.Equal(new[] { "三年主将", "三年控え" }, retired.Select(p => p.Name));
        Assert.Equal(5, roster.Count);                                  // 除去せず残す
        Assert.All(roster.Where(p => p.Grade >= 3), p => Assert.True(p.IsRetired));
        Assert.All(roster.Where(p => p.Grade < 3), p => Assert.False(p.IsRetired));
    }

    [Fact]
    public void RetireThirdYears_IsIdempotent()
    {
        var roster = Roster();
        RosterLifecycle.RetireThirdYears(roster);
        var second = RosterLifecycle.RetireThirdYears(roster);

        Assert.Empty(second);                                           // 2度目は新規引退なし
        Assert.Equal(2, RosterLifecycle.Retired(roster).Count);
    }

    [Fact]
    public void Active_ExcludesRetired_PreservesOrder()
    {
        var roster = Roster();
        RosterLifecycle.RetireThirdYears(roster);

        Assert.Equal(new[] { "二年A", "二年B", "一年" },
            RosterLifecycle.Active(roster).Select(p => p.Name));
    }

    [Fact]
    public void BeginNewTeam_RetiresThirdYears_AndPicksInterimCaptainFromNewTopGrade()
    {
        var roster = Roster();
        CaptainSelector.EnsureCaptain(roster);
        Assert.Equal("三年主将", CaptainSelector.Current(roster)!.Name);

        var t = RosterLifecycle.BeginNewTeam(roster);

        Assert.Equal(2, t.Retired.Count);
        Assert.Equal("二年A", t.InterimCaptain!.Name);                  // 新最上級生（新3年）の統率力最大
        Assert.Single(roster, p => p.IsCaptain);                        // 主将はロスター全体で1名
        Assert.False(roster.First(p => p.Name == "三年主将").IsCaptain); // 引退者の主将フラグは外れる
    }

    [Fact]
    public void OnWeekEntered_FiresOnlyOnNewTeamStartWeek()
    {
        var calendar = new SeasonCalendar();
        var roster = Roster();

        Assert.Null(RosterLifecycle.OnWeekEntered(roster, calendar.SummerRetireWeek, calendar));
        Assert.Empty(RosterLifecycle.Retired(roster));                  // 引退週当日はまだ在籍（練習可）

        var t = RosterLifecycle.OnWeekEntered(roster, calendar.SummerRetireWeek + 1, calendar);
        Assert.NotNull(t);
        Assert.Equal(2, t!.Retired.Count);
    }

    [Fact]
    public void EnsureCaptain_NeverPicksRetiredPlayer()
    {
        var roster = Roster();
        RosterLifecycle.RetireThirdYears(roster);
        foreach (var p in roster) p.IsCaptain = false;

        var cap = CaptainSelector.EnsureCaptain(roster);

        Assert.Equal("二年A", cap!.Name);
        Assert.DoesNotContain(RosterLifecycle.Retired(roster), p => p.IsCaptain);
    }

    [Fact]
    public void EnsureCaptain_DropsCaptaincyOfRetiredAndReselects()
    {
        var roster = Roster();
        CaptainSelector.EnsureCaptain(roster);          // 三年主将
        RosterLifecycle.RetireThirdYears(roster);       // 引退フックを通さずフラグだけ立った状態

        var cap = CaptainSelector.EnsureCaptain(roster);

        Assert.Equal("二年A", cap!.Name);
        Assert.Single(roster, p => p.IsCaptain);
    }

    [Fact]
    public void DesignationCandidates_ExcludeRetired_AndAreNewTopGrade()
    {
        var roster = Roster();
        RosterLifecycle.BeginNewTeam(roster);

        Assert.Equal(new[] { "二年A", "二年B" },
            CaptainSelector.DesignationCandidates(roster).Select(p => p.Name));
    }

    [Fact]
    public void ManualDesignation_SurvivesEnsureCaptain()
    {
        var roster = Roster();
        var calendar = new SeasonCalendar();
        RosterLifecycle.BeginNewTeam(roster);

        var pick = roster.First(p => p.Name == "二年B");
        Assert.True(CaptainSelector.CanDesignate(roster, pick, calendar.SummerRetireWeek + 1, calendar));
        CaptainSelector.Designate(roster, pick);

        CaptainSelector.EnsureCaptain(roster);          // 自動補充は手動指名を上書きしない
        Assert.Equal("二年B", CaptainSelector.Current(roster)!.Name);
    }
}
