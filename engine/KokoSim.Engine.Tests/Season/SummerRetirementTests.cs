using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 夏の敗退直後の3年引退（issue #954）。引退の場面を「どう終わったか」の3通りで分けること、
/// 敗退直後に新チームを発足させた年は第18週の固定フック（安全網）が二度目の発足をしないこと、
/// 発足が第18週より前でも主将を指名できることを固定する。
/// </summary>
public sealed class SummerRetirementTests
{
    private static List<DevelopingPlayer> Roster() => new()
    {
        new DevelopingPlayer { Name = "三年", Grade = 3, Leadership = 80, Mental = 80 },
        new DevelopingPlayer { Name = "二年甲", Grade = 2, Leadership = 40, Mental = 50 },
        new DevelopingPlayer { Name = "二年乙", Grade = 2, Leadership = 70, Mental = 70 },
        new DevelopingPlayer { Name = "一年", Grade = 1 },
    };

    [Fact]
    public void Classify_PrefectureLoss_IsPrefectureLoss()
        => Assert.Equal(SummerExit.PrefectureLoss, SummerRetirement.Classify(TournamentKind.Summer, isChampion: false));

    /// <summary>県大会で優勝した年は甲子園へ進む＝ここでは引退しない（甲子園の後で引退する）。</summary>
    [Fact]
    public void Classify_PrefectureChampion_DoesNotRetireYet()
        => Assert.Null(SummerRetirement.Classify(TournamentKind.Summer, isChampion: true));

    [Fact]
    public void Classify_KoshienLoss_AndChampion_AreSeparate()
    {
        Assert.Equal(SummerExit.KoshienLoss, SummerRetirement.Classify(TournamentKind.SummerKoshien, isChampion: false));
        Assert.Equal(SummerExit.KoshienChampion, SummerRetirement.Classify(TournamentKind.SummerKoshien, isChampion: true));
    }

    [Theory]
    [InlineData(TournamentKind.Autumn)]
    [InlineData(TournamentKind.Regional)]
    [InlineData(TournamentKind.Jingu)]
    [InlineData(TournamentKind.SenbatsuKoshien)]
    public void Classify_OtherTournaments_NeverRetire(TournamentKind kind)
    {
        Assert.Null(SummerRetirement.Classify(kind, isChampion: false));
        Assert.Null(SummerRetirement.Classify(kind, isChampion: true));
    }

