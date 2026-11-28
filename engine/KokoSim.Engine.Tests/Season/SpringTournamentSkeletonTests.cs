using System.Linq;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>春季大会①の骨格（春季大会計画 §2.1・§2.3）。まだ大会には入れない＝既存の暦の動きは変わらない。</summary>
public sealed class SpringTournamentSkeletonTests
{
    private readonly SeasonCalendar _cal = new();
    private readonly TournamentSchedule _schedule = new();

    [Fact]
    public void NewKinds_AreAppendedAtTheEnd_SoSavedIntsStayStable()
    {
        Assert.Equal(6, (int)TournamentKind.AutumnDistrict);
        Assert.Equal(7, (int)TournamentKind.Spring);
        Assert.Equal(8, (int)TournamentKind.SpringRegional);
    }

    [Fact]
    public void SpringStartWeek_SitsInAprilAfterEnrollmentWeek_AndOpeningDateFallsInThatWeek()
    {
        Assert.Equal(2, _cal.SpringTournamentStartWeek);
        Assert.True(_cal.SpringTournamentStartWeek > 0);
        Assert.True(_cal.SpringTournamentStartWeek < _cal.SummerTournamentStartWeek);
        var (month, day) = TournamentDate.OpeningOf(_schedule, TournamentKind.Spring);
        Assert.Equal(_cal.SpringTournamentStartWeek, _cal.WeekOf(month, day));
    }

    [Fact]
    public void Calendar_DoesNotStartOrAnnounceSpring_Yet()
    {
        for (var w = 0; w < _cal.WeeksPerYear; w++)
        {
            var started = _cal.TournamentStartingAt(w);
            Assert.NotEqual(TournamentKind.Spring, started);
            Assert.NotEqual(TournamentKind.SpringRegional, started);
            var next = _cal.NextTournament(w).Kind;
            Assert.True(next is TournamentKind.Summer or TournamentKind.Autumn, $"week {w}: {next}");
        }
        Assert.Null(_cal.TournamentStartingAt(_cal.SpringTournamentStartWeek));
    }

    [Fact]
    public void OpeningDates_AreAprilMidAndMay_AndRegionalComesAfterLongestPrefectureRun()
    {
        Assert.Equal((4, 16), TournamentDate.OpeningOf(_schedule, TournamentKind.Spring));
        Assert.Equal((5, 17), TournamentDate.OpeningOf(_schedule, TournamentKind.SpringRegional));
        Assert.Equal("4月16日", TournamentDate.Label(_schedule, TournamentKind.Spring, 0));
        Assert.Equal("5月17日", TournamentDate.Label(_schedule, TournamentKind.SpringRegional, 0));
        // 県大会は最大でも約3週間（21日）＝その最終日が地区大会の開幕より前。
        var last = TournamentDate.DateOf(_schedule, TournamentKind.Spring, 21);
        Assert.True(last.Month < 5 || (last.Month == 5 && last.Day < 17), $"{last.Month}/{last.Day}");
    }

    [Fact]
    public void ExistingOpeningDates_AreUnchanged()
    {
        Assert.Equal((7, 10), TournamentDate.OpeningOf(_schedule, TournamentKind.Summer));
        Assert.Equal((9, 12), TournamentDate.OpeningOf(_schedule, TournamentKind.Autumn));
        Assert.Equal((10, 18), TournamentDate.OpeningOf(_schedule, TournamentKind.Regional));
        Assert.Equal((11, 15), TournamentDate.OpeningOf(_schedule, TournamentKind.Jingu));
    }

    [Fact]
    public void Names_AreDefinedAndPrefecturalOnlyForTheSpringPrefectureTournament()
    {
        Assert.Equal("春", TournamentKindNames.Of(TournamentKind.Spring));
        Assert.Equal("春季地区大会", TournamentKindNames.Of(TournamentKind.SpringRegional));
        Assert.Equal("春の大会", TournamentKindNames.Short(TournamentKind.Spring));
        Assert.Equal("春季地区大会", TournamentKindNames.Short(TournamentKind.SpringRegional));
        Assert.True(TournamentKindNames.IsPrefectural(TournamentKind.Spring));
        Assert.False(TournamentKindNames.IsPrefectural(TournamentKind.SpringRegional));
    }

