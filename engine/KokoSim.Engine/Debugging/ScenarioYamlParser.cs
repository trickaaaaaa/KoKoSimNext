using System.Collections.Generic;
using System.Globalization;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// <c>data/debug/scenarios.yaml</c> を <see cref="ScenarioCatalog"/> へ変換する純パーサ
/// （IO・外部依存なし＝不変条件#3）。<see cref="Nation.SchoolNameVocabParser"/> と同じ方針で
/// <b>engine に置く</b>: Unity は YamlDotNet を持たないため、Balance(Config層) と Unity が
/// 同一コードで解釈することを型で保証する（片方だけ解釈がズレる事故を構造的に潰す）。
///
/// <para>対応する構文は本ファイルが想定する部分集合のみ:
/// 全行コメント（<c>#</c>）と空行は無視 / <c>scenarios:</c> 直下の <c>- key: value</c> 連なり /
/// <c>key: {a: 1, b: x}</c> のフロー・マップ / <c>key: [1, 2, 3]</c> のフロー・シーケンス。
/// ブロック形式のネストは扱わない（scenarios.yaml をこの書式に保つこと）。
/// 範囲外の値・知らないキー・読めない値は黙って既定値にせずエラーにする。</para>
/// </summary>
public static class ScenarioYamlParser
{
    private static readonly HashSet<string> EntryKeys = new()
    {
        "id", "name", "away", "home", "inning", "top", "outs", "bases", "count", "batter",
        "pitcher_fatigue", "pitcher_rattled", "modern_rules", "seed", "force",
    };
    private static readonly string[] TeamKeys = { "school", "score" };
    private static readonly string[] CountKeys = { "balls", "strikes" };
    private static readonly string[] RulesKeys = { "dh", "tiebreak" };

    /// <summary>
    /// 読めない書き方は<b>黙って既定値にせず</b> <see cref="System.FormatException"/>（または Validate の
    /// <see cref="System.ArgumentException"/>）で止める（知らないキー・数値でない値・範囲外の bases・ブロック形式のネスト・
    /// 未知の force／チーム指定）。シナリオの誤りを読み込み時に見つけるため。
    /// </summary>
    public static ScenarioCatalog Parse(string yaml)
    {
        var list = new List<ScenarioDefinition>();
        var fields = new Dictionary<string, string>();
        var inScenarios = false;
        var inEntry = false;

        void Flush()
        {
            if (fields.Count > 0) list.Add(Build(fields));
            fields.Clear();
            inEntry = false;
        }

        var lineNo = 0;
        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = StripComment(raw);
            if (line.Trim().Length == 0) continue;

            var body = line.Trim();
            // YAML では「scenarios:」直下に字下げなしの「- id:」を置いてもよい＝先頭が「-」ならトップレベルの見出しではない。
            var isEntryStart = body == "-" || body.StartsWith("- ", System.StringComparison.Ordinal);
            if (LeadingSpaces(line) == 0 && !(inScenarios && isEntryStart))
            {
                Flush();
                var heading = body.EndsWith(":", System.StringComparison.Ordinal) ? body.Substring(0, body.Length - 1).Trim() : body;
                if (heading != "scenarios")
                    throw new System.FormatException($"scenarios.yaml {lineNo}行目: 未知のトップレベル節です（scenarios: だけ）: {body}");
                inScenarios = true;
                continue;
            }

            if (isEntryStart)
            {
                Flush();                      // 新しいエントリの先頭
                inEntry = true;
                body = body.Substring(1).Trim();
                if (body.Length == 0) continue;
            }
            else if (!inEntry)
            {
                throw new System.FormatException($"scenarios.yaml {lineNo}行目: 「- 」で始まるエントリの外にある行です: {body}");
            }

            var colon = IndexOfTopLevelColon(body);
            if (colon < 0)
                throw new System.FormatException($"scenarios.yaml {lineNo}行目: 「キー: 値」の形ではありません: {body}");
            var key = body.Substring(0, colon).Trim();
            var value = body.Substring(colon + 1).Trim();
            if (key.Length == 0)
                throw new System.FormatException($"scenarios.yaml {lineNo}行目: キーが空です: {body}");
            if (value.Length == 0)
                throw new System.FormatException(
                    $"scenarios.yaml {lineNo}行目: {key} の値が空です（ブロック形式のネストは未対応。{{ a: 1, b: 2 }} の1行で書く）");
            if (fields.ContainsKey(key))
                throw new System.FormatException($"scenarios.yaml {lineNo}行目: キー {key} が重複しています");
            fields[key] = value;
        }
        Flush();

