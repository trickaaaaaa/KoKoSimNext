namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会展望の文言の部品（data/preview-phrases.yaml・issue #784）。置き場の名前（<c>school.open.fav</c> 等）→
/// 文の候補列。組み立ての規則は <see cref="PreviewWriter"/> が持ち、ここは語彙だけを持つ（不変条件#4）。
/// 置き場が YAML に無い・空のときは <see cref="Fallback"/> の1文で埋める（落ちない・空文字を出さない）。
/// </summary>
public sealed class PreviewPhrases
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _pools;

    public PreviewPhrases(IReadOnlyDictionary<string, IReadOnlyList<string>> pools) => _pools = pools;

    /// <summary>YAML に書かれた置き場の名前（テストの網羅確認用）。</summary>
    public IEnumerable<string> Keys => _pools.Keys;

    /// <summary>置き場の候補列。無い・空なら最小の既定文1本。</summary>
    public IReadOnlyList<string> Pool(string key)
        => _pools.TryGetValue(key, out var p) && p.Count > 0 ? p : FallbackPool(key);

    /// <summary>
    /// YAML を読めなかったときの最小語彙（Unity の StreamingAssets 欠落・テストの既定）。どの置き場にも1文だけ
    /// 返す＝文は組めるが、組み合わせが1通りなので同じ大会で文が被りうる（本番は必ず YAML を渡す）。
    /// </summary>
    public static PreviewPhrases Fallback { get; } = new(new Dictionary<string, IReadOnlyList<string>>());

    private static IReadOnlyList<string> FallbackPool(string key)
    {
        if (key.StartsWith("school.open.")) return new[] { "全{field}校中{seed}番目の戦力評価。" };
        if (key.StartsWith("school.body.")) return new[] { "{strong}が持ち味。" };
        if (key == "player.intro") return new[] { "注目は{grade}年の{name}。" };
        if (key.StartsWith("player.head.")) return new[] { "チームの注目選手。" };
        return new[] { "" };
    }
}

/// <summary>
/// data/preview-phrases.yaml を <see cref="PreviewPhrases"/> へ変換する純パーサ（IO・依存なし＝不変条件#3）。
/// <see cref="Players.SkillCatalogParser"/> と同じ流儀で、Unity（StreamingAssets）とテスト（data/）が同一コードで読む。
///
/// 対応する構文はこのファイルが使う部分集合だけ:
///   - 全行コメント（先頭が <c>#</c>）と空行は無視
///   - <c>名前:</c> の行が置き場の始まり（<c>phrases:</c> は見出しなので読み飛ばす）
///   - <c>- "文"</c> の行がその置き場の候補（前後の <c>"</c> は外す）
/// </summary>
public static class PreviewPhrasesParser
{
    public static PreviewPhrases Parse(string yaml)
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
        return new PreviewPhrases(pools);
    }

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
}