    [Theory]
    [InlineData(TournamentKind.Spring, 6, CrowdTurnout.Sparse)]
    [InlineData(TournamentKind.Spring, 3, CrowdTurnout.Sparse)]
    [InlineData(TournamentKind.Spring, 2, CrowdTurnout.Half)]
    [InlineData(TournamentKind.Spring, 1, CrowdTurnout.Half)]
    [InlineData(TournamentKind.SpringRegional, 4, CrowdTurnout.Half)]
    [InlineData(TournamentKind.SpringRegional, 1, CrowdTurnout.Half)]
    public void Crowd_SpringFollowsAutumnAndRegional(TournamentKind kind, int roundsRemaining, CrowdTurnout expected)
        => Assert.Equal(expected, CrowdTurnouts.For(kind, roundsRemaining));

    [Theory]
    [InlineData(TournamentKind.Spring, 5, 1)]
    [InlineData(TournamentKind.SpringRegional, 3, 1)]
    [InlineData(TournamentKind.Spring, 1, 3)]
    [InlineData(TournamentKind.SpringRegional, 1, 3)]
    public void Pressure_SpringIsQualifierLevel_FinalIsThree(TournamentKind kind, int roundsRemaining, int expected)
        => Assert.Equal(expected, TournamentPressure.StageBonus(kind, roundsRemaining));

    [Fact]
    public void Pressure_SpringIsNotARetirementOnTheLineTournament()
    {
        Assert.False(TournamentPressure.RetirementOnLine(TournamentKind.Spring));
        Assert.False(TournamentPressure.RetirementOnLine(TournamentKind.SpringRegional));
    }

    [Fact]
    public void SuccessAndBigStage_SpringPrefectureMatchesSummer_RegionalMatchesAutumnRegional()
    {
        var t = new TrainingCoefficients();
        Assert.Equal(t.MatchSuccessMultPrefectural, t.MatchSuccessStageMult(TournamentKind.Spring));
        Assert.Equal(t.MatchSuccessMultRegional, t.MatchSuccessStageMult(TournamentKind.SpringRegional));
        var g = new GrowthEventCoefficients();
        Assert.Equal(g.BigStageProb(TournamentKind.Summer), g.BigStageProb(TournamentKind.Spring));
        Assert.Equal(g.BigStageProb(TournamentKind.Regional), g.BigStageProb(TournamentKind.SpringRegional));
        Assert.True(g.BigStageProb(TournamentKind.Spring) > 0);
    }

    [Fact]
    public void ThirdPlaceMatch_IsNotPlayedInSpring()
    {
        Assert.False(ThirdPlaceRule.AppliesTo(TournamentKind.Spring));
        Assert.False(ThirdPlaceRule.AppliesTo(TournamentKind.SpringRegional));
    }

    [Fact]
    public void Weather_SpringUsesAprilAndMay()
    {
        Assert.Equal(WeatherModel.ForMonth(4).MeanTemperatureC, WeatherModel.ForTournament(_schedule, TournamentKind.Spring).MeanTemperatureC);
        Assert.Equal(WeatherModel.ForMonth(5).MeanTemperatureC, WeatherModel.ForTournament(_schedule, TournamentKind.SpringRegional).MeanTemperatureC);
    }

    [Fact]
    public void PitchDayStamp_IsDistinctPerKind()
    {
        var stamps = System.Enum.GetValues<TournamentKind>().Select(k => ScoutPromiseJudge.PitchDayStamp(1, k, 0)).ToList();
        Assert.Equal(stamps.Count, stamps.Distinct().Count());
    }
}
