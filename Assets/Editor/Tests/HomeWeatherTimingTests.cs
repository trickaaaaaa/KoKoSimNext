using KokoSim.Engine.Season;
using KokoSim.Unity.Home;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 大会中のホームの天気欄は、試合当日（ReachedMatchDay）になって初めて出す（issue #1199）。
    /// 試合前の日に出すと、外れない予報が何日も前から見えてしまう。
    /// </summary>
    public class HomeWeatherTimingTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        [TearDown]
        public void Cleanup() => SaveGameIO.ResetForNewGame();

        [Test]
        public void WeatherField_IsEmptyBeforeMatchDay_AndShownOnMatchDay()
        {
            TournamentEntry.Enter(TournamentKind.Summer);
            var s = GameSession.Current;
            Assume.That(s.InTournament && !s.Runner.Finished, "前提: 大会に入って次戦がある");
            Assume.That(!MatchStadium.IsIndoor(s.Kind, s.Runner.NextMatchRoundsRemaining, NationService.PrefectureId),
                "前提: 屋内球場は天気欄を出さない（#1020）");

            var daysBefore = 0;
            while (!s.ReachedMatchDay)
            {
                var v = HomeState.Current.BuildView();
                Assert.IsEmpty(v.WeatherLabel, "試合前（あと" + s.DaysUntilNextMatch + "日）は天気を出さない");
                Assert.IsEmpty(v.WeatherTempValue);
                Assert.IsEmpty(v.WeatherTempUnit);
                daysBefore++;
                s.AdvanceDay();
                Assert.Less(daysBefore, 100, "試合日に到達しない");
            }

            var day = HomeState.Current.BuildView();
            Assert.IsNotEmpty(day.WeatherLabel, "試合当日は天気を出す");
            Assert.IsNotEmpty(day.WeatherTempValue);
            Assert.AreEqual("℃", day.WeatherTempUnit);
        }
    }
}
