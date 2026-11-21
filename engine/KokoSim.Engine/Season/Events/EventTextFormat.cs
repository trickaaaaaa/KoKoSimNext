using System.Collections.Generic;
using System.Text;

namespace KokoSim.Engine.Season.Events;

/// <summary>
/// 会話文の差し込み語（issue #609）。<c>{player}</c> のような語を実際の名前へ置き換える。
///
/// <para><b>未知の語はそのまま残す</b>（消さない）。消すと data/events.yaml の書き間違いが
/// 「文が少し短い」だけになって絵からも読み取れなくなる。残せば必ず目に付く。
/// 使ってよい語は <see cref="KnownKeys"/> が正典で、データ側の全文言が
/// この集合に収まっていることをテストで固定する。</para>
/// </summary>
public static class EventTextFormat
{
    /// <summary>
    /// 差し込み語として書いてよいキー（data/events.yaml のヘッダと対応）。
    /// 大会名の差し込み語（<c>{koshien}</c> など）は <see cref="DisplayNames.Keys"/> から足す（issue #1319）。
    /// </summary>
    public static readonly IReadOnlyList<string> KnownKeys = BuildKnownKeys();

    private static IReadOnlyList<string> BuildKnownKeys()
    {
        var keys = new List<string> { "player", "school", "manager", "count", "injury", "weeks", "other" };
        foreach (var (k, _) in DisplayNames.Keys) keys.Add(k);
        return keys;
    }

    /// <summary>
    /// 差し込み語を置換する。<paramref name="values"/> に無いキーは大会名の表（<see cref="DisplayNames.Current"/>）を引き、
    /// それにも無ければ <c>{key}</c> のまま残す。
    /// </summary>
    public static string Apply(string text, IReadOnlyDictionary<string, string>? values)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') { sb.Append(text[i]); continue; }
            var close = text.IndexOf('}', i + 1);
            if (close < 0) { sb.Append(text, i, text.Length - i); break; }

            var key = text.Substring(i + 1, close - i - 1);
            if (values != null && values.TryGetValue(key, out var v)) sb.Append(v);
            else if (DisplayNames.Current.TryGet(key, out var name)) sb.Append(name);
            else sb.Append(text, i, close - i + 1);
            i = close;
        }
        return sb.ToString();
    }

    /// <summary>文中の差し込み語を列挙する（データ検査用）。</summary>
    public static IReadOnlyList<string> KeysIn(string text)
    {
        var keys = new List<string>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') continue;
            var close = text.IndexOf('}', i + 1);
            if (close < 0) break;
            keys.Add(text.Substring(i + 1, close - i - 1));
            i = close;
        }
        return keys;
    }
}
