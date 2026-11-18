using KokoSim.Engine.Match.Game;
using KokoSim.Unity.MatchResult;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>試合後の成績画面の投手欄: 被安打が PitchingLine.Hits のまま両校に載る（issue #1132）。</summary>
    public class MatchResultStateTests
    {
        [Test]
        public void PitchingRows_CarryHitsForBothSides()
        {
            var r = new GameResult
            {
                AwayName = "A", HomeName = "H", AwayRuns = 3, HomeRuns = 2, InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
                AwayPitching = new[] { new PitchingLine("先発", 27, 33, 7, 2, 5, 1, 110), new PitchingLine("継投", 3, 4, 2, 1, 0, 2, 20) },
                HomePitching = new[] { new PitchingLine("相手投手", 27, 36, 9, 3, 8, 4, 130) },
            };

            var v = MatchResultState.Build(r, managerIsAway: true, "A", "H");

            Assert.AreEqual(2, v.Own.Pitching.Count);
            Assert.AreEqual("7", v.Own.Pitching[0].Hits);
            Assert.AreEqual("2", v.Own.Pitching[1].Hits);
            Assert.AreEqual("9", v.Opponent.Pitching[0].Hits);
            Assert.AreEqual("2", v.Own.Pitching[0].Runs);
            Assert.AreEqual("1", v.Own.Pitching[0].Walks);
        }
    }
}
