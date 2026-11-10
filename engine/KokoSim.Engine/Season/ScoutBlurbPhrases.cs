using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>
/// スカウト候補の寸評の語彙（data/scout-blurbs.yaml・issue #867・design-04 §2.4）。
/// 置き場は <c>scout.blurb.&lt;axis&gt;.&lt;bucket&gt;</c>（axis=batting/mobility/defense/pitching,
/// bucket=high/mid/low）。組み立ての規則は <see cref="ScoutModel.PickBlurb"/> にあり、ここは語彙だけを持つ
/// （不変条件#4 データ駆動。<see cref="Nation.Tournaments.PreviewPhrases"/> と同じ流儀）。
/// </summary>
public sealed class ScoutBlurbPhrases
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _pools;

    public ScoutBlurbPhrases(IReadOnlyDictionary<string, IReadOnlyList<string>> pools) => _pools = pools;

    /// <summary>YAML に書かれた置き場の名前（テストの網羅確認用）。</summary>
    public IEnumerable<string> Keys => _pools.Keys;

    /// <summary>置き場の候補列。無い・空なら最小の既定文1本。</summary>
    public IReadOnlyList<string> Pool(string key)
        => _pools.TryGetValue(key, out var p) && p.Count > 0 ? p : FallbackPool(key);

    /// <summary>
    /// YAML を読めなかったときの最小語彙（Unity の StreamingAssets 欠落・テストの既定）。
    /// どの置き場にも1文だけ返す（本番は必ず YAML を渡す）。
    /// </summary>
    public static ScoutBlurbPhrases Fallback { get; } = new(new Dictionary<string, IReadOnlyList<string>>());

    private static IReadOnlyList<string> FallbackPool(string key) => new[] { "まだ見立ての段階。" };
}

/// <summary>
/// data/scout-blurbs.yaml を <see cref="ScoutBlurbPhrases"/> へ変換する純パーサ（IO・依存なし＝不変条件#3）。
/// <see cref="Nation.Tournaments.PreviewPhrasesParser"/> と同じ流儀で、Unity（StreamingAssets）とテスト（data/）が
/// 同一コードで読む。
///
/// 対応する構文はこのファイルが使う部分集合だけ:
///   - 全行コメント（先頭が <c>#</c>）と空行は無視
///   - <c>名前:</c> の行が置き場の始まり（<c>phrases:</c> は見出しなので読み飛ばす）
///   - <c>- "文"</c> の行がその置き場の候補（前後の <c>"</c> は外す）
/// </summary>
public static class ScoutBlurbPhrasesParser
{
    public static ScoutBlurbPhrases Parse(string yaml)
    {
        var pools = new Dictionary<string, IReadOnlyList<string>>();
        List<string>? current = null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("- "))
            {
                current?.Add(Unquote(line.Substring(2).Trim()));
                continue;
            }
            if (line.EndsWith(":"))
            {
                var key = line.Substring(0, line.Length - 1).Trim();
                if (key == "phrases") { current = null; continue; }
                current = new List<string>();
                pools[key] = current;
            }
        }
        return new ScoutBlurbPhrases(pools);
    }

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
}
