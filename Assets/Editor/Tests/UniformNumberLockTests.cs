using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Unity.Member;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 大会中（自校が残っている間）は背番号・ベンチ入りを変えられない。大会の前・自校が負けたあと・大会と大会の間は変えられる
    /// （issue #1749・設計書05 §1.2）。
    /// </summary>
    public class UniformNumberLockTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        private static School Sch(int id, double strength) => new School { Id = id, Name = "校" + id, PrefectureId = 0, Strength = strength };

        private static (TournamentRunner runner, List<School> field) NewRunner(double managerStrength, double othersStrength)
        {
            var field = new List<School> { Sch(1, managerStrength) };
            for (var i = 0; i < 7; i++) field.Add(Sch(100 + i, othersStrength));
            var r = new TournamentRunner(field, field[0], new NationCoefficients(), new Xoshiro256Random(42),
                new TournamentSchedule(), "テスト大会");
            return (r, field);
        }

        private static int[] Numbers() => RosterService.Active.Select(p => p.UniformNumber).ToArray();

        private static void TryAllFourOperations(MemberSettingState state)
        {
            var roster = RosterService.Active;
            var benchedOut = roster.Select((p, i) => (p, i)).First(t => t.p.UniformNumber == 0).i;
            state.ClickSlot(1);          // 枠から掴む
            state.ClickPool(benchedOut); // 入れ替え
            state.ClickSlot(1);
            state.ClickSlot(2);          // 置く
            state.ClearSlot(3);          // 外す
            state.AutoAssign();          // 自動で振る
        }

        [Test]
        public void BeforeTournament_AllFourOperationsWork()
        {
            var roster = RosterService.Active;
            Assume.That(roster.Count(p => p.UniformNumber == 0), Is.GreaterThan(0));
            var state = new MemberSettingState();
            Assert.IsFalse(state.Locked);
            var before = Numbers();
            state.ClearSlot(3);
            Assert.AreNotEqual(before, Numbers(), "大会の前は外せるはず");
        }

        [Test]
        public void DuringTournament_FourOperationsChangeNothing()
        {
            var (runner, field) = NewRunner(99, 30);
            GameSession.Current.EnterTournament(TournamentKind.Summer, "テスト大会", runner, field);
            Assert.IsTrue(GameSession.Current.UniformNumbersLocked);
            var state = new MemberSettingState();
            Assert.IsTrue(state.Locked);
            Assume.That(RosterService.Active.Any(p => p.UniformNumber == 0));
            var before = Numbers();

            TryAllFourOperations(state);

            Assert.AreEqual(before, Numbers(), "大会中に背番号が変わった");
            Assert.IsTrue(state.BuildView().Locked);
        }

        [Test]
        public void AfterManagerLoses_OperationsWorkAgain()
        {
            var (runner, field) = NewRunner(1, 99);
            GameSession.Current.EnterTournament(TournamentKind.Summer, "テスト大会", runner, field);
            while (!runner.Finished) runner.PlayNextPlayerMatch();

            Assert.IsFalse(GameSession.Current.UniformNumbersLocked);
            var state = new MemberSettingState();
            var before = Numbers();
            state.ClearSlot(3);
            Assert.AreNotEqual(before, Numbers());
        }

        [Test]
        public void EnteringTournament_FillsVacantBenchSlotsFirst()
        {
            var roster = RosterService.Active;
            Assume.That(roster.Count, Is.GreaterThan(UniformNumberAssigner.BenchSize));
            new MemberSettingState().ClearSlot(5);
            Assert.IsFalse(roster.Any(p => p.UniformNumber == 5));

            var (runner, field) = NewRunner(99, 30);
            GameSession.Current.EnterTournament(TournamentKind.Summer, "テスト大会", runner, field);

            Assert.AreEqual(UniformNumberAssigner.BenchSize, roster.Count(p => p.UniformNumber > 0), "ベンチ入りが20人そろっていない");
            Assert.IsTrue(UniformNumberAssigner.Validate(roster), "背番号が重複している");
        }

        [Test]
        public void AfterLoadRestore_StaysLocked()
        {
            var (runner, field) = NewRunner(99, 30);
            GameSession.Current.EnterTournament(TournamentKind.Summer, "テスト大会", runner, field);
            var saved = new TournamentSaveState
            {
                Kind = TournamentKind.Summer,
                Title = "テスト大会",
                TournamentDay = 0,
                FieldSchoolIds = field.Select(s => s.Id).ToList(),
                Runner = runner.CaptureState(),
            };
            GameSession.Current.ResetForNewGame();
            Assert.IsFalse(GameSession.Current.UniformNumbersLocked);

            GameSession.Current.RestoreTournament(saved, runner, field);

            Assert.IsTrue(GameSession.Current.UniformNumbersLocked);
            var before = Numbers();
            new MemberSettingState().AutoAssign();
            Assert.AreEqual(before, Numbers());
        }
    }
}
