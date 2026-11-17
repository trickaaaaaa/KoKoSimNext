using KokoSim.Unity.Audio;
using NUnit.Framework;
using UnityEngine;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// Edit mode で効果音を鳴らそうとしても、スピーカーを作らず何も溜めない。
    /// 溜めると、次に再生ボタンを押した瞬間に打球音・捕球音がまとめて鳴っていた（2026-11-17）。
    /// </summary>
    public class MatchSfxEditModeTests
    {
        [Test]
        public void EditMode_PlayCreatesNoAudioSource()
        {
            int before = Resources.FindObjectsOfTypeAll<AudioSource>().Length;

            MatchSfx.PlayBat(1f, false);
            MatchSfx.PlayCatch(140f);
            MatchSfx.PlayCheer();

            Assert.AreEqual(before, Resources.FindObjectsOfTypeAll<AudioSource>().Length);
        }
    }
}
