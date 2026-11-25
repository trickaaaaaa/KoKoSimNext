using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>地方大会新聞（設計書05 §1.4・issue #1485）。結果・番狂わせ・怪物のうわさの取捨と、ランナーの観測口。</summary>
public sealed class TournamentNewspaperTests
{
    private static School S(int id, string name, double strength)
        => new() { Id = id, Name = name, PrefectureId = 13, Strength = strength };

    private static GameResult G(int away, int home,
        BattingLine[]? awayBat = null, PitchingLine[]? awayPit = null,
        BattingLine[]? homeBat = null, PitchingLine[]? homePit = null)
        => new()
        {
            AwayName = "a", HomeName = "h", AwayRuns = away, HomeRuns = home,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            AwayBatting = awayBat ?? [], AwayPitching = awayPit ?? [],
            HomeBatting = homeBat ?? [], HomePitching = homePit ?? [],
        };

    private static BackgroundGameReport R(int rounds, School away, School home, GameResult g, bool third = false)
        => new(rounds, third, away, home, g);

    private static PhenomType None(int school, int player) => PhenomType.None;

    [Fact]
    public void Final_AndSemifinal_ArePrinted_EarlyRoundsAreNot()
    {
        var a = S(1, "甲高", 60);
        var b = S(2, "乙高", 60);
        var lines = TournamentNewspaper.Compose(new[]
        {
            R(4, a, b, G(3, 1)),
            R(2, a, b, G(3, 1)),
            R(1, a, b, G(0, 2)),
        }, None);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(NewspaperKind.Result, l.Kind));
        Assert.Contains("準決勝", lines[0].Text);
        Assert.Contains("甲高が決勝へ", lines[0].Text);
        Assert.Contains("決勝", lines[1].Text);
        Assert.Contains("乙高が優勝", lines[1].Text);
        Assert.Equal(2, lines[1].SchoolId);
    }

    [Fact]
    public void ThirdPlaceAndTiedGames_AreSkipped()
    {
        var a = S(1, "甲高", 60);
        var b = S(2, "乙高", 60);
        var lines = TournamentNewspaper.Compose(new[]
        {
            R(2, a, b, G(5, 1), third: true),
            R(1, a, b, G(2, 2)),
        }, None);
        Assert.Empty(lines);
    }

    [Fact]
    public void Upset_NeedsTwoTierGap_AnyRound_AndReplacesResultLine()
    {
        var weak = S(1, "弱高", 45);   // E
        var strong = S(2, "強高", 72);  // B
        var near = S(3, "並高", 62);    // C（Bとは1段差）

        var early = TournamentNewspaper.Compose(new[] { R(5, weak, strong, G(4, 3)) }, None);
        Assert.Single(early);
        Assert.Equal(NewspaperKind.Upset, early[0].Kind);
        Assert.StartsWith("番狂わせ", early[0].Text);
        Assert.Contains("弱高が格上の強高", early[0].Text);

        var semi = TournamentNewspaper.Compose(new[] { R(2, weak, strong, G(4, 3)) }, None);
        Assert.Single(semi);   // 結果の行と二重にしない
        Assert.Equal(NewspaperKind.Upset, semi[0].Kind);
        Assert.StartsWith("準決勝番狂わせ", semi[0].Text);

        var oneTier = TournamentNewspaper.Compose(new[] { R(5, near, strong, G(4, 3)) }, None);
        Assert.Empty(oneTier);

        var favouriteWins = TournamentNewspaper.Compose(new[] { R(5, weak, strong, G(1, 3)) }, None);
        Assert.Empty(favouriteWins);
    }

    [Fact]
    public void PhenomPitcherGem_AndSlugger_AreReported_OnlyForPhenoms()
    {
        var a = S(1, "甲高", 60);
        var b = S(2, "乙高", 60);
        var gem = new PitchingLine("怪投手", 24, 28, 3, 0, 13, 1, 100, SourceId: 7);
        var shelled = new PitchingLine("普通投手", 24, 28, 3, 0, 13, 1, 100, SourceId: 8);
        var slug = new BattingLine(4, FieldPosition.FirstBase, "怪打者", 5, 5, 3, 0, 0, 2, 4, 0, 0, SourceId: 9);
        var ordinary = new BattingLine(5, FieldPosition.LeftField, "普通打者", 5, 5, 3, 0, 0, 2, 4, 0, 0, SourceId: 10);
        PhenomType Phenom(int school, int player) => school == 1 && player is 7 or 9 ? PhenomType.Ace : PhenomType.None;

        var lines = TournamentNewspaper.Compose(new[]
        {
            R(5, a, b, G(2, 1, awayBat: [slug, ordinary], awayPit: [gem, shelled])),
        }, Phenom);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(NewspaperKind.Phenom, l.Kind));
        Assert.Contains(lines, l => l.Text.Contains("怪投手") && l.Text.Contains("13奪三振"));
        Assert.Contains(lines, l => l.Text.Contains("怪打者") && l.Text.Contains("2本塁打"));
        Assert.DoesNotContain(lines, l => l.Text.Contains("普通"));
    }

    [Fact]
    public void PhenomQuietGame_IsNotReported()
    {
        var a = S(1, "甲高", 60);
        var b = S(2, "乙高", 60);
        var quiet = new PitchingLine("怪投手", 15, 20, 5, 3, 4, 2, 80, SourceId: 7);
        var lines = TournamentNewspaper.Compose(new[]
        {
            R(5, a, b, G(2, 1, awayPit: [quiet])),
        }, (_, _) => PhenomType.Ace);
        Assert.Empty(lines);
    }

    [Fact]
    public void ExtraLines_AreCapped_ButResultLinesAreKept()
    {
        var weak = S(1, "弱高", 45);
        var strong = S(2, "強高", 72);
        var reports = new List<BackgroundGameReport>();
        for (var i = 0; i < 6; i++) reports.Add(R(5, weak, strong, G(4, 3)));
        reports.Add(R(1, S(3, "丙高", 60), S(4, "丁高", 60), G(5, 0)));

        var lines = TournamentNewspaper.Compose(reports, None);

        Assert.Equal(1 + TournamentNewspaper.MaxExtraLines, lines.Count);
        Assert.Equal(NewspaperKind.Result, lines[0].Kind);
    }

    [Fact]
    public void RunnerHook_ReportsEveryBackgroundGame_WithoutChangingTheOutcome()
    {
        List<School> Field() => Enumerable.Range(0, 8)
            .Select(i => S(100 + i, $"架空{i}", 40 + (i * 37) % 45)).ToList();

        (string? Champion, List<BackgroundGameReport> Reports) Run(bool hooked)
        {
            var schools = Field();
            var bg = new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), new GameContext(), yearIndex: 1);
            var runner = new TournamentRunner(schools, schools[0], new NationCoefficients(), new Xoshiro256Random(42),
                new TournamentSchedule(), "テスト大会", playerResolver: null, backgroundResolver: bg);
            var reports = new List<BackgroundGameReport>();
            if (hooked) runner.OnBackgroundGame = reports.Add;
            while (!runner.Finished) runner.PlayNextPlayerMatch();
            runner.DrainBracketResolution();
            return (runner.ChampionName, reports);
        }

        var with = Run(hooked: true);
        var without = Run(hooked: false);

        Assert.Equal(without.Champion, with.Champion);
        Assert.NotEmpty(with.Reports);
        Assert.All(with.Reports, r => Assert.NotEqual(r.Away.Id, r.Home.Id));
    }
}
