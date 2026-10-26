using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace KokoSim.Engine.Saves.Json;

/// <summary>
/// セーブの見出し（最上位の数項目）だけを拾う読み取り器（issue #660）。
///
/// <para>スロット一覧は週・年・校名しか要らないのに、以前は 24MB の本文を丸ごと DOM へパースしていた
/// （Unity(Mono) で1回4秒・システム画面を開くと約10回）。ここでは<b>値を組み立てずに読み飛ばし</b>、
/// 欲しい最上位の項目だけ取り出す。<see cref="TextReader"/> から流して読むので、展開後の巨大な文字列も作らない。</para>
///
/// <para>読み飛ばしでも<b>文書全体の構文は確かめる</b>（括弧の対応・文字列の閉じ・リテラル・末尾の余り）。
/// 途中で打ち切ると、後半が壊れたセーブが一覧で「読める」と出てしまうため（従来の DOM パースと同じ判定を保つ）。
/// 数値の字面は <see cref="JsonParser"/> と同じ緩さ（数字・符号・小数点・指数の並び）で受ける。</para>
/// </summary>
internal sealed class JsonHeaderScanner
{
    private readonly TextReader _r;
    private readonly char[] _buf = new char[1 << 16];
    private int _len, _pos;

    private JsonHeaderScanner(TextReader r) => _r = r;

    /// <summary>
    /// 最上位オブジェクトを走査し、<paramref name="keys"/> に挙げたキーの値を返す（無ければ null）。
    /// 数値はトークン文字列、文字列は中身、それ以外（null・真偽・配列・オブジェクト）は <see cref="NonScalar"/>。
    /// 壊れた入力は <see cref="JsonParseException"/>。同じキーが二度あれば後勝ち（DOM の上書きと同じ）。
    /// </summary>
    public static object?[] Scan(TextReader reader, string[] keys)
    {
        var s = new JsonHeaderScanner(reader);
        var found = new object?[keys.Length];
        s.SkipWs();
        if (s.Next() != '{') throw new JsonParseException("ルートがオブジェクトでない");
        s.SkipWs();
        if (s.Peek() == '}') s.Next();
        else
        {
            while (true)
            {
                s.SkipWs();
                if (s.Peek() != '"') throw new JsonParseException("オブジェクトのキーは文字列");
                var key = s.ReadString(capture: true)!;
                s.SkipWs();
                if (s.Next() != ':') throw new JsonParseException("キーの後に ':' が無い");
                s.SkipWs();
                var want = Array.IndexOf(keys, key);
                if (want >= 0) found[want] = s.ReadScalarOrSkip();
                else s.SkipValue();
                s.SkipWs();
                var n = s.Next();
                if (n == '}') break;
                if (n != ',') throw new JsonParseException("',' または '}' が必要");
            }
        }
        s.SkipWs();
        if (s.Peek() != '\0') throw new JsonParseException("末尾に余分なトークン");
        return found;
    }

    /// <summary>スカラーでない値（null・真偽・配列・オブジェクト）の印。</summary>
    public static readonly object NonScalar = new();

    /// <summary>数値トークン（文字列と区別するための包み）。</summary>
    public sealed class NumberToken
    {
        public readonly string Text;
        public NumberToken(string text) => Text = text;
        public bool TryInt(out int v) => int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    private object ReadScalarOrSkip()
    {
        var c = Peek();
        if (c == '"') return ReadString(capture: true)!;
        if (c == '-' || (c >= '0' && c <= '9')) return new NumberToken(ReadNumber(capture: true)!);
        SkipValue();
        return NonScalar;
    }

    // 入れ子ごとに「オブジェクトか配列か」を覚えるスタック（再帰しない＝深い入れ子でもスタックを食わない）
    private readonly System.Collections.Generic.List<bool> _inObject = new();

