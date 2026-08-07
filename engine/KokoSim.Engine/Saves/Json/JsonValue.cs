using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KokoSim.Engine.Saves.Json;

/// <summary>
/// セーブ専用の最小 JSON DOM（設計書20-draft §5 改訂・issue #507）。外部依存なし
/// （<c>System.Text.Json</c> も YamlDotNet も使わない）＝Unity(IL2CPP) からもそのまま呼べ、
/// リフレクションを一切使わないので AOT ストリップに強い。<see cref="Debugging.TraceJson"/> と同じく
/// 「フィールド名の単一ソースは呼び出し側の明示マッピング」方針で、往復（parse↔write）の等価を保証する。
///
/// <para>数値は<b>元のトークン文字列</b>のまま保持し、型付きゲッタが要求時にパースする。これにより
/// ulong（母種シード＝2^53超）や double の丸め損失を避け、往復で1ビットも変えない（不変条件#2 決定論）。
/// 書き出す double は round-trippable な <c>G17</c> で整形する。</para>
/// </summary>
public abstract class JsonValue
{
    /// <summary>文字列を JSON としてパースする（末尾に余分なトークンがあれば例外）。</summary>
    public static JsonValue Parse(string text) => JsonParser.Parse(text);

    /// <summary>この値を JSON 文字列へ書き出す。</summary>
    public abstract void Write(StringBuilder sb);

    public override string ToString()
    {
        var sb = new StringBuilder(256);
        Write(sb);
        return sb.ToString();
    }

    public JsonObject AsObject() => (JsonObject)this;
    public JsonArray AsArray() => (JsonArray)this;

    // --- 生成ヘルパ（暗黙変換は使わず明示的に。null は JsonNull へ寄せる） ---
    public static JsonValue Of(string? s) => s is null ? JsonNull.Instance : new JsonString(s);
    public static JsonValue Of(bool b) => new JsonBool(b);
    public static JsonValue Of(int v) => new JsonNumber(v.ToString(CultureInfo.InvariantCulture));
    public static JsonValue Of(long v) => new JsonNumber(v.ToString(CultureInfo.InvariantCulture));
    public static JsonValue Of(ulong v) => new JsonNumber(v.ToString(CultureInfo.InvariantCulture));

    /// <summary>double は round-trippable（G17）で整形。NaN/∞ は JSON にできないので null。</summary>
    public static JsonValue Of(double v) =>
        double.IsNaN(v) || double.IsInfinity(v)
            ? JsonNull.Instance
            : new JsonNumber(v.ToString("G17", CultureInfo.InvariantCulture));
}

/// <summary>順序保存のオブジェクト（キー挿入順で書き出す＝差分比較のノイズを減らす）。</summary>
public sealed class JsonObject : JsonValue
{
    private readonly List<string> _keys = new();
    private readonly Dictionary<string, JsonValue> _map = new();

    public IReadOnlyList<string> Keys => _keys;
    public int Count => _keys.Count;
    public bool Has(string key) => _map.ContainsKey(key);

    public JsonValue this[string key] => _map[key];

    // --- 追加（フルエント。同じキーは上書き＝順序は初回位置を維持） ---
    public JsonObject Add(string key, JsonValue value)
    {
        if (!_map.ContainsKey(key)) _keys.Add(key);
        _map[key] = value;
        return this;
    }

    public JsonObject Add(string key, string? value) => Add(key, Of(value));
    public JsonObject Add(string key, bool value) => Add(key, Of(value));
    public JsonObject Add(string key, int value) => Add(key, Of(value));
    public JsonObject Add(string key, long value) => Add(key, Of(value));
    public JsonObject Add(string key, ulong value) => Add(key, Of(value));
    public JsonObject Add(string key, double value) => Add(key, Of(value));

    // --- 型付きゲッタ（欠損は KeyNotFound で早期に気づく。任意項目は Has で分岐） ---
    private JsonValue Get(string key) =>
        _map.TryGetValue(key, out var v) ? v : throw new KeyNotFoundException($"JSON key not found: {key}");

    public bool IsNull(string key) => Get(key) is JsonNull;
    public string? Str(string key) => Get(key) is JsonString s ? s.Value : null;
    public bool Bool(string key) => ((JsonBool)Get(key)).Value;
    public int Int(string key) => ((JsonNumber)Get(key)).AsInt();
    public long Long(string key) => ((JsonNumber)Get(key)).AsLong();
    public ulong ULong(string key) => ((JsonNumber)Get(key)).AsULong();
    public double Double(string key) => ((JsonNumber)Get(key)).AsDouble();
    public JsonObject Obj(string key) => (JsonObject)Get(key);
    public JsonArray Arr(string key) => (JsonArray)Get(key);

    public override void Write(StringBuilder sb)
    {
        sb.Append('{');
        for (var i = 0; i < _keys.Count; i++)
        {
            if (i > 0) sb.Append(',');
            JsonString.WriteEscaped(sb, _keys[i]);
            sb.Append(':');
            _map[_keys[i]].Write(sb);
        }
        sb.Append('}');
    }
}

/// <summary>順序保存の配列。</summary>
public sealed class JsonArray : JsonValue
{
    private readonly List<JsonValue> _items = new();

    public IReadOnlyList<JsonValue> Items => _items;
    public int Count => _items.Count;
    public JsonValue this[int index] => _items[index];

    public JsonArray Add(JsonValue value)
    {
        _items.Add(value);
        return this;
    }

    public override void Write(StringBuilder sb)
    {
        sb.Append('[');
        for (var i = 0; i < _items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            _items[i].Write(sb);
        }
        sb.Append(']');
    }
}

public sealed class JsonString : JsonValue
{
    public string Value { get; }
    public JsonString(string value) => Value = value;

    public override void Write(StringBuilder sb) => WriteEscaped(sb, Value);

    /// <summary>JSON文字列リテラル（引用符込み）を書く。<see cref="Debugging.TraceJson.Str"/> と同じ規則。</summary>
    public static void WriteEscaped(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}

/// <summary>数値。元のトークン文字列を保持し、要求時に型変換する（往復で丸めない）。</summary>
public sealed class JsonNumber : JsonValue
{
    public string Token { get; }
    public JsonNumber(string token) => Token = token;

    public int AsInt() => int.Parse(Token, NumberStyles.Integer, CultureInfo.InvariantCulture);
    public long AsLong() => long.Parse(Token, NumberStyles.Integer, CultureInfo.InvariantCulture);
    public ulong AsULong() => ulong.Parse(Token, NumberStyles.Integer, CultureInfo.InvariantCulture);

    public double AsDouble() =>
        double.Parse(Token, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

    public override void Write(StringBuilder sb) => sb.Append(Token);
}

public sealed class JsonBool : JsonValue
{
    public bool Value { get; }
    public JsonBool(bool value) => Value = value;
    public override void Write(StringBuilder sb) => sb.Append(Value ? "true" : "false");
}

public sealed class JsonNull : JsonValue
{
    public static readonly JsonNull Instance = new();
    private JsonNull() { }
    public override void Write(StringBuilder sb) => sb.Append("null");
}
