using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KokoSim.Engine.Nation;

/// <summary>
/// data/team-colors.yaml を <see cref="TeamColorPalette"/> へ変換する純パーサ（IO・依存なし＝不変条件#3）。
/// <see cref="SchoolNameVocabParser"/> と同じ流儀で、Balance(Config層) と Unity が同一コードで解釈する。
///
/// 対応する構文は本ファイルが想定する部分集合のみ:
///   - 全行コメント／行末コメント（<c>#</c>。<b>引用符の中は除く</b>＝色が "#EDEBE0" なので必須）
///   - <c>palettes:</c> 見出しの下の <c>- { ... }</c> フロー・マップ（波括弧が閉じるまで複数行を連結）
///   - 入れ子のフロー・マップ（<c>stripe: { color: "#293356", freq: 40 }</c>）
///
/// 見出しは読まず、項目が持つキーで種類を見分ける（<c>hex+name</c>＝色名／<c>id</c>なし＋軸名＝
/// <c>axes:</c> の軸／それ以外＝パレット1件）。
/// </summary>
public static class TeamColorParser
{
    /// <summary>
    /// 解釈できた件数が0なら null を返す（＝呼び元がフォールバックと警告を選べる）。
    /// 1件でも読めたら、壊れた行は黙って捨てて残りを返す。
    /// </summary>
    public static TeamColorPalette? Parse(string yaml)
    {
        var entries = new List<TeamColorEntry>();
        var names = new Dictionary<RgbColor, string>();
        var axes = TeamColorAxes.Default;
        foreach (var block in FlowItems(yaml))
        {
            var map = ParseFlowMap(block);

            // 色名の定義（color_names: の項目）。id を持たないのでパレットの前に判別する
            if (map.TryGetValue("hex", out var nameHex) && map.TryGetValue("name", out var nameText)
                && RgbColor.TryParseHex(nameHex, out var named))
            {
                if (nameText.Length > 0) names[named] = nameText;
                continue;
            }

            // 配色の軸（axes: の項目・issue #621）。id を持たず軸の名前を持つことで見分ける
            // （パレット1件も stripe を持つが、そちらは必ず id を持つ）。
            if (!map.ContainsKey("id") &&
                (map.ContainsKey("visor") || map.ContainsKey("white_under") || map.ContainsKey("stripe")))
            {
                axes = new TeamColorAxes(
                    StripeRate: Rate(map, "stripe", axes.StripeRate),
                    VisorRate: Rate(map, "visor", axes.VisorRate),
                    WhiteUnderRate: Rate(map, "white_under", axes.WhiteUnderRate));
                continue;
            }

            if (!map.TryGetValue("id", out var id) || id.Length == 0) continue;
            if (!map.TryGetValue("base", out var baseHex) || !RgbColor.TryParseHex(baseHex, out var bas)) continue;
            if (!map.TryGetValue("team", out var teamHex) || !RgbColor.TryParseHex(teamHex, out var team)) continue;

            var visor = team;
            if (map.TryGetValue("visor", out var visorHex)) RgbColor.TryParseHex(visorHex, out visor);

            // 縞は「型」だけ（色・密度）。出すかどうかは校IDの軸が決める（issue #621）。
            TeamStripeStyle? stripe = null;
            if (map.TryGetValue("stripe", out var stripeBlock))
            {
                var s = ParseFlowMap(stripeBlock);
                var color = team;
                if (s.TryGetValue("color", out var colorHex)) RgbColor.TryParseHex(colorHex, out color);
                var freq = TeamStripeStyle.DefaultFreqPerMeter;
                if (s.TryGetValue("freq", out var freqText))
                    double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out freq);
                stripe = new TeamStripeStyle(color, freq);
            }

            map.TryGetValue("label", out var label);
            entries.Add(new TeamColorEntry(id, string.IsNullOrEmpty(label) ? id : label, bas, team, visor, stripe));
        }
        return entries.Count > 0 ? new TeamColorPalette(entries, names, axes) : null;
    }

    /// <summary>軸の出現率を読む（欠けていれば既定のまま＝表に書かなくても壊れない）。</summary>
    private static double Rate(Dictionary<string, string> map, string key, double fallback)
        => map.TryGetValue(key, out var text)
           && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    /// <summary>
    /// <c>- { ... }</c> を1件ずつ切り出す（波括弧の深さで判定＝複数行に跨って良い）。
    /// 先頭の <c>-</c> と外側の波括弧は落とし、中身だけ返す。
    /// </summary>
    private static IEnumerable<string> FlowItems(string yaml)
    {
        var buf = new StringBuilder();
        int depth = 0;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (depth == 0)
            {
                if (line[0] != '-') continue;              // palettes: 見出し等は読み飛ばす
                line = line.Substring(1).Trim();
                if (line.Length == 0 || line[0] != '{') continue;   // 対応するのはフロー・マップ形式だけ
            }
            foreach (var ch in line)
            {
                if (ch == '{') depth++;
                else if (ch == '}') depth--;
            }
            buf.Append(line).Append(' ');
            if (depth <= 0)
            {
                var text = buf.ToString().Trim();
                buf.Clear();
                depth = 0;
                if (text.Length >= 2 && text[0] == '{' && text[text.Length - 1] == '}')
                    yield return text.Substring(1, text.Length - 2);
            }
        }
    }

    /// <summary>
    /// フロー・マップの中身（<c>k: v, k: {..}</c>）を辞書へ。値は引用符を外した生文字列で、
    /// 入れ子マップは <c>{...}</c> のまま入れて呼び元が再帰的に解く。
    /// </summary>
    private static Dictionary<string, string> ParseFlowMap(string body)
    {
        var map = new Dictionary<string, string>();
        // 入れ子マップは値を "{...}" のまま持たせて再帰させるので、ここで外側の波括弧を外す
        body = body.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[body.Length - 1] == '}')
            body = body.Substring(1, body.Length - 2);
        int i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && (body[i] == ',' || char.IsWhiteSpace(body[i]))) i++;
            int keyStart = i;
            while (i < body.Length && body[i] != ':') i++;
            if (i >= body.Length) break;
            var key = body.Substring(keyStart, i - keyStart).Trim();
            i++;   // ':'
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;

            int valStart = i;
            if (i < body.Length && body[i] == '{')
            {
                int depth = 0;
                while (i < body.Length)
                {
                    if (body[i] == '{') depth++;
                    else if (body[i] == '}') { depth--; if (depth == 0) { i++; break; } }
                    i++;
                }
            }
            else if (i < body.Length && body[i] == '"')
            {
                i++;
                while (i < body.Length && body[i] != '"') i++;
                if (i < body.Length) i++;   // 閉じ引用符
            }
            else
            {
                while (i < body.Length && body[i] != ',') i++;
            }
            var value = body.Substring(valStart, i - valStart).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);
            if (key.Length > 0) map[key] = value;
        }
        return map;
    }

    /// <summary>
    /// 行末コメントを落とす。<b>引用符の中の <c>#</c> は色（"#EDEBE0"）なので残す</b>。
    /// </summary>
    private static string StripComment(string line)
    {
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') inQuote = !inQuote;
            else if (c == '#' && !inQuote && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line.Substring(0, i);
        }
        return line;
    }
}
