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
/// ブロック形式のネストは扱わない（scenarios.yaml をこの書式に保つこと）。</para>
/// </summary>
public static class ScenarioYamlParser
{
    public static ScenarioCatalog Parse(string yaml)
    {
        var list = new List<ScenarioDefinition>();
        var fields = new Dictionary<string, string>();
        var inScenarios = false;

        void Flush()
        {
            if (fields.Count == 0) return;
            list.Add(Build(fields));
            fields.Clear();
        }

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw);
            if (line.Trim().Length == 0) continue;

            if (LeadingSpaces(line) == 0)
            {
                // トップレベルの見出し。scenarios: 以外の節が来たら現在の収集を打ち切る。
                Flush();
                inScenarios = line.Trim().StartsWith("scenarios:", System.StringComparison.Ordinal);
                continue;
            }
            if (!inScenarios) continue;

            var body = line.Trim();
            if (body.StartsWith("- ", System.StringComparison.Ordinal))
            {
                Flush();                      // 新しいエントリの先頭
                body = body.Substring(2).Trim();
            }

            var colon = IndexOfTopLevelColon(body);
            if (colon < 0) continue;
            var key = body.Substring(0, colon).Trim();
            var value = body.Substring(colon + 1).Trim();
            if (key.Length > 0) fields[key] = value;
        }
        Flush();

        return list.Count == 0 ? ScenarioCatalog.Empty : new ScenarioCatalog(list);
    }

    private static ScenarioDefinition Build(Dictionary<string, string> f)
    {
        if (!f.TryGetValue("id", out var id) || Unquote(id).Length == 0)
            throw new System.FormatException("scenarios.yaml に id のないシナリオがあります。");
        id = Unquote(id);

        var away = FlowMap(Get(f, "away"));
        var home = FlowMap(Get(f, "home"));
        var count = FlowMap(Get(f, "count"));
        var rules = FlowMap(Get(f, "modern_rules"));

        return new ScenarioDefinition
        {
            Id = id,
            Name = Unquote(Get(f, "name")) is { Length: > 0 } n ? n : id,
            Away = Unquote(MapOr(away, "school", "AI:tier=C")),
            Home = Unquote(MapOr(home, "school", "player")),
            AwayScore = Int(MapOr(away, "score", "0"), 0),
            HomeScore = Int(MapOr(home, "score", "0"), 0),
            Inning = Int(Get(f, "inning"), 1),
            Top = Bool(Get(f, "top"), true),
            Outs = Int(Get(f, "outs"), 0),
            Bases = FlowSequence(Get(f, "bases")),
            Balls = Int(MapOr(count, "balls", "0"), 0),
            Strikes = Int(MapOr(count, "strikes", "0"), 0),
            Batter = Int(Get(f, "batter"), 1),
            PitcherFatigue = Int(Get(f, "pitcher_fatigue"), 0),
            Dh = NullableBool(MapOr(rules, "dh", "")),
            TieBreak = NullableBool(MapOr(rules, "tiebreak", "")),
            Seed = ULong(Get(f, "seed")),
            Force = Unquote(Get(f, "force")) is { Length: > 0 } fo ? fo : null,
        };
    }

    private static string Get(Dictionary<string, string> f, string key) => f.TryGetValue(key, out var v) ? v : "";

    private static string MapOr(Dictionary<string, string> map, string key, string fallback)
        => map.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    /// <summary><c>{a: 1, b: "x"}</c> を辞書へ。中括弧が無ければ空。</summary>
    private static Dictionary<string, string> FlowMap(string value)
    {
        var map = new Dictionary<string, string>();
        var s = value.Trim();
        if (s.Length < 2 || s[0] != '{' || s[s.Length - 1] != '}') return map;
        foreach (var part in s.Substring(1, s.Length - 2).Split(','))
        {
            var colon = part.IndexOf(':');
            if (colon < 0) continue;
            map[part.Substring(0, colon).Trim()] = part.Substring(colon + 1).Trim();
        }
        return map;
    }

    /// <summary><c>[1, 2, 3]</c> を整数配列へ。角括弧が無ければ空。</summary>
    private static IReadOnlyList<int> FlowSequence(string value)
    {
        var s = value.Trim();
        if (s.Length < 2 || s[0] != '[' || s[s.Length - 1] != ']') return System.Array.Empty<int>();
        var inner = s.Substring(1, s.Length - 2).Trim();
        if (inner.Length == 0) return System.Array.Empty<int>();
        var list = new List<int>();
        foreach (var part in inner.Split(','))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) list.Add(v);
        }
        return list;
    }

    private static int Int(string s, int fallback)
        => int.TryParse(Unquote(s), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static ulong? ULong(string s)
        => ulong.TryParse(Unquote(s), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool Bool(string s, bool fallback) => NullableBool(s) ?? fallback;

    private static bool? NullableBool(string s) => Unquote(s).ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    };

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
