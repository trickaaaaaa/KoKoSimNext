using System;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// セーブ一覧の週表示がトップバーと同じになる（issue #1528）。
    /// 内部の週は0基点（0＝4月1週）なので、生の値を出すと「0週」になっていた。
    /// トップバー（ScoreboardStrip）と同じ <see cref="SeasonClock.CompactLabel"/> を通す。
    /// </summary>
    public class SaveSlotWeekLabelTests
    {
        private static SaveSlotInfo Info(int yearIndex, int week)
            => new SaveSlotInfo("manual1", exists: true, corrupt: false, week, yearIndex, DateTime.MinValue);

        [TestCase(1, 0, "4月1週")]
        [TestCase(1, 3, "4月4週")]
        [TestCase(1, 4, "5月1週")]
        [TestCase(2, 38, "1月1週")]
        public void WeekLabel_MatchesTopBar(int yearIndex, int week, string expected)
        {
            var info = Info(yearIndex, week);
            Assert.AreEqual(expected, info.WeekLabel);
            Assert.AreEqual(SeasonClock.CompactLabel(yearIndex, week), info.WeekLabel);
        }

        [TestCase(1, 0, "1/4月1週")]
        [TestCase(1, 3, "1/4月4週")]
        [TestCase(3, 22, "3/9月1週")]
        public void ListLabel_IsYearSlashTopBarWeek(int yearIndex, int week, string expected)
            => Assert.AreEqual(expected, Info(yearIndex, week).ListLabel);
    }
}
