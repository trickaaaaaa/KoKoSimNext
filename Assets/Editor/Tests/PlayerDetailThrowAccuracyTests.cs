using System.Linq;
using KokoSim.Unity.Players;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>選手詳細に野手能力「送球」が肩の直後・成長グラフの守備タブにも出る（issue #1165）。</summary>
    public class PlayerDetailThrowAccuracyTests
    {
        [Test]
        public void FielderBars_PutThrowAccuracyRightAfterArmStrength()
        {
            var v = new PlayerDetailState().BuildView(0);
            var labels = v.FielderAbilities.Select(b => b.Label).ToList();

            CollectionAssert.AreEqual(
                new[] { "弾道", "ミート", "パワー", "選球眼", "走力", "肩", "送球", "守備", "捕球" }, labels);
        }

        [Test]
        public void GrowthDefenseTab_HasThrowAccuracyLineWithDistinctColor()
        {
            var v = new PlayerDetailState().BuildView(0);
            var tab = v.GrowthTabs[2];

            Assert.AreEqual(new[] { "走力", "肩", "送球", "守備", "捕球" }, tab.Select(s => s.Label).ToArray());
            var colors = v.GrowthTabs.SelectMany(t => t).Select(s => s.ColorKey).ToList();
            Assert.AreEqual(colors.Count, colors.Distinct().Count(), "線の色キーが他の線とかぶらない");
        }
    }
}
