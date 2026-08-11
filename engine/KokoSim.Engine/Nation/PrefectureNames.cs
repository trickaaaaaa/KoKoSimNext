namespace KokoSim.Engine.Nation;

/// <summary>
/// 47都道府県の名前（JIS順＝Prefecture.Id 0..46）。data/prefectures.yaml と同順で必ず一致させること。
/// ローマ字名は校名語彙 YAML の <c>places_by_prefecture</c> キーを Id へ解決するのに使い、
/// 日本語名は表示（県名・夏の地区名）に使う。
/// エンジンは YAML を読めない（不変条件#3）ため、この不変の対応表を定数として保持する。
/// バランス係数ではなく<b>変わらない事実</b>なので、YAML でなくここが正典でよい。
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

    /// <summary>
    /// JIS順の日本語名（「県」「都」「府」「道」まで含む）。添字がそのまま Prefecture.Id。
    /// <see cref="JisOrder"/> と同じ並び・同じ長さ（<c>PrefectureNamesTests</c> が固定する）。
    /// </summary>
    public static readonly IReadOnlyList<string> Japanese = new[]
    {
        "北海道", "青森県", "岩手県", "宮城県", "秋田県", "山形県", "福島県",
        "茨城県", "栃木県", "群馬県", "埼玉県", "千葉県", "東京都", "神奈川県",
        "新潟県", "富山県", "石川県", "福井県", "山梨県", "長野県",
        "岐阜県", "静岡県", "愛知県", "三重県",
        "滋賀県", "京都府", "大阪府", "兵庫県", "奈良県", "和歌山県",
        "鳥取県", "島根県", "岡山県", "広島県", "山口県",
        "徳島県", "香川県", "愛媛県", "高知県",
        "福岡県", "佐賀県", "長崎県", "熊本県", "大分県", "宮崎県", "鹿児島県", "沖縄県",
    };

    /// <summary>Id → 日本語名。範囲外は空文字（呼び出し側で落とさない）。</summary>
    public static string JapaneseOf(int id)
        => id >= 0 && id < Japanese.Count ? Japanese[id] : "";

    /// <summary>
    /// Id → 大会名に載せる短縮形（「選手権<b>神奈川</b>大会」の形）。末尾の「県／都／府」を落とす。
    /// <b>北海道だけは落とさない</b>（「北海」になってしまう）＝実在の大会名も「南北海道大会」等で
    /// 「北海道」を保つ。範囲外は空文字。
    /// </summary>
    public static string ShortOf(int id)
    {
        var name = JapaneseOf(id);
        if (name.Length <= 1 || name == "北海道") return name;
        var tail = name[name.Length - 1];
        return tail == '県' || tail == '都' || tail == '府' ? name.Substring(0, name.Length - 1) : name;
    }

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
