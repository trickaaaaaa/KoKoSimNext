using System.Linq;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// スカウトの節目イベント（分野選び・候補リスト到着・締切前の念押し）を出すかの判定（issue #1035）。
/// 未契約の年は出ない／枠を使い切って訪問も済んでいれば念押しは出ない、を固定する。
/// </summary>
public sealed class ScoutMilestonesTests
{
    private static readonly SeasonCalendar Cal = new();
    private const int Year = 2;   // 今年度。リストの合流年度は Year + 1

    private static ScoutingState List(int slots, int targeted, int visitsEach, bool resolved = false, int year = Year + 1)
    {
        var s = new ScoutingState { YearIndex = year, Slots = slots, Resolved = resolved };
        for (var i = 0; i < slots * 3; i++)
            s.Candidates.Add(new ScoutCandidate
            {
                Index = i,
                Targeted = i < targeted,
                Visits = i < targeted ? visitsEach : 0,
            });
        return s;
    }

    // ── 分野選び（新チーム発足のあと） ─────────────────────────

    /// <summary>契約の有無に依らず出す（未契約の学校にも「契約しませんか」と聞く・issue #1064）。</summary>
    [Fact]
    public void ChooseField_NoListYet_FiresRegardlessOfContract()
        => Assert.Equal(ScoutMilestone.ChooseField,
            ScoutMilestones.AfterNewTeamStarted(Cal, week: 18, Year, list: null, hasEverContracted: true));

    /// <summary>前年度のリスト（合流年度＝今年度）が残っていても、今年ぶんは未生成＝分野は選べる。</summary>
    [Fact]
    public void ChooseField_LastYearsListStillStored_StillFires()
        => Assert.Equal(ScoutMilestone.ChooseField,
            ScoutMilestones.AfterNewTeamStarted(Cal, week: 18, Year, list: List(2, 1, 1, year: Year), hasEverContracted: true));

