using KokoSim.Unity.Match;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 試合中の投手の球数表示（issue #1689）。BSO・投球チャートと同じ瞬間＝判定を見せた瞬間に1増え、
    /// 試合を終わらせた球も数える。engine の球数から「投げたがまだ判定を見せていない球」を引く式を固定する。
    /// </summary>
    public class ShownPitchCountTests
    {
        [Test]
        public void ThrownButNotCalled_IsNotCountedYet()
        {
            // 初球を投げて描いている最中（engine は1球・投球列1・判定まだ）＝0球
            Assert.AreEqual(0, MatchLiveController.ShownPitchTotal(engineTotal: 1, queued: 1, called: 0, fastMode: false));
        }

        [Test]
        public void CountsTheMomentThePitchIsCalled()
        {
            // 初球の判定を出した瞬間＝1球（次の球のリリースまで待たない）
            Assert.AreEqual(1, MatchLiveController.ShownPitchTotal(engineTotal: 1, queued: 1, called: 1, fastMode: false));
        }

        [Test]
        public void GameEndingPitch_IsCountedWhenCalled()
        {
            // それまで40球＋この打席の7球目で打席が確定（engine は確定ぶんに47球を吸収済み）。
            // 7球目の判定前は46、判定後は47で止まる（次の窓が来ない試合終了でも数える）
            Assert.AreEqual(46, MatchLiveController.ShownPitchTotal(engineTotal: 47, queued: 7, called: 6, fastMode: false));
            Assert.AreEqual(47, MatchLiveController.ShownPitchTotal(engineTotal: 47, queued: 7, called: 7, fastMode: false));
        }

        [Test]
        public void AtTheWindow_EverythingDrawnIsCounted()
        {
            // 采配窓（描き終えて次の球の前）＝engine の値そのまま
            Assert.AreEqual(53, MatchLiveController.ShownPitchTotal(engineTotal: 53, queued: 3, called: 3, fastMode: false));
            Assert.AreEqual(53, MatchLiveController.ShownPitchTotal(engineTotal: 53, queued: 0, called: 0, fastMode: false));
        }

        [Test]
        public void FastMode_ShowsEngineValue()
        {
            // 高速試合モードは投球を描かない＝引かない（残った投球列で永遠に遅れない）
            Assert.AreEqual(80, MatchLiveController.ShownPitchTotal(engineTotal: 80, queued: 4, called: 1, fastMode: true));
        }

        [Test]
        public void NeverNegative()
        {
            Assert.AreEqual(0, MatchLiveController.ShownPitchTotal(engineTotal: 0, queued: 2, called: 0, fastMode: false));
        }
    }
}
