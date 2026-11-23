using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// トップバーの週の表示を、直前に終わった大会の日付より前へ戻さない（issue #1534 項目9）。
    /// 7月16日に負けた後、大会明けの週送り（最終日が入る週までの切り捨て）で「7月2週」と出て逆戻りに見えた。
    /// </summary>
    public class TopBarDateMonotonicTests
    {
        [Test]
        public void WeekOfDate_CountsSevenDaysPerWeekWithinTheMonth()
        {
            var (m1, w1) = SeasonClock.MonthWeekOf(SeasonClock.WeekOfDate(7, 1));
            var (m3, w3) = SeasonClock.MonthWeekOf(SeasonClock.WeekOfDate(7, 16));
            Assert.AreEqual((7, 1), (m1, w1));
            Assert.AreEqual((7, 3), (m3, w3));
        }

        [Test]
        public void DisplayWeek_DoesNotGoBeforeTheLastTournamentDate()
        {
            var july2 = SeasonClock.WeekOfDate(7, 8);    // 7月2週
            var july3 = SeasonClock.WeekOfDate(7, 16);   // 7月3週
            Assert.AreEqual(july3, SeasonClock.DisplayWeek(1, july2, (1, 7, 16)));
            Assert.AreEqual(july3, SeasonClock.DisplayWeek(1, july3, (1, 7, 16)));
            Assert.AreEqual(july3 + 1, SeasonClock.DisplayWeek(1, july3 + 1, (1, 7, 16)), "時計が追い越したら時計のまま");
        }

        [Test]
        public void DisplayWeek_IgnoresFloorFromAnotherYearOrWithoutOne()
        {
            var july2 = SeasonClock.WeekOfDate(7, 8);
            Assert.AreEqual(july2, SeasonClock.DisplayWeek(2, july2, (1, 7, 16)));
            Assert.AreEqual(july2, SeasonClock.DisplayWeek(1, july2, null));
        }
    }
}
