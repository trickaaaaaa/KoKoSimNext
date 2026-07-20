using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 実戦成長ループ（設計書02 §5.3a, Q8・2026-07-20）。出場者の精神力/走塁判断/捕手リードが
/// 試合出場で伸び、非出場者は伸びず、隠しcapで頭打ちになることを固定する。乱数不使用＝決定論。
/// </summary>
public sealed class MatchGrowthModelTests
{
    private static readonly SeasonCalendar Calendar = new();
    private static readonly GrowthStageTable Stages = new();
    private static readonly TrainingCoefficients C = new();

    private static DevelopingPlayer Fielder(int id, int grade = 2, GrowthType growth = GrowthType.Standard)
    {
        var p = new DevelopingPlayer { Id = id, Grade = grade, GrowthType = growth, Mental = 46, Lead = 46 };
        p.SetLevel(AbilityKind.Baserunning, 45);
        p.SetCap(AbilityKind.Baserunning, 80);
        p.MentalCap = 80;
        p.LeadCap = 80;
        return p;
    }

    private static BattingLine Bat(int order, FieldPosition pos, int sourceId, int pa)
        => new(order, pos, $"P{sourceId}", pa, pa, 0, 0, 0, 0, 0, 0, 0, sourceId);

    private static GameResult Game(IReadOnlyList<BattingLine> homeBatting, IReadOnlyList<PitchingLine> homePitching)
        => new()
        {
            AwayName = "相手", HomeName = "自校", AwayRuns = 0, HomeRuns = 1,
            InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
            HomeBatting = homeBatting, HomePitching = homePitching,
        };

    [Fact]
    public void Participants_GainMentalAndBaserunningExp_NonParticipantsDoNot()
    {
        var starter = Fielder(1);
        var benchWarmer = Fielder(2);
        var roster = new List<DevelopingPlayer> { starter, benchWarmer };
        var game = Game(new[] { Bat(1, FieldPosition.SecondBase, starter.Id, 4) }, Array.Empty<PitchingLine>());

        MatchGrowthModel.Apply(game, managerIsAway: false, roster, week: 10, Calendar, Stages, C);

        Assert.True(starter.MentalExp > 0 || starter.Mental > 46, "出場者の精神力が伸びていない");
        Assert.True(starter.Exp(AbilityKind.Baserunning) > 0 || starter.Level(AbilityKind.Baserunning) > 45,
            "出場者の走塁判断が伸びていない");
        Assert.Equal(0.0, benchWarmer.MentalExp);
        Assert.Equal(46, benchWarmer.Mental);
        Assert.Equal(0.0, benchWarmer.Exp(AbilityKind.Baserunning));
    }

    [Fact]
    public void OnlyCatcher_GainsLead()
    {
        var catcher = Fielder(1);
        var infielder = Fielder(2);
        var roster = new List<DevelopingPlayer> { catcher, infielder };
        var game = Game(new[]
        {
            Bat(1, FieldPosition.Catcher, catcher.Id, 4),
            Bat(2, FieldPosition.Shortstop, infielder.Id, 4),
        }, Array.Empty<PitchingLine>());

        MatchGrowthModel.Apply(game, managerIsAway: false, roster, week: 10, Calendar, Stages, C);

        Assert.True(catcher.LeadExp > 0 || catcher.Lead > 46, "捕手出場でリードが伸びていない");
        Assert.Equal(0.0, infielder.LeadExp);
        Assert.Equal(46, infielder.Lead);
    }

    [Fact]
    public void Pitcher_InBothLines_GrowsOnlyOnce()
    {
        var pitcher = Fielder(1);
        var roster = new List<DevelopingPlayer> { pitcher };
        var game = Game(
            new[] { Bat(9, FieldPosition.Pitcher, pitcher.Id, 3) },
            new[] { new PitchingLine($"P1", 27, 30, 5, 0, 8, 2, 120, pitcher.Id) });

        MatchGrowthModel.Apply(game, managerIsAway: false, roster, week: 10, Calendar, Stages, C);

        // 二重付与なし: 1試合ぶんのexpに一致（stage係数は週10・2年・Standard）。
        var stage = Stages.Coefficient(GrowthType.Standard, Calendar.StageIndex(2, 10));
        Assert.Equal(C.MatchMentalExp * stage, pitcher.MentalExp, 6);
    }

    [Fact]
    public void Growth_StopsAtHiddenCap_AndDiscardsExcess()
    {
        var p = Fielder(1);
        p.Mental = 79; // cap=80 の直下
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(new[] { Bat(1, FieldPosition.LeftField, p.Id, 4) }, Array.Empty<PitchingLine>());

        for (var i = 0; i < 400; i++)
            MatchGrowthModel.Apply(game, managerIsAway: false, roster, week: 10, Calendar, Stages, C);

        Assert.Equal(80, p.Mental);       // cap 到達で停止
        Assert.Equal(0.0, p.MentalExp);   // 余剰expは破棄
    }

    [Fact]
    public void RegularCatcher_ThreeYearArc_LeadRisesMeaningfully()
    {
        // 育成カーブDoD（Q8(b)）: レギュラー捕手が年12試合×3年出場すると、リードが有意に伸びて
        // cap までは到達しない（時間アークの中庸帯）。週は各学年の夏前後を近似。
        var p = new DevelopingPlayer { Id = 1, Grade = 1, GrowthType = GrowthType.Standard, Mental = 46, Lead = 46 };
        p.MentalCap = 88;
        p.LeadCap = 88;
        p.SetLevel(AbilityKind.Baserunning, 45);
        p.SetCap(AbilityKind.Baserunning, 88);
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(new[] { Bat(2, FieldPosition.Catcher, p.Id, 4) }, Array.Empty<PitchingLine>());

        for (var year = 1; year <= 3; year++)
        {
            p.Grade = year;
            for (var g = 0; g < 12; g++)
                MatchGrowthModel.Apply(game, managerIsAway: false, roster, week: 15 + g, Calendar, Stages, C);
        }

        Assert.InRange(p.Lead - 46, 5, 20);   // 3年間で+5〜+20（設計帯: 「実戦で開花」だが青天井ではない）
        Assert.InRange(p.Mental - 46, 3, 18);
        Assert.True(p.Lead < 88, "3年でcapへ張り付くのは伸びすぎ");
    }

    [Fact]
    public void AwaySide_UsesAwayLines()
    {
        var p = Fielder(1);
        var roster = new List<DevelopingPlayer> { p };
        var game = new GameResult
        {
            AwayName = "自校", HomeName = "相手", AwayRuns = 2, HomeRuns = 1,
            InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
            AwayBatting = new[] { Bat(1, FieldPosition.CenterField, p.Id, 4) },
        };

        MatchGrowthModel.Apply(game, managerIsAway: true, roster, week: 10, Calendar, Stages, C);
        Assert.True(p.MentalExp > 0, "Away側の自校出場者が伸びていない");
    }
}
