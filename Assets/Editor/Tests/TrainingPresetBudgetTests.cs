using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using KokoSim.Unity.Training;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 練習画面でどのプリセットを押しても、配分欄の合計が練習時間（budget）を使い切ることを固定する（issue #1534）。
    /// 守備重視型が配分欄に行の無いメニュー（内野の汎用守備）を使っていて「残り 70/300分」と余って見えた。
    /// </summary>
    public class TrainingPresetBudgetTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        [TestCase(TrainingPreset.PitcherAuto)]
        [TestCase(TrainingPreset.BatterAuto)]
        [TestCase(TrainingPreset.Balanced)]
        [TestCase(TrainingPreset.DefenseFocus)]
        [TestCase(TrainingPreset.AceDevelopment)]
        [TestCase(TrainingPreset.SluggerDevelopment)]
        public void EveryPreset_FillsTheWholeBudget(TrainingPreset preset)
        {
            var state = new TrainingPlanState();
            state.SelectPlayer(0);
            state.SetPreset(0, preset);

            var v = state.BuildView();
            Assert.AreEqual(v.Budget, v.SelectedTotal, preset + " の配分欄の合計が練習時間に届かない");
            Assert.AreEqual(0, v.SelectedRemaining);
        }
    }
}
