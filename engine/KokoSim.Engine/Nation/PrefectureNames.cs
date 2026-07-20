namespace KokoSim.Engine.Nation;

/// <summary>
/// 47都道府県のローマ字名（JIS順＝Prefecture.Id 0..46）。data/prefectures.yaml と同順で必ず一致させること。
/// 校名語彙 YAML の <c>places_by_prefecture</c> キー（ローマ字名）を Id へ解決するのに使う。
/// エンジンは YAML を読めない（不変条件#3）ため、この不変の対応表を定数として保持する。
/// </summary>
public static class PrefectureNames
{
    /// <summary>JIS順のローマ字名。添字がそのまま Prefecture.Id。</summary>
    public static readonly IReadOnlyList<string> JisOrder = new[]
    {
        "hokkaido", "aomori", "iwate", "miyagi", "akita", "yamagata", "fukushima",
        "ibaraki", "tochigi", "gunma", "saitama", "chiba", "tokyo", "kanagawa",
        "niigata", "toyama", "ishikawa", "fukui", "yamanashi", "nagano",
        "gifu", "shizuoka", "aichi", "mie",
        "shiga", "kyoto", "osaka", "hyogo", "nara", "wakayama",
        "tottori", "shimane", "okayama", "hiroshima", "yamaguchi",
        "tokushima", "kagawa", "ehime", "kochi",
        "fukuoka", "saga", "nagasaki", "kumamoto", "oita", "miyazaki", "kagoshima", "okinawa",
    };

    /// <summary>ローマ字名 → Id。未知名は -1。</summary>
    public static int IdOf(string romaji)
    {
        for (var i = 0; i < JisOrder.Count; i++)
        {
            if (JisOrder[i] == romaji) return i;
        }
        return -1;
    }
}