    [Fact]
    public void ChooseField_ListAlreadyBuiltForThisYear_DoesNotFire()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AfterNewTeamStarted(Cal, week: 18, Year, list: List(2, 0, 0), hasEverContracted: true));

    [Fact]
    public void ChooseField_AfterListWeek_DoesNotFire()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AfterNewTeamStarted(Cal, Cal.RecruitListWeek, Year, list: null, hasEverContracted: true));

    /// <summary>一度も契約したことのない学校には入口を出さない（issue #1594）。契約歴があれば段0でも出す。</summary>
    [Fact]
    public void ChooseField_NeverContracted_DoesNotFire()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AfterNewTeamStarted(Cal, week: 18, Year, list: null, hasEverContracted: false));

    [Fact]
    public void ChooseField_ContractedBeforeButLevelZeroNow_StillFires()
    {
        var set = new KokoSim.Engine.Career.ContractSet();
        set.SetLevel("scouting", 2);
        set.SetLevel("scouting", 0);
        Assert.Equal(ScoutMilestone.ChooseField,
            ScoutMilestones.AfterNewTeamStarted(Cal, week: 18, Year, list: null, set.HasEverContracted("scouting")));
    }

    [Fact]
    public void CanContractNow_OnlyWhenUncontractedAndBeforeListWeek()
    {
        Assert.True(ScoutMilestones.CanContractNow(Cal, week: 5, slots: 0));
        Assert.True(ScoutMilestones.CanContractNow(Cal, Cal.RecruitListWeek - 1, slots: 0));
        Assert.False(ScoutMilestones.CanContractNow(Cal, Cal.RecruitListWeek, slots: 0));   // 年額だけ払って何も得られない
        Assert.False(ScoutMilestones.CanContractNow(Cal, week: 18, slots: 2));               // 契約中
    }

    [Fact]
    public void ContractHint_OnlyNeverContracted_AtSummerStartAndLastWeekBeforeList()
    {
        Assert.True(ScoutMilestones.ShowContractHint(Cal, Cal.SummerTournamentStartWeek, slots: 0, hasEverContracted: false));
        Assert.True(ScoutMilestones.ShowContractHint(Cal, Cal.RecruitListWeek - 1, slots: 0, hasEverContracted: false));
        Assert.False(ScoutMilestones.ShowContractHint(Cal, Cal.SummerTournamentStartWeek + 1, slots: 0, hasEverContracted: false));
        Assert.False(ScoutMilestones.ShowContractHint(Cal, Cal.SummerTournamentStartWeek - 1, slots: 0, hasEverContracted: false));
        Assert.False(ScoutMilestones.ShowContractHint(Cal, Cal.RecruitListWeek, slots: 0, hasEverContracted: false));
        Assert.False(ScoutMilestones.ShowContractHint(Cal, week: 18, slots: 0, hasEverContracted: true));
        Assert.False(ScoutMilestones.ShowContractHint(Cal, week: 18, slots: 2, hasEverContracted: false));
    }

    [Fact]
    public void OpenerEventId_SeparatesActiveLapsedAndFirstContact()
    {
        Assert.Equal(ScoutMilestones.ChooseFieldEventId, ScoutPitch.OpenerEventId(level: 1, everContracted: true));
        Assert.Equal(ScoutPitch.OfferEventId, ScoutPitch.OpenerEventId(level: 0, everContracted: true));
        Assert.Equal(ScoutPitch.FirstOfferEventId, ScoutPitch.OpenerEventId(level: 0, everContracted: false));
        Assert.Equal(ScoutPitch.DeclinedEventId, ScoutPitch.DeclinedEventIdFor(true));
        Assert.Equal(ScoutPitch.FirstDeclinedEventId, ScoutPitch.DeclinedEventIdFor(false));
    }

    [Fact]
    public void CanChooseField_FreezesOnlyWhenThisYearsListExists()
    {
        Assert.True(ScoutMilestones.CanChooseField(null, Year));
        Assert.True(ScoutMilestones.CanChooseField(List(2, 0, 0, year: Year), Year));
        Assert.False(ScoutMilestones.CanChooseField(List(2, 0, 0), Year));
    }

    // ── 候補リストを待つ時期（スカウト画面の出し分け・issue #1064） ─────────

    [Fact]
    public void AwaitingList_ContractedBetweenSummerAndListWeek_IsTrue()
    {
        foreach (var w in Enumerable.Range(Cal.SummerTournamentStartWeek, Cal.RecruitListWeek - Cal.SummerTournamentStartWeek))
            Assert.True(ScoutMilestones.AwaitingList(Cal, w, Year, slots: 2, list: null));
    }

    [Fact]
    public void AwaitingList_Uncontracted_IsFalse()
        => Assert.False(ScoutMilestones.AwaitingList(Cal, 18, Year, slots: 0, list: null));

    [Fact]
    public void AwaitingList_OutsideTheWindow_IsFalse()
    {
        Assert.False(ScoutMilestones.AwaitingList(Cal, Cal.SummerTournamentStartWeek - 1, Year, slots: 2, list: null));
        Assert.False(ScoutMilestones.AwaitingList(Cal, Cal.RecruitListWeek, Year, slots: 2, list: null));
    }

    /// <summary>前年度のリストが残っているだけなら待ち＝今年ぶんはまだ来ていない。</summary>
    [Fact]
    public void AwaitingList_LastYearsListOnly_IsTrue_ThisYearsList_IsFalse()
    {
        Assert.True(ScoutMilestones.AwaitingList(Cal, 18, Year, slots: 2, List(2, 0, 0, year: Year)));
        Assert.False(ScoutMilestones.AwaitingList(Cal, 18, Year, slots: 2, List(2, 0, 0)));
    }

    // ── 候補リスト到着（第24週） ─────────────────────────────

    [Fact]
    public void ListArrived_OnRecruitListWeek_Fires()
        => Assert.Equal(ScoutMilestone.ListArrived,
            ScoutMilestones.AtWeek(Cal, Cal.RecruitListWeek, Year, slots: 2, List(2, 0, 0)));

    [Fact]
    public void ListArrived_Uncontracted_NeverFires()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AtWeek(Cal, Cal.RecruitListWeek, Year, slots: 0, list: null));

    [Fact]
    public void ListArrived_NoListWasBuilt_DoesNotFire()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AtWeek(Cal, Cal.RecruitListWeek, Year, slots: 2, list: null));

    [Fact]
    public void ListArrived_OtherWeeks_DoNotFire()
    {
        var list = List(2, 0, 0);
        foreach (var w in Enumerable.Range(0, Cal.WeeksPerYear)
                     .Where(w => w != Cal.RecruitListWeek && w != ScoutMilestones.DeadlineWeek(Cal)))
            Assert.Equal(ScoutMilestone.None, ScoutMilestones.AtWeek(Cal, w, Year, slots: 2, list));
    }

    // ── 締切前の念押し ────────────────────────────────────

    [Fact]
    public void DeadlineWeek_IsInsideRecruitingSeason_AndBeforeConfirm()
    {
        var w = ScoutMilestones.DeadlineWeek(Cal);
        Assert.True(Cal.IsRecruitingSeason(w));
        Assert.True(w < Cal.ProspectConfirmWeek - 1);   // 確定週の直前（年契約の更新の週）とは重ならない
    }

    [Fact]
    public void Reminder_SlotsLeft_Fires()
        => Assert.Equal(ScoutMilestone.DeadlineReminder,
            ScoutMilestones.AtWeek(Cal, ScoutMilestones.DeadlineWeek(Cal), Year, slots: 3, List(3, 2, 1)));

    [Fact]
    public void Reminder_NobodyVisited_Fires()
        => Assert.Equal(ScoutMilestone.DeadlineReminder,
            ScoutMilestones.AtWeek(Cal, ScoutMilestones.DeadlineWeek(Cal), Year, slots: 3, List(3, 0, 0)));

    [Fact]
    public void Reminder_SlotsUsedUpAndVisited_DoesNotFire()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AtWeek(Cal, ScoutMilestones.DeadlineWeek(Cal), Year, slots: 3, List(3, 3, 2)));

    [Fact]
    public void Reminder_Uncontracted_NeverFires()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AtWeek(Cal, ScoutMilestones.DeadlineWeek(Cal), Year, slots: 0, List(3, 0, 0)));

    [Fact]
    public void Reminder_AlreadyResolved_DoesNotFire()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AtWeek(Cal, ScoutMilestones.DeadlineWeek(Cal), Year, slots: 3, List(3, 0, 0, resolved: true)));

    [Fact]
    public void Reminder_LastYearsListOnly_DoesNotFire()
        => Assert.Equal(ScoutMilestone.None,
            ScoutMilestones.AtWeek(Cal, ScoutMilestones.DeadlineWeek(Cal), Year, slots: 3, List(3, 0, 0, year: Year)));

    [Fact]
    public void EventIds_AreDistinctAndNoneHasNone()
    {
        Assert.Null(ScoutMilestones.EventId(ScoutMilestone.None));
        var ids = new[] { ScoutMilestone.ChooseField, ScoutMilestone.ListArrived, ScoutMilestone.DeadlineReminder }
            .Select(ScoutMilestones.EventId).ToList();
        Assert.Equal(3, ids.Distinct().Count());
    }
}
