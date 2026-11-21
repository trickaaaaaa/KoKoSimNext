namespace KokoSim.Engine.Stats;

/// <summary>投球回の表記。アウト数 → "7 1/3" と、その表記を整数部と端数に割る処理を1箇所に置く。</summary>
public static class InningsFormat
{
    /// <summary>アウト数 → 投球回テキスト（例: 22 → "7 1/3"、21 → "7"）。</summary>
    public static string Text(int outs) => (outs / 3) + (outs % 3 == 0 ? "" : " " + (outs % 3) + "/3");

    /// <summary>
    /// 投球回テキストを整数部と端数に割る（"7 1/3" → ("7", "1/3")）。端数が無い・投球回でない文字列は
    /// 端数を空にして全体を整数部に返す。UI は端数だけ小さく組むために使う（⅓ ⅔ は字形が無い）。
    /// </summary>
    public static (string Whole, string Fraction) Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ("", "");
        var s = text.LastIndexOf(' ');
        if (s > 0 && s == text.Length - 4 && text[s + 2] == '/' && text[s + 3] == '3'
            && (text[s + 1] == '1' || text[s + 1] == '2'))
            return (text.Substring(0, s), text.Substring(s + 1));
        return (text, "");
    }
}
