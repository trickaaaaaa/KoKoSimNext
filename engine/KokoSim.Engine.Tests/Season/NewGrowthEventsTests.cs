using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// issue #694 で足した上昇イベント（分野別の覚醒・大覚醒・伸びしろアップ・新球種・大舞台）。
/// どれも「兆し」の上に起き、才能上限を超えず、表示用の通知には実際に動いた量が載る。
/// </summary>
public sealed class NewGrowthEventsTests
{
    /// <summary>全部の確率を0にした係数（テストごとに1つだけ開ける）。</summary>
    private static GrowthEventCoefficients None() => new()
    {
        AwakeningWeeklyProb = 0, BreakthroughWeeklyProb = 0, SlumpWeeklyProb = 0, PlateauWeeklyProb = 0,
        YipsWeeklyProb = 0, GrandAwakeningWeeklyProb = 0, GrowthSurgeWeeklyProb = 0, NewPitchWeeklyProb = 0,
    };

    private static DevelopingPlayer Fielder(int level = 40, int cap = 99)
    {
        var p = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(p, false);
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, level); p.SetCap(k, cap); }
        return p;
    }

    private static DevelopingPlayer Pitcher()
    {
        var p = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(p, true);
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, 40); p.SetCap(k, 99); }
        return p;
    }

    [Fact]
    public void Awakening_RaisesTwoDifferentAbilitiesOfOneDomain_AndReportsTheRealGains()
    {
        var p = Fielder();
        p.BattingGrowth = 1.4; p.DefenseGrowth = 1.4;
        p.ConditionValue = 0.5;
        var c = None() with { AwakeningWeeklyProb = 1.0 };

        var n = Assert.Single(GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(3), c));
        Assert.Equal(GrowthEventType.Awakening, n.Type);
        Assert.NotNull(n.Domain);
        Assert.Equal(2, n.Gains.Count);
        Assert.NotEqual(n.Gains[0].Ability, n.Gains[1].Ability);
        var kinds = GrowthEventModel.KindsOf(n.Domain!.Value);
        foreach (var g in n.Gains)
        {
            Assert.Contains(g.Ability, kinds);
            Assert.Equal(c.AwakeningGain, g.Amount);
            Assert.Equal(40 + c.AwakeningGain, p.Level(g.Ability));
        }
    }

    [Fact]
    public void Awakening_PicksVariousDomains()
    {
        var c = None() with { AwakeningWeeklyProb = 1.0 };
        var seen = new System.Collections.Generic.HashSet<GrowthDomain>();
        for (ulong s = 1; s <= 40; s++)
        {
            var p = Fielder();
            p.BattingGrowth = 1.4; p.DefenseGrowth = 1.4;
            p.ConditionValue = 0.5;
            foreach (var n in GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(s), c)) seen.Add(n.Domain!.Value);
        }
        Assert.Contains(GrowthDomain.Batting, seen);
        Assert.Contains(GrowthDomain.Defense, seen);
        Assert.Contains(GrowthDomain.Running, seen);
        Assert.DoesNotContain(GrowthDomain.Pitching, seen);   // 投球の覚醒は投手寄りの選手だけ
    }

    [Fact]
    public void Awakening_AtCap_DoesNotFire()
    {
        var p = Fielder(level: 70, cap: 70);
        p.BattingGrowth = 1.4; p.DefenseGrowth = 1.4;
        p.ConditionValue = 0.5;
        Assert.Empty(GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(1), None() with { AwakeningWeeklyProb = 1.0 }));
        Assert.All(AbilityKinds.All, k => Assert.Equal(70, p.Level(k)));
    }

    [Fact]
    public void GrandAwakening_NeedsStrongForm_AndLiftsTheCapOfThatAbility()
    {
        var c = None() with { GrandAwakeningWeeklyProb = 1.0 };
        var lukewarm = Fielder(level: 60, cap: 62);
        lukewarm.BattingGrowth = 1.4;
        lukewarm.ConditionValue = 0.2;   // 好調が足りない
        Assert.Empty(GrowthEventModel.Week(new[] { lukewarm }, new Xoshiro256Random(1), c));

        var p = Fielder(level: 60, cap: 62);
        p.BattingGrowth = 1.4;
        p.ConditionValue = 0.6;
        var n = Assert.Single(GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(1), c));
        Assert.Equal(GrowthEventType.GrandAwakening, n.Type);
        var g = Assert.Single(n.Gains);
        Assert.Equal(62 + c.GrandAwakeningCapLift, p.Cap(g.Ability));
        Assert.Equal(p.Cap(g.Ability), p.Level(g.Ability));   // 上がった上限まで（+10 は上限で止まる）
        Assert.Equal(p.Level(g.Ability) - 60, g.Amount);
    }

    [Fact]
    public void GrowthSurge_RaisesGrowthAndCap_SoLaterTrainingGrowsFaster()
    {
        var c = None() with { GrowthSurgeWeeklyProb = 1.0 };
        var p = Fielder(cap: 80);
        p.ConditionValue = 0.5;
        var n = Assert.Single(GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(5), c));
        Assert.Equal(GrowthEventType.GrowthSurge, n.Type);
        Assert.Equal(c.GrowthSurgeCapLift, n.CapLift);
        foreach (var k in AbilityKinds.All) Assert.Equal(40, p.Level(k));   // その場では動かない

        // 同じ分野の練習で、伸びしろアップした選手のほうが伸びる。
        var plain = Fielder(cap: 80);
        var (ability, menu) = n.Domain == GrowthDomain.Defense
            ? (AbilityKind.Fielding, TrainingMenu.Defense)
            : (AbilityKind.Contact, TrainingMenu.Batting);
        var t = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        for (var i = 0; i < 60; i++)
        {
            DevelopmentModel.TrainWeek(p, menu, 1, 1.0, stages, t);
            DevelopmentModel.TrainWeek(plain, menu, 1, 1.0, stages, t);
        }
        Assert.True(p.Level(ability) > plain.Level(ability),
            $"伸びしろアップ後の練習で差が出ない（{p.Level(ability)} vs {plain.Level(ability)}）");
    }

    [Fact]
    public void GrowthSurge_StopsAtItsMax()
    {
        var c = None() with { GrowthSurgeWeeklyProb = 1.0 };
        var p = Fielder();
        p.BattingGrowth = 1.79; p.DefenseGrowth = 1.79;
        p.ConditionValue = 0.5;
        GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(9), c);
        Assert.True(p.BattingGrowth <= c.GrowthSurgeMax + 1e-9 && p.DefenseGrowth <= c.GrowthSurgeMax + 1e-9);
    }

    [Fact]
    public void NewPitch_OnlyPitchers_LearnUntilMax_ThenRefineTheWeakest()
    {
        var c = None() with { NewPitchWeeklyProb = 1.0 };
        Assert.Empty(GrowthEventModel.Week(new[] { Fielder() }, new Xoshiro256Random(1), c));

        var p = Pitcher();
        var rng = new Xoshiro256Random(2);
        for (var i = 0; i < c.NewPitchMaxBreaking; i++)
        {
            var n = Assert.Single(GrowthEventModel.Week(new[] { p }, rng, c));
            Assert.Equal(GrowthEventType.NewPitch, n.Type);
            Assert.Contains(p.LearnedPitches, lp => lp.Type == n.Pitch);
        }
        Assert.Equal(c.NewPitchMaxBreaking, p.LearnedPitches.Count);
        Assert.Equal(p.LearnedPitches.Count, p.LearnedPitches.Select(x => x.Type).Distinct().Count());

        var refined = Assert.Single(GrowthEventModel.Week(new[] { p }, rng, c));
        Assert.Equal(GrowthEventType.PitchRefined, refined.Type);
        Assert.Equal(c.NewPitchMaxBreaking, p.LearnedPitches.Count);
        var lp2 = p.LearnedPitches.Single(x => x.Type == refined.Pitch);
        Assert.Equal(c.NewPitchOffset + c.PitchRefineGain, lp2.PowerOffset);
    }

    private static GameResult Game(BattingLine[] batting, PitchingLine[] pitching) => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 1, HomeRuns = 5,
        InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
        HomeBatting = batting, HomePitching = pitching,
    };

    [Fact]
    public void BigStage_OnlyForStandoutLines_AndRaisesMental()
    {
        var c = new GrowthEventCoefficients();
        var hero = Fielder(); hero.Id = 1;
        var quiet = Fielder(); quiet.Id = 2;
        var ace = Pitcher(); ace.Id = 3;
        var game = Game(
            new[]
            {
                new BattingLine(1, FieldPosition.CenterField, "英雄", 5, 5, 3, 1, 0, 0, 2, 0, 0, SourceId: 1),
                new BattingLine(2, FieldPosition.LeftField, "静か", 4, 4, 1, 0, 0, 0, 0, 0, 1, SourceId: 2),
            },
            new[] { new PitchingLine("エース", 27, 30, 4, 1, 8, 1, 110, SourceId: 3) });

        var notices = GrowthEventModel.BigStage(game, managerIsAway: false, new[] { hero, quiet, ace },
            prob: 1.0, new Xoshiro256Random(4), c);

        Assert.Equal(2, notices.Count);
        Assert.DoesNotContain(notices, n => n.Player == quiet);
        Assert.All(notices, n => Assert.Equal(GrowthEventType.BigStage, n.Type));
        Assert.Equal(GrowthDomain.Pitching, notices.Single(n => n.Player == ace).Domain);
        Assert.Equal(GrowthDomain.Batting, notices.Single(n => n.Player == hero).Domain);
        Assert.Equal(50 + c.BigStageMentalGain, hero.Mental);
        Assert.Equal(50, quiet.Mental);
    }

    [Fact]
    public void BigStage_ProbabilityFollowsTheTournament()
    {
        var c = new GrowthEventCoefficients();
        Assert.True(c.BigStageProb(TournamentKind.SummerKoshien) > c.BigStageProb(TournamentKind.Regional));
        Assert.True(c.BigStageProb(TournamentKind.Regional) > c.BigStageProb(TournamentKind.Summer));
        Assert.Equal(c.BigStageProb(TournamentKind.SummerKoshien), c.BigStageProb(TournamentKind.SenbatsuKoshien));

        var hero = Fielder(); hero.Id = 1;
        var game = Game(new[] { new BattingLine(1, FieldPosition.CenterField, "英雄", 5, 5, 4, 0, 0, 1, 4, 0, 0, SourceId: 1) },
            System.Array.Empty<PitchingLine>());
        Assert.Empty(GrowthEventModel.BigStage(game, false, new[] { hero }, prob: 0.0, new Xoshiro256Random(1), c));
    }
}
