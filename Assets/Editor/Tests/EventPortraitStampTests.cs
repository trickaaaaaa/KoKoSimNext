using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Season.Events;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>会話の立ち絵は積んだ時点のマネージャーで話す（入れ替えをまたいでも変わらない・issue #1737）。</summary>
    public class EventPortraitStampTests
    {
        [SetUp]
        public void SetUp() => ManagerService.Reset();

        [TearDown]
        public void TearDown() => ManagerService.Reset();

        private static EventScript Script() => new EventScript(new List<EventLine>
        {
            new EventLine(EventRoles.TeamManager, "", "負けてしまいましたね"),
            new EventLine(EventRoles.TeamManagerOutgoing, "", "ありがとう"),
            new EventLine(EventRoles.Player, "", "はい"),
        });

        [Test]
        public void LineStampsPortraitAtResolveTime_SurvivesSuccession()
        {
            var m = ManagerService.Manager;
            m.TeamManagerPortrait = 4;
            m.TeamManagerGrade = 3;
            var lines = EventService.ResolveLines(Script(), new Dictionary<string, string>());

            TeamManagerLifecycle.Succeed(m, new KokoSim.Engine.Core.Xoshiro256Random(1UL));

            Assert.AreEqual(4, lines[0].Portrait, "入れ替え前に積んだ会話は引退する側の絵のまま");
            Assert.AreNotEqual(4, m.TeamManagerPortrait);

            var after = EventService.ResolveLines(Script(), new Dictionary<string, string>());
            Assert.AreEqual(m.TeamManagerPortrait, after[0].Portrait, "入れ替え後に積んだ会話は新しいマネージャー");
            Assert.AreEqual(4, after[1].Portrait, "引退する側は控えた番号");
            Assert.AreEqual(0, after[2].Portrait, "マネージャー以外は控え無し");
        }

        [Test]
        public void ExplicitPortrait_OverridesCurrent()
        {
            ManagerService.Manager.TeamManagerPortrait = 2;
            var lines = EventService.ResolveLines(Script(), new Dictionary<string, string>(), teamManagerPortrait: 7);
            Assert.AreEqual(7, lines[0].Portrait);
        }
    }
}
