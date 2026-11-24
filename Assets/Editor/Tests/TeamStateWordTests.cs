using KokoSim.Engine.Match.Game;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>結束 U → 「チーム状態」の言葉5段の写像（issue #1600）。境目は 85／70／50／30。70・30 は係数と一致。</summary>
    public class TeamStateWordTests
    {
        [TestCase(100.0, "めっちゃええ感じ")]
        [TestCase(85.0, "めっちゃええ感じ")]
        [TestCase(84.9, "ええ感じ")]
        [TestCase(70.0, "ええ感じ")]
        [TestCase(69.9, "まあまあ")]
        [TestCase(50.0, "まあまあ")]
        [TestCase(49.9, "ちょっとギクシャク")]
        [TestCase(30.0, "ちょっとギクシャク")]
        [TestCase(29.9, "バラバラ")]
        [TestCase(0.0, "バラバラ")]
        public void Word_AtBoundaries(double u, string expected)
            => Assert.AreEqual(expected, ChemistryDisplay.TeamStateWord(u));

        [Test]
        public void Boundaries70And30_FollowCoefficients()
        {
            var c = new CohesionCoefficients();
            Assert.AreEqual(TeamStateLevel.Good, ChemistryDisplay.TeamStateOf(c.HighThreshold, c));
            Assert.AreEqual(TeamStateLevel.Normal, ChemistryDisplay.TeamStateOf(c.HighThreshold - 0.01, c));
            Assert.AreEqual(TeamStateLevel.Bad, ChemistryDisplay.TeamStateOf(c.LowThreshold, c));
            Assert.AreEqual(TeamStateLevel.Worst, ChemistryDisplay.TeamStateOf(c.LowThreshold - 0.01, c));
        }

        [Test]
        public void GoodWords_MatchWhereMatchEffectStarts()
        {
            var c = new CohesionCoefficients();
            Assert.AreEqual(1, Cohesion.RattledThresholdStep(c.HighThreshold, c));
            Assert.AreEqual(-1, Cohesion.RattledThresholdStep(c.LowThreshold - 0.01, c));
            Assert.AreEqual(0, Cohesion.RattledThresholdStep(c.LowThreshold, c));
        }
    }
}
