using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// ホームのチーム内ランキングは在籍中の部員（RosterService.Active）だけで順位をつける。
    /// 夏で引退した3年生・卒業生は通算成績が残っていても載らない（issue #1411）。
    /// </summary>
    public class TeamStatsRankingsTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        private static void Fold(int id, string name, int homeRuns)
        {
            var line = new BattingLine(1, FieldPosition.CenterField, name,
                PlateAppearances: 4, AtBats: 4, Hits: homeRuns, Doubles: 0, Triples: 0, HomeRuns: homeRuns,
                Rbi: homeRuns, Walks: 0, StrikeOuts: 0, SourceId: id);
            var result = new GameResult
            {
                AwayName = "相手", HomeName = "自校", AwayRuns = 0, HomeRuns = homeRuns,
                InningsPlayed = 9, TotalPitches = 100, PitcherChanges = 0,
                HomeBatting = new[] { line },
            };
            GameSession.Current.Stats.FoldGame(result, managerIsAway: false);
        }

        private static string[] HomeRunNames()
            => TeamStatsService.Rankings().Single(r => r.Label == "本塁打")
                .Top.Where(e => !e.Empty).Select(e => e.Name).ToArray();

        [Test]
        public void Rankings_ExcludeRetiredAndGraduated_KeepActive()
        {
            var senior = RosterService.Active.First(p => p.Grade >= 3);
            var junior = RosterService.Active.First(p => p.Grade < 3);
            Fold(senior.Id, senior.Name, 5);
            Fold(junior.Id, junior.Name, 2);

            CollectionAssert.AreEqual(new[] { senior.Name, junior.Name }, HomeRunNames(), "引退前は両方載る");

            senior.IsRetired = true;
            CollectionAssert.AreEqual(new[] { junior.Name }, HomeRunNames(), "夏の引退直後は引退者が載らない");

            RosterService.AdvanceYear(2);
            Assert.IsTrue(RosterService.Graduated.Contains(senior.Id), "前提: 卒業生として退避されている");
            CollectionAssert.AreEqual(new[] { junior.Name }, HomeRunNames(), "年度替わりの後も卒業生は載らない");
        }

        [Test]
        public void Rankings_ExcludeStatsOfPlayersNotOnThisRoster()
        {
            // 転任後は旧校の部員の通算が成績ストアに残るが、今の在籍に居ないので載らない（issue #730）。
            var junior = RosterService.Active.First();
            Fold(junior.Id, junior.Name, 1);
            Fold(999999, "旧校の主砲", 9);

            CollectionAssert.AreEqual(new[] { junior.Name }, HomeRunNames());
        }
    }
}