        return list.Count == 0 ? ScenarioCatalog.Empty : new ScenarioCatalog(list);
    }

    private static ScenarioDefinition Build(Dictionary<string, string> f)
    {
        if (!f.TryGetValue("id", out var id) || Unquote(id).Length == 0)
            throw new System.FormatException("scenarios.yaml に id のないシナリオがあります。");
        id = Unquote(id);

        foreach (var key in f.Keys)
        {
            if (!EntryKeys.Contains(key))
                throw new System.FormatException($"シナリオ {id}: 未知のキー {key}（{string.Join(" / ", EntryKeys)}）");
        }

        var away = FlowMap(id, "away", Get(f, "away"), TeamKeys);
        var home = FlowMap(id, "home", Get(f, "home"), TeamKeys);
        var count = FlowMap(id, "count", Get(f, "count"), CountKeys);
        var rules = FlowMap(id, "modern_rules", Get(f, "modern_rules"), RulesKeys);

        var def = new ScenarioDefinition
        {
            Id = id,
            Name = Unquote(Get(f, "name")) is { Length: > 0 } n ? n : id,
            Away = Unquote(MapOr(away, "school", "AI:tier=C")),
            Home = Unquote(MapOr(home, "school", "player")),
            AwayScore = Int(id, "away.score", MapOr(away, "score", ""), 0),
            HomeScore = Int(id, "home.score", MapOr(home, "score", ""), 0),
            Inning = Int(id, "inning", Get(f, "inning"), 1),
            Top = Bool(id, "top", Get(f, "top"), true),
            Outs = Int(id, "outs", Get(f, "outs"), 0),
            Bases = FlowSequence(id, Get(f, "bases")),
            Balls = Int(id, "count.balls", MapOr(count, "balls", ""), 0),
            Strikes = Int(id, "count.strikes", MapOr(count, "strikes", ""), 0),
            Batter = Int(id, "batter", Get(f, "batter"), 1),
            PitcherFatigue = Int(id, "pitcher_fatigue", Get(f, "pitcher_fatigue"), 0),
            PitcherRattled = Bool(id, "pitcher_rattled", Get(f, "pitcher_rattled"), false),
            Dh = NullableBool(id, "modern_rules.dh", MapOr(rules, "dh", "")),
            TieBreak = NullableBool(id, "modern_rules.tiebreak", MapOr(rules, "tiebreak", "")),
            Seed = ULong(id, Get(f, "seed")),
            Force = Unquote(Get(f, "force")) is { Length: > 0 } fo ? fo : null,
        };
        def.Validate();
        return def;
    }

    private static string Get(Dictionary<string, string> f, string key) => f.TryGetValue(key, out var v) ? v : "";

    private static string MapOr(Dictionary<string, string> map, string key, string fallback)
        => map.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    /// <summary>
    /// <c>{a: 1, b: "x"}</c> を辞書へ。値が無ければ空。中括弧が無い・許可外のキー・「キー: 値」でない要素は例外。
    /// 区切りの <c>,</c> は引用符の中では分けない。
    /// </summary>
    private static Dictionary<string, string> FlowMap(string id, string field, string value, string[] allowed)
    {
        var map = new Dictionary<string, string>();
        var s = value.Trim();
        if (s.Length == 0) return map;
        if (s.Length < 2 || s[0] != '{' || s[s.Length - 1] != '}')
            throw new System.FormatException($"シナリオ {id}: {field} は {{ key: value, ... }} の形で書く: {value}");
        var inner = s.Substring(1, s.Length - 2);
        if (inner.Trim().Length == 0) return map;
        foreach (var part in SplitTopLevelCommas(inner))
        {
            var colon = part.IndexOf(':');
            if (colon < 0)
                throw new System.FormatException($"シナリオ {id}: {field} の要素が「キー: 値」の形ではありません: {part.Trim()}");
            var key = part.Substring(0, colon).Trim();
            if (System.Array.IndexOf(allowed, key) < 0)
                throw new System.FormatException($"シナリオ {id}: {field} に未知のキー {key}（{string.Join(" / ", allowed)}）");
            if (map.ContainsKey(key))
                throw new System.FormatException($"シナリオ {id}: {field} のキー {key} が重複しています");
            map[key] = part.Substring(colon + 1).Trim();
        }
        return map;
    }

    /// <summary>引用符の外の <c>,</c> で分ける。</summary>
    private static IEnumerable<string> SplitTopLevelCommas(string s)
    {
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == ',')
            {
                yield return s.Substring(start, i - start);
                start = i + 1;
            }
        }
        yield return s.Substring(start);
    }

    /// <summary><c>[1, 2, 3]</c> を整数配列へ。値が無ければ空。角括弧が無い・数値でない要素は例外。</summary>
    private static IReadOnlyList<int> FlowSequence(string id, string value)
    {
        var s = value.Trim();
        if (s.Length == 0) return System.Array.Empty<int>();
        if (s.Length < 2 || s[0] != '[' || s[s.Length - 1] != ']')
            throw new System.FormatException($"シナリオ {id}: bases は [1, 2, 3] の形で書く: {value}");
        var inner = s.Substring(1, s.Length - 2).Trim();
        if (inner.Length == 0) return System.Array.Empty<int>();
        var list = new List<int>();
        foreach (var part in inner.Split(','))
        {
            if (!int.TryParse(Unquote(part), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                throw new System.FormatException($"シナリオ {id}: bases の要素が整数ではありません: '{part.Trim()}'");
            list.Add(v);
        }
        return list;
    }

    private static int Int(string id, string what, string s, int fallback)
    {
        if (s.Trim().Length == 0) return fallback;
        if (!int.TryParse(Unquote(s), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new System.FormatException($"シナリオ {id}: {what} は整数で書く: {s}");
        return v;
    }

    private static ulong? ULong(string id, string s)
    {
        if (s.Trim().Length == 0) return null;
        if (!ulong.TryParse(Unquote(s), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new System.FormatException($"シナリオ {id}: seed は0以上の整数で書く: {s}");
        return v;
    }

    private static bool Bool(string id, string what, string s, bool fallback) => NullableBool(id, what, s) ?? fallback;

    private static bool? NullableBool(string id, string what, string s)
    {
        if (s.Trim().Length == 0) return null;
        switch (Unquote(s).ToLowerInvariant())
        {
            case "true" or "yes" or "on": return true;
            case "false" or "no" or "off": return false;
            default: throw new System.FormatException($"シナリオ {id}: {what} は true / false で書く: {s}");
        }
    }

    private static string Unquote(string s)
    {
        var t = s.Trim();
        if (t.Length >= 2 && ((t[0] == '"' && t[t.Length - 1] == '"') || (t[0] == '\'' && t[t.Length - 1] == '\'')))
            return t.Substring(1, t.Length - 2);
        return t;
    }

    /// <summary>行末コメントを落とす（引用符の中の <c>#</c> は残す）。</summary>
    private static string StripComment(string line)
    {
        var inQuote = false;
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuote)
            {
                if (c == quote) inQuote = false;
            }
            else if (c is '"' or '\'')
            {
                inQuote = true;
                quote = c;
            }
            else if (c == '#')
            {
                // "a #b" のように空白の後の # だけをコメント扱いにする（値中の # を壊さない）。
                if (i == 0 || char.IsWhiteSpace(line[i - 1])) return line.Substring(0, i);
            }
        }
        return line;
    }

    /// <summary>フロー・マップ/シーケンスの内側を無視して、キーの区切りコロンを探す。</summary>
    private static int IndexOfTopLevelColon(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    private static int LeadingSpaces(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }
}
