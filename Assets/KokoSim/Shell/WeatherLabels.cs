using KokoSim.Engine.Match.Game;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 天気の状態（<see cref="WeatherState"/>）の表示用テキスト（issue #803）。ホーム画面の対戦カード・
    /// 試合中の電光掲示板の両方がここを単一ソースにする（値は engine の天気プレビュー／
    /// <see cref="GameContext.ResolvedWeather"/> からそのまま読み、ここでは文字に変換するだけ）。
    /// </summary>
    public static class WeatherLabels
    {
        public static string Jp(WeatherState state) => state switch
        {
            WeatherState.Rainy => "雨",
            WeatherState.Cloudy => "曇",
            _ => "晴",
        };
    }
}
