using KokoSim.Engine.Nation;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>自校の試合の士気は、画面のチーム状態と同じ結束 U を見る（issue #1603）。監督の信頼度はもう入力ではない。</summary>
    public class ManagerTeamMoraleTests
    {
        [SetUp]
        public void SetUp() => SaveGameIO.StartNewSchool("舞鶴", NationService.DefaultPrefectureId, null, 42UL);

        [TearDown]
        public void TearDown() => SaveGameIO.ResetForNewGame();

        [Test]
        public void Morale_IsCohesion_NotTrust()
        {
            ManagerService.Manager.Trust = 5.0;
            var team = PlayerMatchResolver.BuildManagerTeam(NationService.ManagerSchoolName);
            Assert.IsTrue(team.Morale.HasValue);
            Assert.AreEqual(ChemistryDisplay.CohesionOf(team), team.Morale.Value, 1e-9);

            ManagerService.Manager.Trust = 95.0;
            var again = PlayerMatchResolver.BuildManagerTeam(NationService.ManagerSchoolName);
            Assert.AreEqual(team.Morale.Value, again.Morale.Value, 1e-9, "信頼度を変えても士気は動かない");
        }
    }
}
