using System.IO;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Season.Events;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 監督成長イベントの条件（issue #1483）。大敗からの学び＝大差負けの年・名将との練習試合＝強さティアS/Aと戦った年・
/// 指導者講習会＝参加を選んだら確定（抽選なし）。キャリアシム（既定引数）は従来どおり動く＝帯不変。
/// </summary>
public sealed class ManagerGrowthConditionsTests
{
    private static ManagerGrowthCoefficients Only(
        double bigLoss = 0, double rival = 0) => new()
    {
        BigLossProb = bigLoss, FamousRivalProb = rival, ProObVisitBaseProb = 0, ProObVisitFameSlope = 0, MentorProb = 0,
    };

    private static string FindDataFile(string name)
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", name))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data", name);
    }

    // ===== 大敗からの学び =====

    [Fact]
    public void BigLoss_NeedsABlowoutLoss_NotAWinlessYear()
    {
        var c = Only(bigLoss: 1.0);

        var blowout = ManagerGrowthEvents.Yearly(new Manager(), false, wins: 5, new Xoshiro256Random(1), c,
            hadBlowoutLoss: true, attendSeminar: false);
        Assert.Contains(blowout, n => n.Type == ManagerGrowthEventType.LessonFromBigLoss);

        var noBlowout = ManagerGrowthEvents.Yearly(new Manager(), false, wins: 0, new Xoshiro256Random(1), c,
            hadBlowoutLoss: false, attendSeminar: false);
        Assert.DoesNotContain(noBlowout, n => n.Type == ManagerGrowthEventType.LessonFromBigLoss);
    }

    [Fact]
    public void BigLoss_WithoutScores_FallsBackToWinlessYear_SoTheCareerSimIsUnchanged()
    {
        var c = Only(bigLoss: 1.0);
        Assert.Contains(ManagerGrowthEvents.Yearly(new Manager(), false, 0, new Xoshiro256Random(1), c, attendSeminar: false),
            n => n.Type == ManagerGrowthEventType.LessonFromBigLoss);
        Assert.DoesNotContain(ManagerGrowthEvents.Yearly(new Manager(), false, 2, new Xoshiro256Random(1), c, attendSeminar: false),
            n => n.Type == ManagerGrowthEventType.LessonFromBigLoss);
    }

    [Theory]
    [InlineData(10, 40, 40, CoachingArea.Batting)]
    [InlineData(40, 10, 40, CoachingArea.Pitching)]
    [InlineData(40, 40, 10, CoachingArea.Defense)]
    public void BigLoss_BoostsAndReportsTheWeakestArea(double bat, double pit, double def, CoachingArea weakest)
    {
        var m = new Manager { CoachingBatting = bat, CoachingPitching = pit, CoachingDefense = def };
        var c = Only(bigLoss: 1.0);
        var fired = ManagerGrowthEvents.Yearly(m, false, 3, new Xoshiro256Random(1), c, hadBlowoutLoss: true, attendSeminar: false);

        var n = Assert.Single(fired, x => x.Type == ManagerGrowthEventType.LessonFromBigLoss);
        Assert.Equal(weakest, n.Area);
        Assert.Equal(10 + c.BigLossBoost, new[] { m.CoachingBatting, m.CoachingPitching, m.CoachingDefense }.Min(), 6);
    }

    [Fact]
    public void BlowoutLoss_UsesTheSameDefinitionAsTheFormPenalty()
    {
        var f = new FormCoefficients();
        GameResult Game(int home, int away) => new()
        {
            AwayName = "相手", HomeName = "自校", HomeRuns = home, AwayRuns = away, InningsPlayed = 9,
            TotalPitches = 200, PitcherChanges = 0,
            HomeBatting = System.Array.Empty<BattingLine>(), HomePitching = System.Array.Empty<PitchingLine>(),
        };
        var margin = (int)f.MatchBlowoutLossMargin;

        Assert.True(MatchConditionModel.IsBlowoutLoss(Game(0, margin), managerIsAway: false, f));
        Assert.False(MatchConditionModel.IsBlowoutLoss(Game(0, margin - 1), managerIsAway: false, f));
        Assert.False(MatchConditionModel.IsBlowoutLoss(Game(margin, 0), managerIsAway: false, f));   // 勝ちは大敗ではない
        Assert.True(MatchConditionModel.IsBlowoutLoss(Game(margin, 0), managerIsAway: true, f));    // 自校が後攻でなく先攻側
    }

    // ===== 名将との練習試合 =====

    [Fact]
    public void FamousRival_NeedsAPowerhouse_AndStillDrawsTheSameRandoms()
    {
        var c = new ManagerGrowthCoefficients { FamousRivalProb = 1.0, ProObVisitBaseProb = 0.5, MentorProb = 0.5 };

        var faced = ManagerGrowthEvents.Yearly(new Manager(), false, 3, new Xoshiro256Random(7), c,
            facedPowerhouse: true, attendSeminar: false);
        var notFaced = ManagerGrowthEvents.Yearly(new Manager(), false, 3, new Xoshiro256Random(7), c,
            facedPowerhouse: false, attendSeminar: false);

        Assert.Contains(faced, n => n.Type == ManagerGrowthEventType.FamousRivalLesson);
        Assert.DoesNotContain(notFaced, n => n.Type == ManagerGrowthEventType.FamousRivalLesson);
        // 乱数は条件によらず引く＝名将だけが消え、ほかの節目の抽選は動かない。
        Assert.Equal(
            faced.Where(n => n.Type != ManagerGrowthEventType.FamousRivalLesson).Select(n => n.Type),
            notFaced.Select(n => n.Type));
    }

    [Fact]
    public void FamousRival_DefaultIsAlwaysFaced_SoTheCareerSimIsUnchanged()
    {
        var fired = ManagerGrowthEvents.Yearly(new Manager(), false, 3, new Xoshiro256Random(1), Only(rival: 1.0),
            attendSeminar: false);
        Assert.Contains(fired, n => n.Type == ManagerGrowthEventType.FamousRivalLesson);
    }

    [Theory]
    [InlineData(Tier.S, true)]
    [InlineData(Tier.A, true)]
    [InlineData(Tier.B, false)]
    [InlineData(Tier.G, false)]
    public void Powerhouse_IsTierSOrA(Tier tier, bool expected)
        => Assert.Equal(expected, ManagerGrowthEvents.IsPowerhouse(tier));

    // ===== 指導者講習会 =====

    [Fact]
    public void Seminar_JoiningIsCertain_NoLottery()
    {
        var c = Only();
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var m = new Manager { Funds = 100 };
            var fired = ManagerGrowthEvents.Yearly(m, false, 3, new Xoshiro256Random(seed), c);
            var n = Assert.Single(fired, x => x.Type == ManagerGrowthEventType.CoachingSeminar);
            Assert.NotNull(n.Area);
            Assert.Equal(100 - c.SeminarCost, m.Funds, 6);
        }
    }

    [Fact]
    public void Seminar_SkippedOrUnaffordable_CostsNothingAndTeachesNothing()
    {
        var c = Only();

        var skipped = new Manager { Funds = 100 };
        var firedSkip = ManagerGrowthEvents.Yearly(skipped, false, 3, new Xoshiro256Random(1), c, attendSeminar: false);
        Assert.DoesNotContain(firedSkip, n => n.Type == ManagerGrowthEventType.CoachingSeminar);
        Assert.Equal(100, skipped.Funds, 6);

        var broke = new Manager { Funds = c.SeminarCost - 1 };
        var firedBroke = ManagerGrowthEvents.Yearly(broke, false, 3, new Xoshiro256Random(1), c);
        Assert.DoesNotContain(firedBroke, n => n.Type == ManagerGrowthEventType.CoachingSeminar);
        Assert.Equal(c.SeminarCost - 1, broke.Funds, 6);
    }

    [Theory]
    [InlineData(CoachingArea.Batting)]
    [InlineData(CoachingArea.Pitching)]
    [InlineData(CoachingArea.Defense)]
    public void AttendSeminar_RaisesOnlyTheThemeArea_AndPays(CoachingArea area)
    {
        var c = new ManagerGrowthCoefficients();
        var m = new Manager { Funds = 50, CoachingBatting = 30, CoachingPitching = 30, CoachingDefense = 30 };

        Assert.Equal(area, ManagerGrowthEvents.AttendSeminar(m, area, c));

        Assert.Equal(50 - c.SeminarCost, m.Funds, 6);
        Assert.Equal(30 + (area == CoachingArea.Batting ? c.SeminarBoost : 0), m.CoachingBatting, 6);
        Assert.Equal(30 + (area == CoachingArea.Pitching ? c.SeminarBoost : 0), m.CoachingPitching, 6);
        Assert.Equal(30 + (area == CoachingArea.Defense ? c.SeminarBoost : 0), m.CoachingDefense, 6);
    }

    [Fact]
    public void SeminarTheme_IsDeterministicPerSeed()
    {
        Assert.Equal(
            ManagerGrowthEvents.SeminarTheme(new Xoshiro256Random(99)),
            ManagerGrowthEvents.SeminarTheme(new Xoshiro256Random(99)));
        var themes = Enumerable.Range(1, 60).Select(i => ManagerGrowthEvents.SeminarTheme(new Xoshiro256Random((ulong)i))).Distinct();
        Assert.Equal(3, themes.Count());
    }

    [Fact]
    public void ManagerSeasonUpdate_PassesTheNewConditionsThrough()
    {
        var c = Only(bigLoss: 1.0, rival: 1.0);
        var fired = ManagerSeasonUpdate.Apply(new Manager { Funds = 100 }, false, false, 4, new Xoshiro256Random(3),
            growthCoeff: c, hadBlowoutLoss: false, facedPowerhouse: false, attendSeminar: false);
        Assert.DoesNotContain(fired, n => n.Type is ManagerGrowthEventType.LessonFromBigLoss
            or ManagerGrowthEventType.FamousRivalLesson or ManagerGrowthEventType.CoachingSeminar);

        var all = ManagerSeasonUpdate.Apply(new Manager { Funds = 100 }, false, false, 4, new Xoshiro256Random(3),
            growthCoeff: c, hadBlowoutLoss: true, facedPowerhouse: true, attendSeminar: true);
        Assert.Contains(all, n => n.Type == ManagerGrowthEventType.LessonFromBigLoss);
        Assert.Contains(all, n => n.Type == ManagerGrowthEventType.FamousRivalLesson);
        Assert.Contains(all, n => n.Type == ManagerGrowthEventType.CoachingSeminar);
    }

    // ===== 冬の誘い（data/events.yaml） =====

    [Fact]
    public void SeminarEvent_IsAWinterFixedQuestionWithJoinAndSkip()
    {
        var e = EventTableParser.Parse(File.ReadAllText(FindDataFile("events.yaml"))).Single(x => x.Id == "coaching_seminar");

        Assert.Equal(EventKind.Fixed, e.Kind);
        Assert.True(e.CalendarWeek > new SeasonCalendar().WinterCampWeek, "冬のオフ＝冬合宿のあと");
        Assert.True(e.RequiresSeminarFunds);
        Assert.Equal(new[] { EventStance.SeminarJoin, EventStance.SeminarSkip }, e.Choices.Select(c => c.Stance));
        Assert.All(e.Choices, c => Assert.Equal(1.0, c.ImproveProb));
        // 参加後に何を学んだか（{lesson}）を会話で言い切る。
        Assert.Contains(e.Outcomes.Where(o => o.Stance == EventStance.SeminarJoin),
            o => o.Script.Lines.Any(l => l.Say.Contains("{lesson}")));
        Assert.True(EventStances.IsSeminarDecision(EventStance.SeminarJoin));
        Assert.False(EventStances.MovesCohesion(EventStance.SeminarJoin));
    }

    [Fact]
    public void RequiresSeminarFunds_DoesNotLeakIntoTheNextEvent()
    {
        var events = EventTableParser.Parse(
            "events:\n  - id: a\n    kind: Fixed\n    calendar_week: 1\n    requires_seminar_funds: true\n    text: \"t\"\n"
            + "  - id: b\n    kind: Fixed\n    calendar_week: 2\n    text: \"t\"\n");
        Assert.True(events[0].RequiresSeminarFunds);
        Assert.False(events[1].RequiresSeminarFunds);
    }

    [Fact]
    public void SeminarEvent_IsNotAskedWhenTheClubCannotAffordIt()
    {
        var events = EventTableParser.Parse(File.ReadAllText(FindDataFile("events.yaml")));
        var week = events.Single(x => x.Id == "coaching_seminar").CalendarWeek!.Value;

        bool Asked(System.Func<bool>? filter)
        {
            var s = new EventScheduler(events, new EventCoefficients()) { SeminarFundsFilter = filter };
            return s.Week(week, new Xoshiro256Random(1)).Any(f => f.Event.Id == "coaching_seminar");
        }

        Assert.True(Asked(() => true));
        Assert.False(Asked(() => false));
        Assert.False(Asked(null));   // 監督を持たないヘッドレスシムには出ない（ゴールデン不変）
    }

    // ===== セーブ（v51） =====

    [Fact]
    public void Save_YearFlags_Roundtrip()
    {
        var state = new SaveState { ManagerBlowoutLossThisYear = true, ManagerPowerhouseSchoolId = 1234 };
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.True(s.ManagerBlowoutLossThisYear);
        Assert.Equal(1234, s.ManagerPowerhouseSchoolId);
    }

    [Fact]
    public void Save_OldSaveWithoutYearFlags_ReadsAsNone()
    {
        var json = SaveCodec.Serialize(new SaveState { ManagerBlowoutLossThisYear = true, ManagerPowerhouseSchoolId = 5 });
        var stripped = System.Text.RegularExpressions.Regex.Replace(
                json, "\"(managerBlowoutLossThisYear|managerPowerhouseSchoolId)\":(true|false|-?\\d+),?", "")
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":50");
        Assert.DoesNotContain("managerBlowoutLossThisYear", stripped);
        Assert.True(SaveCodec.TryDeserialize(stripped, out var s, out var err), err);
        Assert.False(s.ManagerBlowoutLossThisYear);
        Assert.Equal(-1, s.ManagerPowerhouseSchoolId);
    }
}