    private void SkipValue()
    {
        var baseDepth = _inObject.Count;
        while (true)
        {
            // ── 値を1つ読む（開き括弧なら潜って最初の要素へ） ──
            SkipWs();
            var c = Peek();
            var opened = false;
            switch (c)
            {
                case '{':
                case '[':
                    Next();
                    SkipWs();
                    if (Peek() == (c == '{' ? '}' : ']')) { Next(); break; }   // 空
                    _inObject.Add(c == '{');
                    if (c == '{') ReadKeyColon();
                    opened = true;
                    break;
                case '"': ReadString(capture: false); break;
                case 't': Expect("true"); break;
                case 'f': Expect("false"); break;
                case 'n': Expect("null"); break;
                case '\0': throw new JsonParseException("値が来る前に入力終端");
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) { ReadNumber(capture: false); break; }
                    throw new JsonParseException($"予期しない文字 '{c}'");
            }
            if (opened) continue;

            // ── 値を読み終えた: 区切りなら次の値へ、閉じ括弧なら浮上する ──
            while (true)
            {
                if (_inObject.Count == baseDepth) return;
                SkipWs();
                var n = Next();
                var inObj = _inObject[_inObject.Count - 1];
                if (n == ',')
                {
                    if (inObj) { SkipWs(); ReadKeyColon(); }
                    break;
                }
                if (n == (inObj ? '}' : ']')) { _inObject.RemoveAt(_inObject.Count - 1); continue; }
                throw new JsonParseException("',' または閉じ括弧が必要");
            }
        }
    }

    private void ReadKeyColon()
    {
        if (Peek() != '"') throw new JsonParseException("オブジェクトのキーは文字列");
        ReadString(capture: false);
        SkipWs();
        if (Next() != ':') throw new JsonParseException("キーの後に ':' が無い");
    }

    private string? ReadString(bool capture)
    {
        Next(); // opening quote
        var sb = capture ? new StringBuilder() : null;
        while (true)
        {
            var c = NextRaw();
            if (c < 0) throw new JsonParseException("文字列が閉じられていない");
            if (c == '"') return sb?.ToString();
            if (c == '\\')
            {
                var e = NextRaw();
                if (e < 0) throw new JsonParseException("エスケープが不完全");
                switch ((char)e)
                {
                    case '"': sb?.Append('"'); break;
                    case '\\': sb?.Append('\\'); break;
                    case '/': sb?.Append('/'); break;
                    case 'b': sb?.Append('\b'); break;
                    case 'f': sb?.Append('\f'); break;
                    case 'n': sb?.Append('\n'); break;
                    case 'r': sb?.Append('\r'); break;
                    case 't': sb?.Append('\t'); break;
                    case 'u':
                        var code = 0;
                        for (var k = 0; k < 4; k++)
                        {
                            var h = NextRaw();
                            var d = h is >= '0' and <= '9' ? h - '0'
                                : h is >= 'a' and <= 'f' ? h - 'a' + 10
                                : h is >= 'A' and <= 'F' ? h - 'A' + 10 : -1;
                            if (d < 0) throw new JsonParseException("不正な \\u エスケープ");
                            code = code * 16 + d;
                        }
                        sb?.Append((char)code);
                        break;
                    default: throw new JsonParseException($"不正なエスケープ '\\{(char)e}'");
                }
            }
            else sb?.Append((char)c);
        }
    }

    private string? ReadNumber(bool capture)
    {
        // ⚠ `sb?.Append(Next())` と書かない: sb が null だと引数の Next() ごと評価されず、読み位置が進まない
        var sb = capture ? new StringBuilder() : null;
        if (Peek() == '-') { var m = Next(); sb?.Append(m); }
        while (true)
        {
            var c = Peek();
            if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
            {
                _pos++;
                sb?.Append(c);
            }
            else return sb?.ToString();
        }
    }

    private void Expect(string literal)
    {
        foreach (var ch in literal)
            if (NextRaw() != ch) throw new JsonParseException($"'{literal}' が必要");
    }

    private void SkipWs()
    {
        while (true)
        {
            var c = Peek();
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _pos++;
            else return;
        }
    }

    /// <summary>次の1字を覗く。終端は '\0'。</summary>
    private char Peek()
    {
        if (_pos >= _len && !Fill()) return '\0';
        return _buf[_pos];
    }

    private char Next()
    {
        var c = NextRaw();
        if (c < 0) throw new JsonParseException("入力終端");
        return (char)c;
    }

    private int NextRaw()
    {
        if (_pos >= _len && !Fill()) return -1;
        return _buf[_pos++];
    }

    private bool Fill()
    {
        _len = _r.Read(_buf, 0, _buf.Length);
        _pos = 0;
        return _len > 0;
    }
}
