using System;
using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 他地区の秋季地区予選の背景フルシム（issue #1106）の受け渡し。
    /// 別の大会（前年・別スロット）の結果を今年へ混ぜないこと。
    /// </summary>
    public class OtherDistrictBackgroundSimTests
    {
        private static IReadOnlyList<KeyValuePair<int, Func<OtherDistrictResult>>> OneDistrict(int districtId)
            => new[]
            {
                new KeyValuePair<int, Func<OtherDistrictResult>>(districtId,
                    () => new OtherDistrictResult(Array.Empty<School>(), Array.Empty<OtherDistrictGame>())),
            };

        private ulong _lastKey;

        private void Begin(ulong key, int districtId)
        {
            _lastKey = key;
            OtherDistrictBackgroundSim.Start(key, OneDistrict(districtId));
        }

        // 背景の走りを残さない（次のテストや別の試験へ裏スレッドが食い込まないように、最後に始めた走りを待つ）。
        [TearDown]
        public void WaitForBackgroundRun()
        {
            if (_lastKey != 0UL) OtherDistrictBackgroundSim.WaitForResults(_lastKey);
            _lastKey = 0UL;
        }

        [Test]
        public void WaitForResults_OfAnotherTournament_IsEmpty()
        {
            Begin(1001UL, 3);

            Assert.AreEqual(1, OtherDistrictBackgroundSim.WaitForResults(1001UL).Count);
            Assert.AreEqual(0, OtherDistrictBackgroundSim.WaitForResults(1002UL).Count,
                "前の大会の結果が今年の大会へ混ざった");
        }

        [Test]
        public void IsFor_TracksTheLatestStart()
        {
            Begin(2001UL, 1);
            Begin(2002UL, 1);

            Assert.IsFalse(OtherDistrictBackgroundSim.IsFor(2001UL));
            Assert.IsTrue(OtherDistrictBackgroundSim.IsFor(2002UL));
        }
    }
}
