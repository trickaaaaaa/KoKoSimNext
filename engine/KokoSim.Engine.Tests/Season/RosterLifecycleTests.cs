using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
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

    // 背番号の空き詰め検証用: AverageLevel を明示的に制御できる選手（issue #531）。
    private static DevelopingPlayer GuyWithLevel(string name, int grade, int level)
    {
        var p = Guy(name, grade);
        PitcherRoleResolver.SeedAptitudeForRole(p, pitcher: false);
        foreach (var k in AbilityKinds.All) { p.SetCap(k, 99); p.SetLevel(k, level); }
        return p;
    }

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

    // --- issue #531: 引退で空いた背番号の自動補充 ---

    [Fact]
    public void BeginNewTeam_ClearsRetiredNumbers_AndFillsVacanciesFromBenchOutSurvivors()
    {
        var captain = GuyWithLevel("三年主将", 3, 90);
        var reserve = GuyWithLevel("三年控え", 3, 40);
        var starterA = GuyWithLevel("二年A", 2, 80);
        var benchOutStrong = GuyWithLevel("一年強", 1, 70);
        var benchOutWeak = GuyWithLevel("一年弱", 1, 30);
        captain.UniformNumber = 1;
        reserve.UniformNumber = 2;
        starterA.UniformNumber = 3;
        benchOutStrong.UniformNumber = 0;
        benchOutWeak.UniformNumber = 0;
        var roster = new List<DevelopingPlayer> { captain, reserve, starterA, benchOutStrong, benchOutWeak };

        RosterLifecycle.BeginNewTeam(roster);

        Assert.Equal(0, captain.UniformNumber);          // 引退者の番号は解放される
        Assert.Equal(0, reserve.UniformNumber);
        Assert.Equal(3, starterA.UniformNumber);         // 在籍者の既存割当は変わらない
        // 空いた1・2番を、ベンチ外の在籍部員のうち AverageLevel 降順で詰める。
        Assert.Equal(1, benchOutStrong.UniformNumber);
        Assert.Equal(2, benchOutWeak.UniformNumber);
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void BeginNewTeam_LeavesVacanciesWhenBenchOutSurvivorsRunOut()
    {
        var captain = GuyWithLevel("三年主将", 3, 90);
        var starterA = GuyWithLevel("二年A", 2, 80);
        captain.UniformNumber = 1;
        starterA.UniformNumber = 2;
        var roster = new List<DevelopingPlayer> { captain, starterA };   // ベンチ外の在籍部員がいない

        RosterLifecycle.BeginNewTeam(roster);

        Assert.Equal(0, captain.UniformNumber);
        Assert.Equal(2, starterA.UniformNumber);         // 埋める相手がいないので1番は空のまま
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void BeginNewTeam_DoesNotAssignNumbersToRetiredPlayers()
    {
        // 引退者もClear後は背番号0になるが、FillVacanciesの対象はActive(在籍者)限定＝引退者へ新番号は付かない。
        var captain = GuyWithLevel("三年主将", 3, 99);   // ロスター最強でも引退済みなら対象外
        var benchOut = GuyWithLevel("一年", 1, 10);
        captain.UniformNumber = 1;
        benchOut.UniformNumber = 0;
        var roster = new List<DevelopingPlayer> { captain, benchOut };

        RosterLifecycle.BeginNewTeam(roster);

        Assert.Equal(0, captain.UniformNumber);          // 引退者は0のまま(新番号を貰わない)
        Assert.Equal(1, benchOut.UniformNumber);         // 在籍者だけが空きを埋める
    }

    [Fact]
    public void BeginNewTeam_IsIdempotentForUniformNumbers()
    {
        var captain = GuyWithLevel("三年主将", 3, 90);
        var benchOut = GuyWithLevel("一年", 1, 50);
        captain.UniformNumber = 1;
        benchOut.UniformNumber = 0;
        var roster = new List<DevelopingPlayer> { captain, benchOut };

        RosterLifecycle.BeginNewTeam(roster);
        var after1 = roster.Select(p => p.UniformNumber).ToList();
        RosterLifecycle.BeginNewTeam(roster);            // 2度目（同じ週フックが誤って重複発火しても安全）
        var after2 = roster.Select(p => p.UniformNumber).ToList();

        Assert.Equal(after1, after2);
    }

    // --- issue #535: 年度替わり（4月）の一括処理 AdvanceYear ---

    private static DevelopingPlayer Player(string name, int grade, int id)
        => new() { Name = name, Grade = grade, Id = id, Leadership = 50, Mental = 50 };

    private static List<DevelopingPlayer> GradedRoster() => new()
    {
        Player("三年甲", 3, 1),
        Player("三年乙", 3, 2),
        Player("二年甲", 2, 3),
        Player("二年乙", 2, 4),
        Player("一年甲", 1, 5),
        Player("一年乙", 1, 6),
    };

    [Fact]
    public void AdvanceYear_Promotes_Graduates_AndAddsFreshmen()
    {
        var roster = GradedRoster();
        var result = RosterLifecycle.AdvanceYear(
            roster, year: 2, RosterCoefficients.Default, new Xoshiro256Random(99));

        // 3年（元Grade3）は卒業してロスターから消え、退避リストへ返る。
        Assert.Equal(new[] { "三年甲", "三年乙" }, result.Graduated.Select(p => p.Name));
        Assert.DoesNotContain(roster, p => p.Name is "三年甲" or "三年乙");

        // 在校生は1つ進級（元2年→3年・元1年→2年）。
        Assert.Equal(3, roster.First(p => p.Name == "二年甲").Grade);
        Assert.Equal(2, roster.First(p => p.Name == "一年甲").Grade);

        // 新入生（Grade=1）が新規に加入している。
        Assert.NotEmpty(result.Newcomers);
        Assert.All(result.Newcomers, p => Assert.Equal(1, p.Grade));
        Assert.Contains(roster, p => p.Grade == 1);

        // 卒業した3年（元Grade3）は、除去前に Grade++ されて4になっている（engine と同じ流儀）。
        Assert.All(result.Graduated, p => Assert.Equal(4, p.Grade));
    }

    [Fact]
    public void AdvanceYear_AssignsMonotonicIds_NoCollisionWithSurvivorsOrGraduates()
    {
        var roster = GradedRoster();                 // 既存Id = 1..6
        var nextId = roster.Max(p => p.Id) + 1;      // = 7
        var survivorAndGradIds = new HashSet<int>(GradedRoster().Select(p => p.Id));

        var result = RosterLifecycle.AdvanceYear(
            roster, year: 2, RosterCoefficients.Default, new Xoshiro256Random(99), nextId: nextId);

        // 新入生の Id は 7 から連番で、単調増加＝既存/卒業生の Id と衝突しない。
        Assert.Equal(Enumerable.Range(nextId, result.Newcomers.Count).ToArray(),
            result.Newcomers.Select(p => p.Id).ToArray());
        Assert.All(result.Newcomers, p => Assert.DoesNotContain(p.Id, survivorAndGradIds));
        Assert.Equal(nextId + result.Newcomers.Count, result.NextId);

        // ロスター全体（在校生＋新入生）の Id に重複が無い。
        var ids = roster.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AdvanceYear_ZeroNextId_LeavesNewcomerIdsUnassigned()
    {
        // 既定 nextId=0（SeasonEngine 経路）は採番しない＝新入生 Id は 0 のまま（切り出し前と一致）。
        var roster = GradedRoster();
        var result = RosterLifecycle.AdvanceYear(
            roster, year: 2, RosterCoefficients.Default, new Xoshiro256Random(99));

        Assert.All(result.Newcomers, p => Assert.Equal(0, p.Id));
        Assert.Equal(0, result.NextId);
    }

    [Fact]
    public void AdvanceYear_EnsuresCaptainAmongCurrentPlayers()
    {
        var roster = GradedRoster();
        RosterLifecycle.AdvanceYear(
            roster, year: 2, RosterCoefficients.Default, new Xoshiro256Random(99));

        var captains = roster.Where(p => p.IsCaptain).ToList();
        Assert.Single(captains);
        Assert.Equal(3, captains[0].Grade);   // 新最上級生（元2年）から選ばれる
    }

    [Fact]
    public void AdvanceYear_HigherTalentCenter_RaisesNewcomerLevels()
    {
        // 名声由来の talentCenter（issue #127）を上げると新入生の質が上がる。
        double AvgNewcomerLevel(double? center)
        {
            var roster = GradedRoster();
            var result = RosterLifecycle.AdvanceYear(
                roster, year: 2, RosterCoefficients.Default, new Xoshiro256Random(99), talentCenter: center);
            return result.Newcomers.Average(p => p.AverageLevel());
        }

        var high = AvgNewcomerLevel(RosterCoefficients.Default.TalentCenterFromFame(100));
        var baseline = AvgNewcomerLevel(RosterCoefficients.Default.TalentCenterFromFame(0));
        Assert.True(high > baseline, $"名声100の新入生平均 {high:F1} が名声0 {baseline:F1} を上回らない");
    }

    [Fact]
    public void BeginNewTeam_DoesNotChangeArchivedUniformNumbers()
    {
        // 大会終了時のアーカイブ確定 → その後の引退週で番号を詰め直しても、
        // 既にアーカイブへ書き込んだ「当時の背番号」は不変（値コピーのため。issue #531の完了条件）。
        var captain = GuyWithLevel("三年主将", 3, 90);
        captain.Id = 1;
        captain.UniformNumber = 1;
        var roster = new List<DevelopingPlayer> { captain };

        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(
            new GameResult
            {
                AwayName = "相手", HomeName = "自校", AwayRuns = 0, HomeRuns = 1,
                InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
                HomeBatting = new[]
                {
                    new BattingLine(1, FieldPosition.CenterField, "三年主将", 1, 1, 1, 0, 0, 0, 0, 0, 0,
                        SourceId: 1, Runs: 1),
                },
            },
            managerIsAway: false);
        store.ArchiveCurrentTournament(TournamentSlot.SummerPref,
            new Dictionary<int, (int Grade, int UniformNumber)> { [1] = (captain.Grade, captain.UniformNumber) });

        RosterLifecycle.BeginNewTeam(roster);   // 引退→背番号0（アーカイブより後）

        Assert.Equal(0, captain.UniformNumber);
        var archived = store.Archive.Get(new TournamentArchiveKey(3, TournamentSlot.SummerPref), 1);
        Assert.NotNull(archived);
        Assert.Equal(1, archived!.UniformNumber);   // アーカイブ側は当時の1番のまま
    }
}
