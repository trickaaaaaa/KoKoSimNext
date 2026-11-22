using KokoSim.Engine.Match.Game;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 天気の状態（<see cref="WeatherState"/>）→ 天気マークの写像（issue #1200）。ホーム画面（明るい紙色）と
    /// 試合中の電光掲示板（暗い黒島）の両方がここを単一ソースにする。返すのは USS のクラス名で、
    /// 画像（Assets/UI/Images/weather/）との対応は KokoSimTheme.uss の <c>.wx-icon--*</c> が持つ。
    /// </summary>
    public static class WeatherIcons
    {
        public static string UssClass(WeatherState state, bool darkBackground)
        {
            var kind = state switch
            {
                WeatherState.Rainy => "rainy",
                WeatherState.Cloudy => "cloudy",
                _ => "sunny",
            };
            return "wx-icon--" + kind + (darkBackground ? "-dark" : "-light");
        }

        /// <summary>天気マークの要素に付け得るクラス全部（付け替え時に先に外す用）。</summary>
        public static readonly string[] AllUssClasses =
        {
            "wx-icon--sunny-light", "wx-icon--cloudy-light", "wx-icon--rainy-light",
            "wx-icon--sunny-dark", "wx-icon--cloudy-dark", "wx-icon--rainy-dark",
        };

        /// <summary>要素に天気マークを出す（前のマークは外す）。</summary>
        public static void Apply(UnityEngine.UIElements.VisualElement icon, WeatherState state, bool darkBackground)
        {
            if (icon == null) return;
            foreach (var c in AllUssClasses) icon.RemoveFromClassList(c);
            icon.AddToClassList(UssClass(state, darkBackground));
        }
    }
}
