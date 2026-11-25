using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>スランプの立て直し（設計書03 §5.5・issue #1481）: 起用法・休養・個別指導で早く抜ける。</summary>
public sealed class SlumpRecoveryTests
{
    private static GrowthEventCoefficients Quiet() => new()
    {
        AwakeningWeeklyProb = 0, BreakthroughWeeklyProb = 0, SlumpWeeklyProb = 0, PlateauWeeklyProb = 0,
        YipsWeeklyProb = 0, GrandAwakeningWeeklyProb = 0, GrowthSurgeWeeklyProb = 0, NewPitchWeeklyProb = 0,
    };

    private static DevelopingPlayer Slumping(int weeks, int id = 1)
    {
        var p = new DevelopingPlayer { Id = id, SlumpWeeks = weeks };
        PitcherRoleResolver.SeedAptitudeForRole(p, false);
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, 40); p.SetCap(k, 99); }
        return p;
    }

    private static int Week(DevelopingPlayer p, double rest = 0.0)
    {
        GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(1), Quiet(), null, _ => rest);
        return p.SlumpWeeks;
    }

    [Fact]
    public void NoHelp_SlumpShrinksByOneWeek()
        => Assert.Equal(4, Week(Slumping(5)));

    [Fact]
    public void IndividualCoaching_ShortensTheSlumpFaster()
    {
        var p = Slumping(5);
        p.IndividualCoaching = true;
        Assert.Equal(3, Week(p));
    }

    [Fact]
    public void RestWeek_ShortensTheSlumpFaster_OnlyAtOrAboveTheShareThreshold()
    {
        var c = Quiet();
        Assert.Equal(3, Week(Slumping(5), rest: c.SlumpRestShareMin));
        Assert.Equal(4, Week(Slumping(5), rest: c.SlumpRestShareMin - 0.01));
    }

    [Fact]
    public void CoachingAndRest_Stack_AndNeverGoBelowZero()
    {
        var p = Slumping(5);
        p.IndividualCoaching = true;
        Assert.Equal(2, Week(p, rest: 0.5));
        Assert.Equal(0, Week(p, rest: 0.5));
        Assert.Equal(0, Week(p, rest: 0.5));
    }

    [Fact]
    public void RestShare_IsRestMinutesOverBudget()
    {
        var plan = new TrainingPlan
        {
            Preset = TrainingPreset.Custom,
            Allocations = new[] { new MenuAllocation(TrainingMenu.Batting, 200), new MenuAllocation(TrainingMenu.Rest, 100) },
        };
        Assert.Equal(100 / 300.0, TrainingPresets.RestShare(plan, 300), 6);
        Assert.Equal(0.0, TrainingPresets.RestShare(TrainingPlan.Default, 300));
    }

    private static GameResult Game(BattingLine[] batting, PitchingLine[] pitching) => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 1, HomeRuns = 5,
        InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
        HomeBatting = batting, HomePitching = pitching,
    };

    [Fact]
    public void MatchAppearance_OnlyGoodPerformanceBySlumpingPlayersShortensTheSlump()
    {
        var c = new GrowthEventCoefficients();
        var hero = Slumping(4, id: 1);       // 好打
        var flop = Slumping(4, id: 2);       // 出たが結果なし
        var healthy = Slumping(0, id: 3);    // スランプではない
        var bench = Slumping(4, id: 4);      // 出ていない
        var game = Game(new[]
        {
            new BattingLine(1, FieldPosition.CenterField, "好打", 5, 5, 3, 1, 0, 0, 2, 0, 0, SourceId: 1),
            new BattingLine(2, FieldPosition.LeftField, "不発", 4, 4, 0, 0, 0, 0, 0, 0, 2, SourceId: 2),
            new BattingLine(3, FieldPosition.RightField, "元気", 5, 5, 3, 0, 0, 0, 2, 0, 0, SourceId: 3),
        }, System.Array.Empty<PitchingLine>());

        var helped = GrowthEventModel.SlumpRecoveryAfterMatch(game, managerIsAway: false,
            new[] { hero, flop, healthy, bench }, c);

        Assert.Equal(new[] { hero }, helped);
        Assert.Equal(4 - c.SlumpMatchRecoveryWeeks, hero.SlumpWeeks);
        Assert.Equal(4, flop.SlumpWeeks);
        Assert.Equal(0, healthy.SlumpWeeks);
        Assert.Equal(4, bench.SlumpWeeks);
    }

    [Fact]
    public void MatchAppearance_PitcherWhoGoodAndBatsToo_IsHelpedOnce()
    {
        var c = new GrowthEventCoefficients();
        var ace = Slumping(4, id: 5);
        var game = Game(
            new[] { new BattingLine(1, FieldPosition.Pitcher, "二刀流", 5, 5, 3, 0, 0, 0, 2, 0, 0, SourceId: 5) },
            new[] { new PitchingLine("二刀流", 27, 30, 4, 1, 8, 1, 110, SourceId: 5) });

        GrowthEventModel.SlumpRecoveryAfterMatch(game, managerIsAway: false, new[] { ace }, c);

        Assert.Equal(4 - c.SlumpMatchRecoveryWeeks, ace.SlumpWeeks);
    }
}
