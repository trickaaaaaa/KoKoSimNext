using System.Text;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 日本語の禁則（句読点・閉じ括弧を行頭に置かない／開き括弧を行末に置かない）を、リッチテキストの
    /// <c>&lt;nobr&gt;</c> で包んで守らせる（issue #1501）。
    ///
    /// <para>UI Toolkit の標準の組版は Panel Text Settings の禁則リスト（Leading/Following Characters）を見ない
    /// （2026-11-23 実測: リストを入れても「。」だけが次の行へ落ちた。要素ごとの <c>-unity-text-generator: advanced</c> も
    /// 効かなかった）。そこで、行頭に置けない文字は直前の1文字と、行末に置けない文字は直後の1文字と、
    /// それぞれ <c>&lt;nobr&gt;</c> でひと続きにする。折り返す文章を Label に入れる前に通す。</para>
    ///
    /// <para>既存のタグ（<c>&lt;b&gt;</c> 等）の中には手を入れない。<c>enableRichText</c> が切れた Label には使わない。</para>
    /// </summary>
    public static class Kinsoku
    {
        /// <summary>行頭に置かない文字（句読点・閉じ括弧・小書きかな・長音など）。</summary>
        public const string Leading =
            ")]}〕〉》」』】〙〗〟’”｠»ヽヾーァィゥェォッャュョヮヵヶぁぃぅぇぉっゃゅょゎゕゖ々〻‐゠–〜～？！?!‼⁇⁈⁉・：；:;、。，．,.…‥％%℃°）］｝";

        /// <summary>行末に置かない文字（開き括弧）。</summary>
        public const string Following = "([{〔〈《「『【〘〖〝‘“｟«（［｛";

        public static string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length + 16);
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '<')
                {
                    // 既存のタグはそのまま写す
                    var close = text.IndexOf('>', i);
                    if (close < 0) { sb.Append(text, i, text.Length - i); break; }
                    sb.Append(text, i, close - i + 1);
                    i = close + 1;
                    continue;
                }
                // ひと続きにする範囲 [i, end) を決める: 開き括弧の連なり＋本体1文字＋行頭禁止文字の連なり
                var end = i;
                while (end < text.Length && Following.IndexOf(text[end]) >= 0) end++;
                if (end < text.Length && text[end] != '<' && !char.IsWhiteSpace(text[end])) end++;
                var bodyEnd = end;
                while (end < text.Length && Leading.IndexOf(text[end]) >= 0) end++;
                if (end - i >= 2 && (end > bodyEnd || bodyEnd - i >= 2))
                {
                    sb.Append("<nobr>").Append(text, i, end - i).Append("</nobr>");
                    i = end;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }
    }
}
