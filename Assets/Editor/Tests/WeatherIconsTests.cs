using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Match.Game;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>天気 → マークの写像（issue #1200）。3状態×明暗2背景が別々の画像に当たり、画像と USS が実在する。</summary>
    public class WeatherIconsTests
    {
        [TestCase(WeatherState.Sunny, false, "wx-icon--sunny-light")]
        [TestCase(WeatherState.Cloudy, false, "wx-icon--cloudy-light")]
        [TestCase(WeatherState.Rainy, false, "wx-icon--rainy-light")]
        [TestCase(WeatherState.Sunny, true, "wx-icon--sunny-dark")]
        [TestCase(WeatherState.Cloudy, true, "wx-icon--cloudy-dark")]
        [TestCase(WeatherState.Rainy, true, "wx-icon--rainy-dark")]
        public void UssClass_MapsStateAndBackground(WeatherState state, bool dark, string expected)
            => Assert.AreEqual(expected, WeatherIcons.UssClass(state, dark));

        [Test]
        public void EveryClass_HasImageFileAndUssRule()
        {
            var uss = File.ReadAllText("Assets/UI/Components/components.uss");
            var seen = new HashSet<string>();
            foreach (WeatherState s in System.Enum.GetValues(typeof(WeatherState)))
            foreach (var dark in new[] { false, true })
            {
                var cls = WeatherIcons.UssClass(s, dark);
                Assert.IsTrue(seen.Add(cls), "クラスが重複: " + cls);
                Assert.Contains(cls, WeatherIcons.AllUssClasses);
                var file = "weather-" + cls.Substring("wx-icon--".Length) + ".png";
                Assert.IsTrue(File.Exists("Assets/UI/Images/weather/" + file), "画像が無い: " + file);
                Assert.IsTrue(uss.Contains("." + cls + " ") && uss.Contains(file), "USS に規則が無い: " + cls);
            }
            Assert.AreEqual(WeatherIcons.AllUssClasses.Length, seen.Count);
        }
    }
}
