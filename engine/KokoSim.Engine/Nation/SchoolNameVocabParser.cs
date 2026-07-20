using System.Globalization;
using System.Text;

namespace KokoSim.Engine.Nation;

/// <summary>
/// data/school-names.yaml を <see cref="SchoolNameVocab"/> へ変換する純パーサ（IO・依存なし＝不変条件#3）。
/// エンジンに置くことで Balance(Config層) と Unity が同一コードで解釈し、決定論を保証する。
/// 対応する構文は本ファイルが想定する単純な部分集合のみ:
///   - 全行コメント（先頭が <c>#</c>）と空行は無視
///   - <c>key: [a, b, c]</c> のフロー・リスト（複数行に跨って良い＝括弧が閉じるまで連結）
///   - <c>key: 0.30</c> のスカラ（private_ratio）
///   - <c>places_by_prefecture:</c> 直下の2字下げ <c>name: [..]</c>（name はローマ字県名 or 数値Id）
/// </summary>
public static class SchoolNameVocabParser
{
    public static SchoolNameVocab Parse(string yaml)
    {
        var d = new SchoolNameVocab();
        var lines = JoinFlowLines(yaml);

        List<string>? publicSuffixes = null, privateStems = null, privateSuffixes = null, placePrefixes = null;
        double? privateRatio = null;
        var places = new Dictionary<int, IReadOnlyList<string>>();

        string? currentTop = null;
        foreach (var line in lines)
        {
            var indent = LeadingSpaces(line);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            var colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            var key = trimmed.Substring(0, colon).Trim();
            var value = trimmed.Substring(colon + 1).Trim();

            if (indent == 0)
            {
                currentTop = key;
                if (value.Length == 0) continue; // マップ見出し（places_by_prefecture:）。中身は下の行。

                switch (key)
                {
                    case "public_suffixes": publicSuffixes = ParseList(value); break;
                    case "private_stems": privateStems = ParseList(value); break;
                    case "private_suffixes": privateSuffixes = ParseList(value); break;
                    case "place_prefixes": placePrefixes = ParseList(value); break;
                    case "private_ratio":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                            privateRatio = r;
                        break;
                }
            }
            else if (currentTop == "places_by_prefecture")
            {
                var id = int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
                    ? numeric
                    : PrefectureNames.IdOf(key);
                if (id < 0) continue; // 未知の県名は無視（フォールバックに委ねる）
                var list = ParseList(value);
                if (list.Count > 0) places[id] = list;
            }
        }

        return new SchoolNameVocab
        {
            PlacePrefixes = placePrefixes ?? d.PlacePrefixes,
            PublicSuffixes = publicSuffixes ?? d.PublicSuffixes,
            PrivateStems = privateStems ?? d.PrivateStems,
            PrivateSuffixes = privateSuffixes ?? d.PrivateSuffixes,
            PrivateRatio = privateRatio ?? d.PrivateRatio,
            PlacesByPrefecture = places,
        };
    }

    /// <summary>括弧が閉じるまで後続行を連結し、フロー・リストを1論理行にまとめる（コメント/空行は除去）。</summary>
    private static List<string> JoinFlowLines(string yaml)
    {
        var result = new List<string>();
        var buffer = new StringBuilder();
        var depth = 0;

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (depth == 0 && (t.Length == 0 || t[0] == '#')) continue;

            if (depth == 0)
            {
                buffer.Clear();
                buffer.Append(line);
            }
            else
            {
                // 継続行（リストの続き）。行頭インデントは捨て、1スペースで繋ぐ。
                buffer.Append(' ').Append(t);
            }

            depth += CountUnquoted(line, '[') - CountUnquoted(line, ']');
            if (depth <= 0)
            {
                depth = 0;
                result.Add(buffer.ToString());
            }
        }
        if (buffer.Length > 0 && depth > 0) result.Add(buffer.ToString()); // 未閉鎖でも取りこぼさない
        return result;
    }

    private static int CountUnquoted(string s, char c)
    {
        var n = 0;
        foreach (var ch in s) if (ch == c) n++;
        return n;
    }

    private static int LeadingSpaces(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }

    /// <summary><c>[a, b, c]</c>（または裸の <c>a, b, c</c>）を要素へ分解。空要素は捨てる。</summary>
    private static List<string> ParseList(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("[")) v = v.Substring(1);
        if (v.EndsWith("]")) v = v.Substring(0, v.Length - 1);

        var items = new List<string>();
        foreach (var part in v.Split(','))
        {
            var item = part.Trim().Trim('"', '\'');
            if (item.Length > 0) items.Add(item);
        }
        return items;
    }
}