    /// <summary>3通りが別の会話（優勝の年に敗戦の台詞を出さない）。</summary>
    [Fact]
    public void EventIds_AreDistinctPerExit_AndRecognized()
    {
        var ids = new[] { SummerExit.PrefectureLoss, SummerExit.KoshienLoss, SummerExit.KoshienChampion }
            .Select(SummerRetirement.EventId).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(SummerRetirement.IsRetirementEvent(id)));
        Assert.False(SummerRetirement.IsRetirementEvent("graduation"));
        Assert.False(SummerRetirement.IsRetirementEvent(null));
    }

    /// <summary>
    /// 敗退直後に新チームを発足させた年は、第18週の安全網が引退・暫定主将の選び直しをしない
    /// （しないと、プレイヤーが指名した主将が自動選出で上書きされ、指名モーダルも二度開く）。
    /// </summary>
    [Fact]
    public void SafetyNet_DoesNotRestartNewTeam_WhenAlreadyStarted()
    {
        var calendar = new SeasonCalendar();
        var roster = Roster();
        RosterLifecycle.BeginNewTeam(roster);
        var designated = roster.Single(p => p.Name == "二年甲");
        CaptainSelector.Designate(roster, designated);

        var t = SeasonTransitions.OnWeekEntered(roster, calendar.SummerRetireWeek + 1, calendar, newTeamAlreadyStarted: true);

        Assert.False(t.StartsNewTeam);
        Assert.True(designated.IsCaptain);   // 指名した主将がそのまま
    }

    /// <summary>まだ発足していなければ、第18週の安全網が従来どおり引退させる（敗退以外の経路の取りこぼし防止）。</summary>
    [Fact]
    public void SafetyNet_StillRetires_WhenNotStarted()
    {
        var calendar = new SeasonCalendar();
        var t = SeasonTransitions.OnWeekEntered(Roster(), calendar.SummerRetireWeek + 1, calendar, newTeamAlreadyStarted: false);
        Assert.True(t.StartsNewTeam);
        Assert.Single(t.NewTeam!.Retired);
    }

    /// <summary>県予選で早く負けた年は第18週より前に新チームが発足する＝その時点から主将を指名できる。</summary>
    [Fact]
    public void DesignationWindow_OpensEarly_WhenNewTeamStartedEarly()
    {
        var cal = new SeasonCalendar();
        var early = cal.SummerTournamentStartWeek + 1;
        Assert.False(CaptainSelector.IsDesignationWindow(early, cal));                          // 従来の窓は閉じている
        Assert.True(CaptainSelector.IsDesignationWindow(early, cal, newTeamStarted: true));     // 発足済みなら開く
        Assert.False(CaptainSelector.IsDesignationWindow(cal.AutumnTournamentStartWeek, cal, newTeamStarted: true)); // 初公式戦以降は不可

        var roster = Roster();
        RosterLifecycle.BeginNewTeam(roster);
        var pick = roster.Single(p => p.Name == "二年甲");
        Assert.False(CaptainSelector.CanDesignate(roster, pick, early, cal));
        Assert.True(CaptainSelector.CanDesignate(roster, pick, early, cal, newTeamStarted: true));
    }

    /// <summary>未発足（false）を渡したときは従来の窓と1ビット一致（旧経路を変えない）。</summary>
    [Fact]
    public void DesignationWindow_WithoutEarlyStart_MatchesLegacy()
    {
        var cal = new SeasonCalendar();
        for (var w = 0; w < cal.WeeksPerYear; w++)
            Assert.Equal(CaptainSelector.IsDesignationWindow(w, cal),
                CaptainSelector.IsDesignationWindow(w, cal, newTeamStarted: false));
    }

    /// <summary>
    /// 3年生が0人（新規開始の1年目＝全員1年生など）の夏は、引退の会話（「三年生が一人ずつ、背番号を返しに来た」）を
    /// 出さない（issue #1531）。新チームの発足そのものは行う。
    /// </summary>
    [Theory]
    [InlineData(SummerExit.PrefectureLoss)]
    [InlineData(SummerExit.KoshienLoss)]
    [InlineData(SummerExit.KoshienChampion)]
    public void EventIdFor_NoThirdYears_ShowsNoRetirementEvent(SummerExit exit)
    {
        var roster = new List<DevelopingPlayer>
        {
            new DevelopingPlayer { Name = "一年甲", Grade = 1 },
            new DevelopingPlayer { Name = "一年乙", Grade = 1 },
        };
        var transition = RosterLifecycle.BeginNewTeam(roster);

        Assert.Empty(transition.Retired);
        Assert.Null(SummerRetirement.EventIdFor(exit, transition));
    }

    /// <summary>3年生が引退した夏は従来どおり、終わり方に応じた引退の会話を出す。</summary>
    [Theory]
    [InlineData(SummerExit.PrefectureLoss)]
    [InlineData(SummerExit.KoshienLoss)]
    [InlineData(SummerExit.KoshienChampion)]
    public void EventIdFor_WithThirdYears_ShowsRetirementEvent(SummerExit exit)
    {
        var transition = RosterLifecycle.BeginNewTeam(Roster());
        Assert.Equal(SummerRetirement.EventId(exit), SummerRetirement.EventIdFor(exit, transition));
    }

    /// <summary>
    /// 敗退直後の発足でも、引退者の背番号を外し空き枠を詰めた案が出来ている＝背番号決定画面の初期案
    /// （<see cref="UniformNumberAssigner.FillVacancies"/>）がそのまま確定できる状態。
    /// </summary>
    [Fact]
    public void BeginNewTeam_LeavesFilledNumbers_AsInitialProposal()
    {
        var roster = Roster();
        roster[0].UniformNumber = 1;
        roster[1].UniformNumber = 2;
        RosterLifecycle.BeginNewTeam(roster);

        Assert.Equal(0, roster[0].UniformNumber);   // 引退した3年は返上
        Assert.Contains(roster, p => !p.IsRetired && p.UniformNumber == 1);   // 空いたエース番号は詰まっている
    }
}
