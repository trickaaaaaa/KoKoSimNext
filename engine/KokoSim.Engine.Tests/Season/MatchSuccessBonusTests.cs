using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 成功体験ボーナス（設計書02 §5.3b・issue #1487）。好打→ミート/パワー、好投→制球/キレ/スタミナ、
/// 好守→守備/捕球/送球精度に少量のexpが入り、大舞台ほど大きく、練習の主効果を上回らないことを固定する。
/// </summary>
public sealed class MatchSuccessBonusTests
{
    private static readonly SeasonCalendar Calendar = new();
    private static readonly GrowthStageTable Stages = new();
    private static readonly TrainingCoefficients C = new();

    private static DevelopingPlayer Player(int id)
    {
        var p = new DevelopingPlayer { Id = id, Grade = 2, GrowthType = GrowthType.Standard };
        foreach (var k in new[]
                 {
                     AbilityKind.Contact, AbilityKind.Power, AbilityKind.Fielding, AbilityKind.Catching,
                     AbilityKind.ThrowAccuracy, AbilityKind.Control, AbilityKind.PitchRank, AbilityKind.Stamina,
                 })
        {
            p.SetLevel(k, 40);
            p.SetCap(k, 90);
        }
        return p;
    }

    private static BattingLine Bat(int id, FieldPosition pos, int hits = 0, int rbi = 0)
        => new(1, pos, $"P{id}", 4, 4, hits, 0, 0, 0, rbi, 0, 0, id)
        {
            Positions = new[] { pos },
        };

    private static GameResult Game(
        IReadOnlyList<BattingLine> batting, IReadOnlyList<PitchingLine>? pitching = null,
        int errors = 0, IReadOnlyList<FieldingLine>? fielding = null)
        => new()
        {
            AwayName = "相手", HomeName = "自校", AwayRuns = 0, HomeRuns = 1,
            InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
            HomeBatting = batting, HomePitching = pitching ?? Array.Empty<PitchingLine>(),
            HomeErrors = errors, HomeFielding = fielding ?? Array.Empty<FieldingLine>(),
        };

    private static void Run(GameResult g, IReadOnlyList<DevelopingPlayer> roster, TournamentKind? kind)
        => MatchGrowthModel.ApplySuccess(g, managerIsAway: false, roster, kind, week: 10, Calendar, Stages, C);

    // 練習と同じ ApplyExp を通した期待値（成長段階係数×伸びしろ×素質の倍率は練習と共通）。
    private static double Reference(AbilityKind kind, double baseExp)
    {
        var r = Player(0);
        DevelopmentModel.ApplyExp(r, kind, baseExp * Stages.Coefficient(GrowthType.Standard, Calendar.StageIndex(2, 10)), C);
        return r.Exp(kind);
    }

    [Fact]
    public void GoodBatting_GrowsContactAndPower_Only()
    {
        var p = Player(1);
        Run(Game(new[] { Bat(1, FieldPosition.LeftField, hits: 3) }, errors: 2), new[] { p }, null);

        Assert.Equal(Reference(AbilityKind.Contact, C.MatchSuccessBatExp), p.Exp(AbilityKind.Contact), 6);
        Assert.True(p.Exp(AbilityKind.Power) > 0);
        Assert.Equal(0.0, p.Exp(AbilityKind.Fielding));   // 失策のある試合は好守にならない
        Assert.Equal(0.0, p.Exp(AbilityKind.Control));
    }

    [Fact]
    public void QuietBatting_GivesNothing()
    {
        var p = Player(1);
        Run(Game(new[] { Bat(1, FieldPosition.DesignatedHitter, hits: 1, rbi: 1) }, errors: 1), new[] { p }, null);

        Assert.Equal(0.0, p.Exp(AbilityKind.Contact));
        Assert.Equal(0.0, p.Exp(AbilityKind.Power));
    }

