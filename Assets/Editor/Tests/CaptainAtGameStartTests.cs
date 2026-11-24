using System.Linq;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 1年目の主将は新規開始で選ぶ（オーナー判断 2026-11-24）。1年目は全員1年生で夏に誰も引退しないので、
    /// 夏の後に「新チーム始動」の主将指名を出さず、始めに選んだ主将が続く。
    /// </summary>
    public class CaptainAtGameStartTests
    {
        [SetUp]
        public void SetUp()
        {
            SaveGameIO.StartNewSchool("舞鶴", NationService.DefaultPrefectureId, null, 42UL);
        }

        [TearDown]
        public void TearDown() => SaveGameIO.ResetForNewGame();

        [Test]
        public void NewGame_AsksForCaptain_AndYear1SummerKeepsIt_WithoutAskingAgain()
        {
            NewTeamService.OpenAtGameStart();
            Assert.IsTrue(NewTeamService.AtGameStart);

            var view = NewTeamService.BuildView();
            Assert.IsTrue(view.AtGameStart);
            Assert.AreEqual("", view.RetiredLabel, "始めの指名では「0 名が引退」を出さない");
            Assert.Greater(view.Candidates.Count, 1);

            // 自動選出の暫定主将ではない選手を選ぶ
            var pick = view.Candidates.First(c => !c.IsInterim);
            Assert.IsTrue(NewTeamService.Confirm(pick.ActiveIndex), "期間外（4月）でも始めの指名は受け付ける");
            Assert.IsFalse(NewTeamService.Pending);
            Assert.IsFalse(UniformNumberService.Pending, "始めの指名の後は背番号決定へ進まない");
            var captain = RosterService.Active[pick.ActiveIndex];
            Assert.IsTrue(captain.IsCaptain);

            // 1年目の夏の県大会で敗退＝誰も引退しない
            Assert.IsTrue(SummerRetirementService.OnTournamentEnded(TournamentKind.Summer, isChampion: false));
            Assert.IsFalse(NewTeamService.Pending, "誰も引退しない夏は主将指名を出さない");
            Assert.IsTrue(NewTeamService.IsStartedThisYear, "第18週の安全網に二度目の発足をさせない");
            Assert.IsTrue(captain.IsCaptain, "始めに選んだ主将が続く");
            Assert.AreEqual(1, RosterService.Roster.Count(p => p.IsCaptain));
        }
    }
}
