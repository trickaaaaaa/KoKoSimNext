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
    }
}
