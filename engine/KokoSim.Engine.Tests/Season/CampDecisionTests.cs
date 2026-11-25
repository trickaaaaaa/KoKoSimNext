using System.IO;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Season.Events;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>合宿の「する／しない」（issue #1482）。見送ると倍率・結束・費用の三つがすべてなくなる。</summary>
public class CampDecisionTests
{
    private static readonly SeasonCalendar Cal = new();
    private static readonly TrainingCoefficients T = new();
    private static readonly CareerCoefficients Career = new();
    private static readonly CohesionCoefficients C = new();

    private static string FindDataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", name))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data", name);
    }

    [Fact]
    public void CampMultiplier_DefaultSkips_IsUnchanged()
    {
        Assert.Equal(T.SummerCampMult, Cal.CampMultiplier(Cal.SummerCampWeek, T));
        Assert.Equal(T.WinterCampMult, Cal.CampMultiplier(Cal.WinterCampWeek, T));
        Assert.Equal(1.0, Cal.CampMultiplier(Cal.SummerCampWeek + 1, T));
    }

    [Fact]
    public void CampMultiplier_SkippedCamp_IsNeutralOnlyForThatSeason()
    {
        var summerSkipped = new CampSkips(Summer: true, Winter: false);
        Assert.Equal(1.0, Cal.CampMultiplier(Cal.SummerCampWeek, T, summerSkipped));
        Assert.Equal(T.WinterCampMult, Cal.CampMultiplier(Cal.WinterCampWeek, T, summerSkipped));

        var winterSkipped = new CampSkips(Summer: false, Winter: true);
        Assert.Equal(T.SummerCampMult, Cal.CampMultiplier(Cal.SummerCampWeek, T, winterSkipped));
        Assert.Equal(1.0, Cal.CampMultiplier(Cal.WinterCampWeek, T, winterSkipped));
    }

    [Fact]
    public void CampSkips_WithAndIsSkipped_RoundTrip()
    {
        var s = CampSkips.None.With(winter: true, skipped: true);
        Assert.True(s.IsSkipped(winter: true));
        Assert.False(s.IsSkipped(winter: false));
        Assert.Equal(CampSkips.None, s.With(winter: true, skipped: false));
    }

    [Fact]
    public void AnnualFixedExpense_SkippedCampsAreNotPaid()
    {
        const double contract = 12.0;
        var both = CareerEngine.AnnualFixedExpense(contract, Career);
        Assert.Equal(Career.SummerCampCost + Career.WinterCampCost + contract, both, 9);
        Assert.Equal(both - Career.SummerCampCost, CareerEngine.AnnualFixedExpense(contract, Career, new CampSkips(true, false)), 9);
        Assert.Equal(both - Career.WinterCampCost, CareerEngine.AnnualFixedExpense(contract, Career, new CampSkips(false, true)), 9);
        Assert.Equal(contract, CareerEngine.AnnualFixedExpense(contract, Career, new CampSkips(true, true)), 9);
    }

    [Fact]
    public void AnnualFundsNet_AndProjection_FollowTheSkips()
    {
        var skips = new CampSkips(true, true);
        var kept = Career.SummerCampCost + Career.WinterCampCost;
        Assert.Equal(CareerEngine.AnnualFundsNet(50, 20, 0, Career) + kept,
            CareerEngine.AnnualFundsNet(50, 20, 0, Career, skips), 9);
        Assert.Equal(CareerEngine.ProjectedFunds(100, 50, 20, 0, Career) + kept,
            CareerEngine.ProjectedFunds(100, 50, 20, 0, Career, skips), 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkipCamp_ReturnsToTheValueWithoutTheCamp(bool winter)
    {
        var week = winter ? Cal.WinterCampWeek : Cal.SummerCampWeek;
        const double before = 7.5;
        var withCamp = Cohesion.AdvanceWeek(before, week, Cal, C);
        var withoutCamp = Cohesion.DecayWeek(before, C);
        Assert.Equal(withoutCamp, Cohesion.SkipCamp(withCamp, winter, C), 12);
    }

    // ===== 正典（data/events.yaml） =====

    [Theory]
    [InlineData("summer_camp", 18)]
    [InlineData("winter_camp", 38)]
    public void RepositoryTable_CampEvents_AskWhetherToHoldTheCamp(string id, int week)
    {
        var e = EventTableParser.Parse(File.ReadAllText(FindDataFile("events.yaml"))).Single(x => x.Id == id);
        Assert.Equal(EventKind.Fixed, e.Kind);
        Assert.Equal(week, e.CalendarWeek);
        Assert.Equal(new[] { EventStance.CampJoin, EventStance.CampSkip }, e.Choices.Select(c => c.Stance).ToArray());
        Assert.All(e.Choices, c => Assert.Equal(1.0, c.ImproveProb));
        Assert.All(e.Outcomes, o => Assert.True(o.Improved, "合宿の決断に悪化は無い"));

        // 質問の会話には費用・期間・効果の3点が出る（数字は差し込み語＝係数の正典から）。
        foreach (var v in e.Variants)
        {
            var text = string.Join("\n", v.Lines.Select(l => l.Say));
            Assert.Contains("{camp_cost}", text);
            Assert.Contains("{camp_weeks}", text);
            Assert.Contains("{camp_mult}", text);
        }

        // する: いつもの様子に加えて、結束がもう一段上がる会話が数本ある。しない: 結束も効果も動かさない。
        var join = e.Outcomes.Where(o => o.Stance == EventStance.CampJoin).ToList();
        Assert.True(join.Count(o => o.EffectOrNone.Cohesion > 0) >= 3, "結束が上がる合宿の会話が3本以上");
        Assert.True(join.Count(o => o.EffectOrNone.Cohesion == 0) >= 3, "いつもの合宿の会話が3本以上");
        var skip = e.Outcomes.Where(o => o.Stance == EventStance.CampSkip).ToList();
        Assert.NotEmpty(skip);
        Assert.All(skip, o => Assert.True(o.EffectOrNone.IsEmpty));
    }

    [Fact]
    public void Parse_CampStancesAndCohesionEffect()
    {
        const string yaml = @"
events:
  - id: c
    kind: Fixed
    calendar_week: 3
    text: ""t""
    choices:
      - stance: camp_join
        label: ""する""
      - stance: camp_skip
        label: ""しない""
    outcomes:
      - stance: camp_join
        result: improved
        effects:
          cohesion: 3
        lines:
          - who: narration
            say: ""a""
";
        var e = EventTableParser.Parse(yaml).Single();
        Assert.Equal(EventStance.CampJoin, e.Choices[0].Stance);
        Assert.Equal(EventStance.CampSkip, e.Choices[1].Stance);
        Assert.Equal(3.0, e.Outcomes[0].EffectOrNone.Cohesion);
        Assert.False(e.Outcomes[0].EffectOrNone.IsEmpty);
        Assert.True(EventStances.IsCampDecision(EventStance.CampJoin));
        Assert.False(EventStances.MovesCohesion(EventStance.CampSkip));
    }

    // ===== セーブ（v49） =====

    [Fact]
    public void Save_CampSkips_Roundtrip()
    {
        var state = new SaveState { SummerCampSkipped = true, WinterCampSkipped = false };
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.True(s.SummerCampSkipped);
        Assert.False(s.WinterCampSkipped);
    }

    [Fact]
    public void Save_OldSaveWithoutCampKeys_AttendsBothCamps()
    {
        var json = SaveCodec.Serialize(new SaveState { SummerCampSkipped = true, WinterCampSkipped = true });
        var stripped = System.Text.RegularExpressions.Regex.Replace(json, "\"(summer|winter)CampSkipped\":(true|false),?", "")
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":48");
        Assert.DoesNotContain("CampSkipped", stripped);
        Assert.True(SaveCodec.TryDeserialize(stripped, out var s, out var err), err);
        Assert.False(s.SummerCampSkipped);
        Assert.False(s.WinterCampSkipped);
    }
}