    [Fact]
    public void GoodPitching_GrowsControlPitchRankStamina()
    {
        var p = Player(1);
        var g = Game(Array.Empty<BattingLine>(),
            new[] { new PitchingLine("P1", 27, 30, 4, 1, 8, 2, 110, p.Id) }, errors: 1);
        Run(g, new[] { p }, null);

        Assert.Equal(Reference(AbilityKind.Control, C.MatchSuccessPitchExp), p.Exp(AbilityKind.Control), 6);
        Assert.True(p.Exp(AbilityKind.PitchRank) > 0);
        Assert.True(p.Exp(AbilityKind.Stamina) > 0);
        Assert.Equal(0.0, p.Exp(AbilityKind.Contact));
    }

    [Fact]
    public void BadPitching_GivesNothing()
    {
        var p = Player(1);
        var g = Game(Array.Empty<BattingLine>(), new[] { new PitchingLine("P1", 12, 25, 9, 7, 2, 3, 90, p.Id) });
        Run(g, new[] { p }, null);

        Assert.Equal(0.0, p.Exp(AbilityKind.Control));
    }

    [Fact]
    public void ErrorFreeFielder_GrowsDefense_ButNotDhOrErrorMaker()
    {
        var fielder = Player(1);
        var dh = Player(2);
        var culprit = Player(3);
        var game = Game(
            new[]
            {
                Bat(fielder.Id, FieldPosition.Shortstop),
                Bat(dh.Id, FieldPosition.DesignatedHitter),
                Bat(culprit.Id, FieldPosition.ThirdBase),
            },
            errors: 0,
            fielding: new[] { new FieldingLine(culprit.Id, FieldPosition.ThirdBase, "P3", 1) });
        Run(game, new[] { fielder, dh, culprit }, null);

        Assert.Equal(Reference(AbilityKind.Fielding, C.MatchSuccessFieldExp), fielder.Exp(AbilityKind.Fielding), 6);
        Assert.True(fielder.Exp(AbilityKind.Catching) > 0);
        Assert.True(fielder.Exp(AbilityKind.ThrowAccuracy) > 0);
        Assert.Equal(0.0, dh.Exp(AbilityKind.Fielding));
        Assert.Equal(0.0, culprit.Exp(AbilityKind.Fielding));
    }

    [Fact]
    public void BiggerStage_GivesBiggerBonus_PracticeIsSmallest()
    {
        double Gain(TournamentKind? kind)
        {
            var p = Player(1);
            Run(Game(new[] { Bat(1, FieldPosition.LeftField, hits: 3) }, errors: 1), new[] { p }, kind);
            return p.Exp(AbilityKind.Contact);
        }

        var practice = Gain(null);
        var prefectural = Gain(TournamentKind.Summer);
        var regional = Gain(TournamentKind.Regional);
        var koshien = Gain(TournamentKind.SummerKoshien);

        Assert.True(practice > 0);
        Assert.True(practice < prefectural && prefectural < regional && regional < koshien);
        Assert.Equal(C.MatchSuccessMultKoshien / C.MatchSuccessMultPractice, koshien / practice, 6);
    }

    [Fact]
    public void Bonus_StaysBelowOneWeekOfMainTraining()
    {
        // 甲子園の最大倍率でも、1試合・1能力あたりの素の付与は練習の主効果1週（BaseMainExp）の半分未満
        // （設計書02 §5.3b: 練習成長を上回らない範囲。成長段階・伸びしろの倍率は練習と共通）。
        var max = Math.Max(C.MatchSuccessBatExp, Math.Max(C.MatchSuccessPitchExp, C.MatchSuccessFieldExp));
        Assert.True(max * C.MatchSuccessMultKoshien < C.BaseMainExp * 0.5);
    }

    [Fact]
    public void Growth_StopsAtCap()
    {
        var p = Player(1);
        p.SetLevel(AbilityKind.Contact, 90);
        Run(Game(new[] { Bat(1, FieldPosition.LeftField, hits: 3) }, errors: 1), new[] { p }, TournamentKind.SummerKoshien);

        Assert.Equal(90, p.Level(AbilityKind.Contact));
        Assert.Equal(0.0, p.Exp(AbilityKind.Contact));
    }

    [Fact]
    public void NonRosterPlayers_AreIgnored()
    {
        var p = Player(1);
        Run(Game(new[] { Bat(99, FieldPosition.LeftField, hits: 4) }), new[] { p }, null);

        Assert.Equal(0.0, p.Exp(AbilityKind.Contact));
    }
}
