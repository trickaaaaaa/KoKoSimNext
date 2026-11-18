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

        [Test]
        public void WaitForResults_OfAnotherTournament_IsEmpty()
        {
            OtherDistrictBackgroundSim.Start(1001UL, OneDistrict(3));

            Assert.AreEqual(1, OtherDistrictBackgroundSim.WaitForResults(1001UL).Count);
            Assert.AreEqual(0, OtherDistrictBackgroundSim.WaitForResults(1002UL).Count,
                "前の大会の結果が今年の大会へ混ざった");
        }

        [Test]
        public void IsFor_TracksTheLatestStart()
        {
            OtherDistrictBackgroundSim.Start(2001UL, OneDistrict(1));
            OtherDistrictBackgroundSim.Start(2002UL, OneDistrict(1));

            Assert.IsFalse(OtherDistrictBackgroundSim.IsFor(2001UL));
            Assert.IsTrue(OtherDistrictBackgroundSim.IsFor(2002UL));
        }
    }
}
