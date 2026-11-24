using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 体験版は2年目の夏が全部終わったところで止める（#1323・steam-roadmap 段階4）。範囲の判定そのものは
    /// engine の DemoScopeTests が見る。ここは週送りの入口が実際に止まるか（週が進まない・終了画面を頼む）を見る。
    /// </summary>
    public class DemoGateTests
    {
        private static readonly int Retire = new SeasonCalendar().SummerRetireWeek;

        [SetUp]
        public void NewGame()
        {
            SaveGameIO.ResetForNewGame();
            DemoGate.TakeEndScreenRequest();
        }

        [TearDown]
        public void Reset()
        {
            DemoGate.ForceInEditor = false;
            DemoGate.TakeEndScreenRequest();
        }

        private static void PutClock(int yearIndex, int week)
            => GameClock.Restore(new SaveState { YearIndex = yearIndex, Week = week });

        [Test]
        public void Demo_StopsBeforeTheWeekTheSummerEnds()
        {
            DemoGate.ForceInEditor = true;
            PutClock(2, Retire - 1);
            Assume.That(GameSession.Current.InTournament || GameSession.Current.HasSpectatedKoshien, Is.False);

            WeekAdvance.FromSideScreen(null);

            Assert.AreEqual((2, Retire - 1), (GameClock.YearIndex, GameClock.Week), "体験版の区切りを越えて週が進んだ");
            Assert.IsTrue(DemoGate.TakeEndScreenRequest(), "終了画面を頼んでいない");
        }

        [Test]
        public void Demo_StillAdvancesBeforeTheCutoff()
        {
            DemoGate.ForceInEditor = true;
            PutClock(2, Retire - 3);

            WeekAdvance.FromSideScreen(null);

            Assert.AreEqual((2, Retire - 2), (GameClock.YearIndex, GameClock.Week), "区切りの前なのに止まった");
            Assert.IsFalse(DemoGate.TakeEndScreenRequest());
        }

        [Test]
        public void FullVersion_IsNeverStopped()
        {
            PutClock(2, Retire - 1);
            Assert.IsFalse(DemoGate.StopBeforeAdvance, "製品版で体験版の区切りが効いている");

            WeekAdvance.FromSideScreen(null);

            Assert.AreEqual((2, Retire), (GameClock.YearIndex, GameClock.Week));
        }
    }
}
