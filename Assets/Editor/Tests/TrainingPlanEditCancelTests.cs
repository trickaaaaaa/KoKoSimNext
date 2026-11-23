using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using KokoSim.Unity.Training;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 練習設定のモーダルは「決定」を押したときだけ変更を残し、×・暗幕・Esc で閉じたら開いた時点へ戻す（issue #1534）。
    /// モーダルの操作は共有ロスターへ即時に書くので、開いた時点の控え（BeginEdit）へ戻す形で取り消す。
    /// </summary>
    public class TrainingPlanEditCancelTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        [Test]
        public void CancelEdit_RestoresPlansDelegationAndNominations()
        {
            var state = new TrainingPlanState();
            var roster = RosterService.Active;
            Assume.That(roster.Count, Is.GreaterThanOrEqualTo(2));
            var plan0 = roster[0].Plan;
            var plan1 = roster[1].Plan;
            var nom0 = roster[0].IndividualCoaching;

            state.SelectPlayer(0);
            state.BeginEdit();
            state.SetPreset(0, TrainingPreset.DefenseFocus);
            state.ToggleNominate(0);
            state.SelectPlayer(1);   // モーダルの「次 ▶」で別の選手も触る
            state.AdjustMinutes(1, TrainingMenu.Batting, +3);
            state.ToggleDelegate(1);
            state.CancelEdit();

            Assert.AreSame(plan0, roster[0].Plan, "1人目の計画が開いた時点に戻らない");
            Assert.AreSame(plan1, roster[1].Plan, "途中で切り替えた2人目の計画が戻らない");
            Assert.AreEqual(nom0, roster[0].IndividualCoaching, "個別指導の指名が戻らない");
            Assert.IsFalse(state.IsDelegated(1), "委任が戻らない");
        }

        [Test]
        public void CommitEdit_KeepsChanges()
        {
            var state = new TrainingPlanState();
            var roster = RosterService.Active;

            state.SelectPlayer(0);
            state.BeginEdit();
            state.SetPreset(0, TrainingPreset.DefenseFocus);
            state.CommitEdit();
            state.CancelEdit();   // 決定の後に閉じても戻さない

            Assert.AreEqual(TrainingPreset.DefenseFocus, roster[0].Plan?.Preset);
        }
    }
}
