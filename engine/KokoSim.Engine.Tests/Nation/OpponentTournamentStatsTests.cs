using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 相手校の今大会成績が「積まれているのに読めない」状態を塞ぐ検証（issue #634）。
/// 裏試合フルシム（#43）が積んだ値を、次に当たる相手校の学校IDで引けることを固定する。
/// </summary>
public sealed class OpponentTournamentStatsTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static List<School> MakePrefecture(int baseId, int count)
    {
        var schools = new List<School>(count);
        for (var i = 0; i < count; i++)
            schools.Add(new School
            {
                Id = baseId + i,
                // 自校（先頭）だけ突出させ、初戦で消えて NextOpponent が null になるのを防ぐ。
                Name = $"架空{baseId + i}",
                PrefectureId = 13,
                Strength = i == 0 ? 95 : 40 + (i * 37) % 30,
                Fame = 40 + (i * 13) % 30,
            });
        return schools;
    }

    private static (TournamentRunner Runner, NationTournamentStats Stats) Start(List<School> schools, ulong seed)
    {
        var stats = new NationTournamentStats();
        var bg = new BackgroundMatchResolver(
            new NationRosters(new AiRosterDeps()), new GameContext(), yearIndex: 1, stats: stats);
        var runner = new TournamentRunner(
            schools, schools[0], Coeff, new Xoshiro256Random(seed), Schedule, "テスト大会",
            playerResolver: null, backgroundResolver: bg);
        return (runner, stats);
    }

    private static int PlateAppearances(StatBook? book)
        => book is null ? 0 : book.Players.Values.Sum(p => p.Batting.PlateAppearances);

    [Fact]
    public void NextOpponent_HasPlateAppearancesFromTheRoundItAlreadyPlayed()
    {
        var schools = MakePrefecture(300, 8);
        var (runner, stats) = Start(schools, 7);

        runner.PlayNextPlayerMatch();   // 1回戦（自校のカード＋同ラウンドの裏カードを消化）

        var next = runner.NextOpponent;
        Assert.NotNull(next);           // 2回戦の相手＝1回戦を戦って勝ち上がってきた校

        // 症状の中心: この学校IDで引けなければ「成績がどこにも出ない」になる。
        var book = stats.ForSchool(next!.Id);
        Assert.NotNull(book);
        Assert.True(PlateAppearances(book) > 0, "1回戦を戦った相手校に打席が積まれていない");
    }

    [Fact]
    public void EverySchoolThatPlayed_HasBothScopes_AndCareerSurvivesTheNextTournament()
    {
        var schools = MakePrefecture(400, 8);
        var (runner, stats) = Start(schools, 11);
        while (!runner.Finished) runner.PlayNextPlayerMatch();
        runner.DrainBracketResolution();   // issue #529: 敗退後の残り消化は明示的に最後まで回す

        var played = stats.Schools.ToList();
        Assert.NotEmpty(played);
        foreach (var id in played)
        {
            Assert.True(PlateAppearances(stats.ForSchool(id)) > 0);
            // 通算は今大会と同じ値から始まる（この大会が初戦なので一致する）。
            Assert.Equal(PlateAppearances(stats.ForSchool(id)), PlateAppearances(stats.CareerForSchool(id)));
        }

        // 次の大会に入ると今大会だけ消え、通算は残る（相手校のHUDに出す「通算」の土台）。
        stats.StartTournament();
        foreach (var id in played)
        {
            Assert.Null(stats.ForSchool(id));
            Assert.True(PlateAppearances(stats.CareerForSchool(id)) > 0);
        }
    }

    [Fact]
    public void ManagerMatchOutcome_CarriesTheOpponentSchoolId()
    {
        var schools = MakePrefecture(500, 8);
        var (runner, _) = Start(schools, 7);

        var outcome = runner.PlayNextPlayerMatch();

        // 校名だけでは学校を同定できない＝自校戦の相手校側を全国成績へ畳むための帰属キー。
        Assert.Contains(schools, s => s.Id == outcome.OpponentId && s.Name == outcome.OpponentName);
        Assert.NotEqual(schools[0].Id, outcome.OpponentId);
    }
}
