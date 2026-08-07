using System;
using System.Globalization;
using System.Text;

namespace KokoSim.Engine.Saves.Json;

/// <summary>
/// 最小の再帰下降 JSON パーサ（issue #507）。外部依存なし。数値は<b>トークン文字列のまま</b>
/// <see cref="JsonNumber"/> に格納し、型変換は取り出し側に委ねる（往復で丸めない）。
/// 壊れた入力は <see cref="JsonParseException"/> を投げる（呼び出し側＝<see cref="SaveCodec"/> が
/// 版チェックの前段でこれを捕まえ、例外で落ちず「読めないセーブ」として弾く）。
/// </summary>
internal sealed class JsonParser
{
    private readonly string _s;
    private int _i;

    private JsonParser(string s) => _s = s;

    public static JsonValue Parse(string text)
    {
        if (text is null) throw new JsonParseException("入力が null");
        var p = new JsonParser(text);
        p.SkipWhitespace();
        var v = p.ParseValue();
        p.SkipWhitespace();
        if (p._i != p._s.Length) throw new JsonParseException($"末尾に余分なトークン（位置 {p._i}）");
        return v;
    }

    private JsonValue ParseValue()
    {
        SkipWhitespace();
        if (_i >= _s.Length) throw new JsonParseException("値が来る前に入力終端");
        var c = _s[_i];
        switch (c)
        {
            case '{': return ParseObject();
            case '[': return ParseArray();
            case '"': return new JsonString(ParseString());
            case 't': Expect("true"); return new JsonBool(true);
            case 'f': Expect("false"); return new JsonBool(false);
            case 'n': Expect("null"); return JsonNull.Instance;
            default:
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                throw new JsonParseException($"予期しない文字 '{c}'（位置 {_i}）");
        }
    }

    private JsonObject ParseObject()
    {
        var obj = new JsonObject();
        _i++; // '{'
        SkipWhitespace();
        if (Peek() == '}') { _i++; return obj; }
        while (true)
        {
            SkipWhitespace();
            if (Peek() != '"') throw new JsonParseException($"オブジェクトのキーは文字列（位置 {_i}）");
            var key = ParseString();
            SkipWhitespace();
            if (Peek() != ':') throw new JsonParseException($"キーの後に ':' が無い（位置 {_i}）");
            _i++;
            var val = ParseValue();
            obj.Add(key, val);
            SkipWhitespace();
            var n = Next();
            if (n == '}') break;
            if (n != ',') throw new JsonParseException($"',' または '}}' が必要（位置 {_i}）");
        }
        return obj;
    }

    private JsonArray ParseArray()
    {
        var arr = new JsonArray();
        _i++; // '['
        SkipWhitespace();
        if (Peek() == ']') { _i++; return arr; }
        while (true)
        {
            arr.Add(ParseValue());
            SkipWhitespace();
            var n = Next();
            if (n == ']') break;
            if (n != ',') throw new JsonParseException($"',' または ']' が必要（位置 {_i}）");
        }
        return arr;
    }

    private string ParseString()
    {
        _i++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) throw new JsonParseException("文字列が閉じられていない");
            var c = _s[_i++];
            if (c == '"') break;
            if (c == '\\')
            {
                if (_i >= _s.Length) throw new JsonParseException("エスケープが不完全");
                var e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length) throw new JsonParseException("\\u エスケープが不完全");
                        var hex = _s.Substring(_i, 4);
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw new JsonParseException($"不正な \\u エスケープ: {hex}");
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default: throw new JsonParseException($"不正なエスケープ '\\{e}'");
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private JsonNumber ParseNumber()
    {
        var start = _i;
        if (Peek() == '-') _i++;
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
            else break;
        }
        return new JsonNumber(_s.Substring(start, _i - start));
    }

    private void Expect(string literal)
    {
        if (_i + literal.Length > _s.Length || _s.Substring(_i, literal.Length) != literal)
            throw new JsonParseException($"'{literal}' が必要（位置 {_i}）");
        _i += literal.Length;
    }

    private char Peek() => _i < _s.Length ? _s[_i] : '\0';

    private char Next()
    {
        if (_i >= _s.Length) throw new JsonParseException("入力終端");
        return _s[_i++];
    }

    private void SkipWhitespace()
    {
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
            else break;
        }
    }
}

/// <summary>JSON として壊れている入力（版チェックより手前で弾く）。</summary>
public sealed class JsonParseException : Exception
{
    public JsonParseException(string message) : base(message) { }
}
