using System.Collections.Generic;
using System.Globalization;

namespace KokoSim.Engine.Match.Field;

/// <summary>
/// data/stadiums.yaml を <see cref="Stadium"/> の一覧へ変換する純パーサ（IO・依存なし＝不変条件#3。issue #905）。
/// Unity は YamlDotNet（<c>KokoSim.Config.StadiumCatalogLoader</c>）を持たないので、StreamingAssets に複製した
/// 同じ YAML をこれで読む。<b>解釈は Config 側のローダと1件残らず一致させる</b>
/// （<c>StadiumCatalogParserConsistencyTests</c> が実ファイルで固定する）。
///
/// 対応する構文は本ファイルが使う部分集合だけ:
///   - 全行コメント／行末コメント（<c>#</c>。引用符の中は除く）
///   - トップレベルの見出し（<c>stadiums:</c> / <c>prefecture_finals:</c>）の下のブロック・リスト
///   - 項目は <c>- id: …</c> で始まり、続く <c>key: value</c> 行がその項目に属する
///
/// 既定値は Config 側と同じ（right 省略＝left、fence_height 省略＝2.5m、center 省略＝118m、left 省略＝95m、
/// tier 省略＝県営級）。id の無い項目と未知の tier の項目は捨てる（Config 側は例外にする＝Unity は止めない）。
/// </summary>
public static class StadiumCatalogParser
{
    private static readonly HashSet<string> Sections = new() { "stadiums", "prefecture_finals" };

    /// <summary>読めた球場（YAML の並び順）。1件も読めなければ null（呼び元がフォールバックと警告を選ぶ）。</summary>
    public static IReadOnlyList<Stadium>? Parse(string yaml)
    {
        var list = new List<Stadium>();
        var seen = new HashSet<string>();
        Dictionary<string, string>? cur = null;
        var inSection = false;

        void Flush()
        {
            if (cur != null && TryBuild(cur, out var s) && seen.Add(s.Id)) list.Add(s);
            cur = null;
        }

        foreach (var raw in yaml.Replace("\r", "").Split('\n'))
        {
            var noComment = StripComment(raw);
            if (noComment.Trim().Length == 0) continue;

            // 字下げ無しの行＝トップレベルの見出し。対応する見出しの中だけ項目を拾う。
            if (noComment[0] != ' ' && noComment[0] != '-')
            {
                Flush();
                var head = noComment.Trim();
                inSection = head.EndsWith(":") && Sections.Contains(head.Substring(0, head.Length - 1).Trim());
                continue;
            }
            if (!inSection) continue;

            var line = noComment.Trim();
            if (line.StartsWith("-"))
            {
                Flush();
                cur = new Dictionary<string, string>();
                line = line.Substring(1).Trim();
                if (line.Length == 0) continue;
            }
            if (cur == null) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            cur[line.Substring(0, colon).Trim()] = Unquote(line.Substring(colon + 1).Trim());
        }
        Flush();
        return list.Count > 0 ? list : null;
    }

    private static bool TryBuild(Dictionary<string, string> map, out Stadium stadium)
    {
        stadium = null!;
        if (!map.TryGetValue("id", out var id) || id.Length == 0) return false;
        if (!TryTier(map.TryGetValue("tier", out var t) ? t : null, out var tier)) return false;
        var left = Num(map, "left", 95.0);
        stadium = new Stadium
        {
            Id = id,
            Name = map.TryGetValue("name", out var name) ? name : "",
            Tier = tier,
            LeftM = left,
            RightM = Num(map, "right", left),
            CenterM = Num(map, "center", 118.0),
            FenceHeightM = Num(map, "fence_height", 2.5),
            Wind = map.TryGetValue("wind", out var wind) && wind.Length > 0 ? wind : null,
            Model = map.TryGetValue("model", out var model) && model.Length > 0 ? model : null,
        };
        return true;
    }

    private static bool TryTier(string? text, out StadiumTier tier)
    {
        switch (text)
        {
            case "municipal": tier = StadiumTier.Municipal; return true;
            case "national": tier = StadiumTier.National; return true;
            case "prefectural": case null: case "": tier = StadiumTier.Prefectural; return true;
            default: tier = default; return false;
        }
    }

    private static double Num(Dictionary<string, string> map, string key, double fallback)
        => map.TryGetValue(key, out var text)
           && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private static string Unquote(string s)
        => s.Length >= 2 && (s[0] == '"' && s[s.Length - 1] == '"' || s[0] == '\'' && s[s.Length - 1] == '\'')
            ? s.Substring(1, s.Length - 2) : s;

    /// <summary>行末コメントを落とす（引用符の中の # は残す）。</summary>
    private static string StripComment(string line)
    {
        char quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
            if (ch == '"' || ch == '\'') { quote = ch; continue; }
            if (ch == '#') return line.Substring(0, i).TrimEnd();
        }
        return line.TrimEnd();
    }
}
