using KokoSim.Engine.Saves.Json;
using Xunit;

namespace KokoSim.Engine.Tests.Saves;

/// <summary>
/// セーブ用 JSON DOM（<see cref="JsonValue"/>）の往復（issue #507）。外部依存なしの自前パーサ/ライタが
/// 「書く→読む→また書く」で1文字も変わらないこと（＝往復の等価）を固定する。ulong や double の
/// 桁落ちが無いこと（決定論の前提）を重点的に確かめる。
/// </summary>
public sealed class JsonRoundTripTests
{
    private static string Roundtrip(string json) => JsonValue.Parse(json).ToString();

    [Fact]
    public void Object_And_Array_Roundtrip_Preserving_Order()
    {
        var json = "{\"b\":1,\"a\":[true,false,null,\"x\"],\"c\":{\"d\":-2}}";
        Assert.Equal(json, Roundtrip(json));
    }

    /// <summary>
    /// issue #660: オブジェクトはキーが一定数を超えると索引を作る。索引の前後で、引き当て・上書き（位置は初回のまま）・
    /// 書き出し順が変わらないこと。
    /// </summary>
    [Fact]
    public void Object_ManyKeys_LookupOverwriteAndOrder_AcrossIndexThreshold()
    {
        var o = new JsonObject();
        for (var i = 0; i < 20; i++) o.Add("k" + i, i);
        o.Add("k3", 300);    // 索引ができる前に入ったキーの上書き
        o.Add("k15", 1500);  // 索引ができた後に入ったキーの上書き
        Assert.Equal(20, o.Count);
        Assert.Equal(300, o.Int("k3"));
        Assert.Equal(1500, o.Int("k15"));
        Assert.Equal(19, o.Int("k19"));
        Assert.False(o.Has("k20"));
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => o.Int("k20"));
        Assert.Equal("k3", o.Keys[3]);
        Assert.Equal("k15", o.Keys[15]);
        var json = o.ToString();
        Assert.StartsWith("{\"k0\":0,\"k1\":1,\"k2\":2,\"k3\":300,", json);
        Assert.Equal(json, Roundtrip(json));
    }

    [Fact]
    public void String_Escapes_Roundtrip()
    {
        var built = new JsonObject().Add("s", "行\t\"引用\"\\\n終わり").ToString();
        var reparsed = JsonValue.Parse(built).AsObject();
        Assert.Equal("行\t\"引用\"\\\n終わり", reparsed.Str("s"));
        Assert.Equal(built, JsonValue.Parse(built).ToString());
    }

    [Fact]
    public void NonAscii_Is_Emitted_Raw()
    {
        var built = new JsonObject().Add("name", "桜丘").ToString();
        Assert.Contains("桜丘", built);   // 非ASCIIはエスケープせずそのまま（TraceJson と同方針）
        Assert.Equal("桜丘", JsonValue.Parse(built).AsObject().Str("name"));
    }

    [Fact]
    public void ULong_Beyond_Double_Precision_Roundtrips_Exactly()
    {
        // 2^53 を超える母種シードを double で焼くと壊れる。文字列トークン保持で厳密に往復することを固定。
        ulong seed = 0xDEADBEEFCAFEF00DUL;   // = 16045690984833335309
        var built = new JsonObject().Add("seed", seed).ToString();
        Assert.Equal(seed, JsonValue.Parse(built).AsObject().ULong("seed"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.123456789012345)]
    [InlineData(1234567.8901234567)]
    [InlineData(1e-9)]
    public void Double_Roundtrips_Exactly(double v)
    {
        var built = new JsonObject().Add("x", v).ToString();
        Assert.Equal(v, JsonValue.Parse(built).AsObject().Double("x"));
    }

    [Fact]
    public void NaN_And_Infinity_Become_Null()
    {
        var built = new JsonObject().Add("x", double.NaN).Add("y", double.PositiveInfinity).ToString();
        var o = JsonValue.Parse(built).AsObject();
        Assert.True(o.IsNull("x"));
        Assert.True(o.IsNull("y"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"a\":}")]
    [InlineData("[1,2")]
    [InlineData("nul")]
    [InlineData("{\"a\":1}trailing")]
    public void Malformed_Input_Throws_JsonParseException(string bad)
    {
        Assert.Throws<JsonParseException>(() => JsonValue.Parse(bad));
    }
}
