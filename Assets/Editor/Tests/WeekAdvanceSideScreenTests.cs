using System.Linq;
using KokoSim.Engine.Season;
using KokoSim.Unity.Home;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// ホーム以外の画面の「今週を進める」（WeekAdvance.FromSideScreen）が、ホームと同じく週の中身
    /// （練習・フィード）まで回すことを固定する（issue #1113）。暦だけが進んで練習が飛んでいた。
    /// </summary>
    public class WeekAdvanceSideScreenTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        private static int AbilitySum()
            => RosterService.Active.Sum(p => AbilityKinds.All.Sum(k => p.Level(k)));

        [Test]
        public void FromSideScreen_AdvancesCalendarAndRunsPractice()
        {
            var week0 = GameClock.Week;
            var sum0 = AbilitySum();

            // 序盤の数週は練習期間。練習が回れば能力が伸びてフィードに「成長」が積まれる。
            for (var i = 0; i < 4; i++) WeekAdvance.FromSideScreen(null);

            Assert.Greater(GameClock.Week, week0, "暦が進む");
            Assert.Greater(AbilitySum(), sum0, "横の画面から進めても練習が効く（能力の経験値が増える）");
            Assert.IsTrue(HomeState.Current.BuildView().Feed.Any(f => f.When == "今週"), "週のフィードが積まれる");
        }

        /// <summary>
        /// 見せていないイベントがある間は、横の画面からさらに週を進めない（issue #1527）。進めると場面がホームに
        /// 行くまで溜まり続け、その間セーブもできなかった（システムタブで「今週を進める」→画面が変わらず週だけ進んだ）。
        /// </summary>
        [Test]
        public void FromSideScreen_DoesNotAdvanceWhileAnEventIsUnseen()
        {
            for (var i = 0; i < 60 && !EventService.HasPending; i++) WeekAdvance.FromSideScreen(null);
            Assume.That(EventService.HasPending, "60週のうちにイベントが1つも積まれなかった（前提が崩れた）");

            var week = GameClock.Week;
            var year = GameClock.YearIndex;
            WeekAdvance.FromSideScreen(null);

            Assert.AreEqual((year, week), (GameClock.YearIndex, GameClock.Week), "場面を見せる前に次の週へ進めない");
        }
    }
}
