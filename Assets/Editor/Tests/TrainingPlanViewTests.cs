using System.Linq;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using KokoSim.Unity.Training;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 練習画面のリデザイン（一覧＋配分のポップアップを1画面に）で ViewModel が出すもの。
    /// 伸びる能力は今の能力値と「この配分での1週の伸び」を1行に持ち、棒の長さは取りうる最大の伸びで満タン。
    /// </summary>
    public class TrainingPlanViewTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        private static TrainingPlanState OpenFirst()
        {
            var state = new TrainingPlanState();
            state.SelectPlayer(0);
            return state;
        }

        private static void ClearAll(TrainingPlanState state, TrainingPlanView v)
        {
            state.SetPreset(0, TrainingPreset.Custom);
            foreach (var s in v.SelectedSlots) state.SetMinutes(0, s.Menu, 0);
            foreach (var d in v.DefenseSlots) state.SetMinutes(0, d.Menu, 0);
        }

        [Test]
        public void Growth_ListsFielderElevenThenPitcherFive_WithoutPitchTypes()
        {
            var v = OpenFirst().BuildView();
            var fielder = v.SelectedGrowth.Where(g => !g.Pitcher).Select(g => g.Kind).ToArray();
            var pitcher = v.SelectedGrowth.Where(g => g.Pitcher).Select(g => g.Kind).ToArray();
            CollectionAssert.AreEqual(new[]
            {
                AbilityKind.LaunchTendency, AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline, AbilityKind.Bunt,
                AbilityKind.Speed, AbilityKind.Steal, AbilityKind.ArmStrength, AbilityKind.ThrowAccuracy,
                AbilityKind.Fielding, AbilityKind.Catching,
            }, fielder);
            CollectionAssert.AreEqual(new[]
            {
                AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank, AbilityKind.Stuff,
            }, pitcher);
        }

        [Test]
        public void Growth_LaunchAndVelocityAreText_OthersCarryRank()
        {
            var v = OpenFirst().BuildView();
            foreach (var g in v.SelectedGrowth)
            {
                var isText = g.Kind == AbilityKind.LaunchTendency || g.Kind == AbilityKind.Velocity;
                Assert.AreEqual(isText, g.Text.Length > 0, g.AbilityJp + " の文字表示");
                Assert.AreEqual(!isText, g.Grade.Length > 0, g.AbilityJp + " のランク");
            }
            StringAssert.EndsWith(" km/h", v.SelectedGrowth.First(g => g.Kind == AbilityKind.Velocity).Text);
        }

        [Test]
        public void Growth_FractionIsNormalized_AndZeroPlanGrowsNothing()
        {
            var state = OpenFirst();
            var v = state.BuildView();
            Assume.That(v.SelectedGrowth.Any(g => g.Grows), "練習できない週（既定の配分で何も伸びない）");
            foreach (var g in v.SelectedGrowth)
            {
                Assert.That(g.Fraction, Is.InRange(0.0, 1.0), g.AbilityJp);
                Assert.AreEqual(g.Grows, g.Fraction > 0, g.AbilityJp + " の棒と伸びの有無");
            }

            ClearAll(state, v);
            var empty = state.BuildView();
            Assert.AreEqual(0, empty.SelectedTotal);
            Assert.IsFalse(empty.SelectedGrowth.Any(g => g.Grows), "0分なのに伸びる能力がある");
        }

        [Test]
        public void Growth_FullBar_IsReachedBySomeSingleMenu()
        {
            var state = OpenFirst();
            var v = state.BuildView();
            Assume.That(v.SelectedGrowth.Any(g => g.Grows));
            var menus = v.SelectedSlots.Select(s => s.Menu).ToArray();
            var best = 0.0;
            foreach (var m in menus)
            {
                ClearAll(state, v);
                state.SetMinutes(0, m, v.Budget);
                var after = state.BuildView();
                Assert.AreEqual(v.Budget, after.SelectedTotal, m + " に全部注げない");
                foreach (var g in after.SelectedGrowth) best = System.Math.Max(best, g.Fraction);
            }
            // 満タン＝「どれか1つの練習に1日の全部を注いだときの、全能力の中で最大の伸び」。
            Assert.AreEqual(1.0, best, 1e-9);
        }

        [Test]
        public void Totals_SplitByGroup_AddUpToSelectedTotal()
        {
            var v = OpenFirst().BuildView();
            Assert.AreEqual(v.SelectedTotal, v.SelFielderMinutes + v.SelPitcherMinutes + v.SelDefenseMinutes);
            Assert.AreEqual(v.Budget - v.SelectedTotal, v.SelectedRemaining);
            Assert.AreEqual(9, v.DefenseSlots.Count);
            Assert.IsTrue(v.DefenseSlots.All(d => d.Grade.Length > 0 && d.FullJp.Length == 2));
            // 投手練習は実在の4つだけ（制球という練習は無い）。
            CollectionAssert.AreEqual(
                new[] { TrainingMenu.Pitching, TrainingMenu.BreakingBall, TrainingMenu.VelocityTraining, TrainingMenu.Running },
                v.SelectedSlots.Where(s => s.Pitcher).Select(s => s.Menu).ToArray());
        }

        [Test]
        public void Nominations_CarryPlayerDetails_AndCount()
        {
            var state = OpenFirst();
            Assume.That(state.ToggleNominate(0), Is.Not.EqualTo(NominationResult.Full));
            var v = state.BuildView();
            var slot = v.Nominations.First(n => n.Index == 0);
            Assert.AreEqual(RosterService.Active[0].Name, slot.Name);
            StringAssert.EndsWith("年", slot.GradeLabel);
            Assert.IsNotEmpty(slot.HandLabel);
            Assert.AreEqual(v.Nominations.Count(n => !n.Empty), v.NominatedCount);
        }

        [Test]
        public void ActiveTemplate_FollowsCurrentAllocation()
        {
            var state = OpenFirst();
            state.SetPreset(0, TrainingPreset.Custom);
            state.SaveTemplate();
            Assert.AreEqual("テンプレ1", state.BuildView().ActiveTemplate, "保存した直後は選択中");

            state.AdjustMinutes(0, TrainingMenu.Batting, -1);
            state.AdjustMinutes(0, TrainingMenu.Bunt, +1);
            var changed = state.BuildView();
            Assume.That(changed.SelectedSlots.First(s => s.Menu == TrainingMenu.Bunt).Minutes, Is.GreaterThan(0));
            Assert.AreEqual("", changed.ActiveTemplate, "配分を変えたら選択中ではない");

            state.ApplyTemplate(0, "テンプレ1");
            Assert.AreEqual("テンプレ1", state.BuildView().ActiveTemplate);
        }

        [Test]
        public void RowChips_HaveShortNames_AndAlternateDefenseShade()
        {
            var state = OpenFirst();
            state.SetPreset(0, TrainingPreset.Custom);
            var v = state.BuildView();
            ClearAll(state, v);
            state.SetMinutes(0, TrainingMenu.FlyBallHitting, 30);
            state.SetMinutes(0, TrainingMenu.DefenseLF, 30);
            state.SetMinutes(0, TrainingMenu.DefenseCF, 30);
            var chips = state.BuildView().Rows.First(r => r.Index == 0).Chips;
            CollectionAssert.AreEqual(new[] { "フライ", "左守", "中守" }, chips.Select(c => c.ShortJp).ToArray());
            CollectionAssert.AreEqual(new[] { false, false, true }, chips.Select(c => c.AltShade).ToArray());
        }
    }
}
