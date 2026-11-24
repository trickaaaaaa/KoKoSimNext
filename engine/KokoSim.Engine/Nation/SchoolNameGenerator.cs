using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation;

/// <summary>校名生成の語彙（設計書05 §2.1, YAML駆動）。</summary>
public sealed record SchoolNameVocab
{
    /// <summary>全国共有の地名（県別リストが無い県のフォールバック）。</summary>
    public IReadOnlyList<string> PlacePrefixes { get; init; } = new[] { "青葉", "桜", "緑" };
    public IReadOnlyList<string> PublicSuffixes { get; init; } = new[] { "東", "西", "中央" };
    public IReadOnlyList<string> PrivateStems { get; init; } = new[] { "聖凛", "北都" };
    public IReadOnlyList<string> PrivateSuffixes { get; init; } = new[] { "学院", "学園" };
    public double PrivateRatio { get; init; } = 0.35;

    /// <summary>
    /// 県別の地名（Prefecture.Id → 地名リスト。設計書05 §2.1 拡張）。地域色と大規模県の容量を担う。
    /// 該当県のリストが無い／空なら共有 <see cref="PlacePrefixes"/> へフォールバック（後方互換）。
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> PlacesByPrefecture { get; init; }
        = new Dictionary<int, IReadOnlyList<string>>();

    /// <summary>
    /// 出さない私立の名前（語幹＋接尾。「高校」は付けない。issue #1320）。実在校のブランド名
    /// （明徳義塾・常翔学園 等）が架空の学校として出ないようにする。当たったら乱数を引き直さず接尾を隣へずらす
    /// （<see cref="SchoolNameGenerator.Generate"/>）。公立は <see cref="ExcludedPublicNames"/>。
    /// </summary>
    public IReadOnlyCollection<string> ExcludedPrivateNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 出さない公立の名前（地名＋特徴語。「高校」は付けない。issue #1470）。実在校名（札幌東・仙台第一 等）との完全一致を避ける。
    /// 当たったら乱数を引き直さず特徴語を隣へずらす。一覧は実在校名の全国リスト（非公開側）と語彙を照合する
    /// <c>tools/check-real-school-names.py</c> が出す。
    /// </summary>
    public IReadOnlyCollection<string> ExcludedPublicNames { get; init; } = Array.Empty<string>();
}

/// <summary>校名ジェネレータ。公立=地名＋特徴、私立=語幹＋接尾。乱数は注入（決定論）。</summary>
public static class SchoolNameGenerator
{
    /// <summary>
    /// 校名を1つ生成する。<paramref name="prefId"/> を渡すと公立の地名を県別リストから引く（無ければ共有へフォールバック）。
    /// <b>乱数消費は常に3ドロー固定（NextDouble×1＋NextInt×2）</b>。県別リストの選択は Pick 前に済ませるため、
    /// 語彙サイズや県指定の有無に関わらずドロー数は不変＝強さ/名声/校風の生成列を乱さない（統計回帰帯を保護）。
    /// </summary>
    public static (string Name, Establishment Establishment) Generate(
        SchoolNameVocab v, IRandomSource rng, int prefId = -1)
    {
        if (rng.NextDouble() < v.PrivateRatio)
        {
            var si = PickIndex(v.PrivateStems, rng);
            var xi = PickIndex(v.PrivateSuffixes, rng);
            var (stem, suffix) = AvoidExcluded(v, si, xi);
            return (stem + suffix + "高校", Establishment.Private);
        }
        else
        {
            // 県別地名 → 無ければ共有地名。リスト選択は乱数を消費しない（Pick が1ドロー消費）。
            var places = PlacesFor(v, prefId);
            var place = Pick(places, rng);
            var suffix = AvoidRepeat(place, v.PublicSuffixes, PickIndex(v.PublicSuffixes, rng), v.ExcludedPublicNames);
            return (place + suffix + "高校", Establishment.Public);
        }
    }

    /// <summary>県別地名リストを返す（非空のとき）。無ければ共有 PlacePrefixes。乱数不使用。</summary>
    private static IReadOnlyList<string> PlacesFor(SchoolNameVocab v, int prefId)
    {
        if (prefId >= 0
            && v.PlacesByPrefecture.TryGetValue(prefId, out var list)
            && list.Count > 0)
        {
            return list;
        }
        return v.PlacePrefixes;
    }

    private static string Pick(IReadOnlyList<string> list, IRandomSource rng)
        => list.Count == 0 ? "" : list[rng.NextInt(0, list.Count)];

    /// <summary><see cref="Pick"/> と同じ乱数の使い方で添字を返す（空なら -1・乱数を消費しない）。</summary>
    private static int PickIndex(IReadOnlyList<string> list, IRandomSource rng)
        => list.Count == 0 ? -1 : rng.NextInt(0, list.Count);

    /// <summary>字が重なる組み合わせか（「南陽南陽」「湘南南」「仙台台」）。地名の末尾が接尾の先頭字か接尾そのもの。</summary>
    public static bool RepeatsCharacters(string place, string suffix)
        => place.Length > 0 && suffix.Length > 0 && (place[^1] == suffix[0] || place.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>
    /// 引いた接尾が地名と字が重なるなら、<b>乱数を使わずに</b>接尾を隣へずらす（全部重なる異常時は引いたまま）。
    /// 実在校名（<paramref name="excluded"/>）に当たる組み合わせも同じ要領で避ける（全部当たるなら重なりだけを避ける）。
    /// ドロー数は変わらない＝重ならなかった学校の名前も、強さ・名声・校風の生成列も変わらない。
    /// </summary>
    private static string AvoidRepeat(string place, IReadOnlyList<string> suffixes, int index, IReadOnlyCollection<string> excluded)
    {
        if (index < 0) return "";
        if (excluded.Count > 0)
        {
            for (var d = 0; d < suffixes.Count; d++)
            {
                var s = suffixes[(index + d) % suffixes.Count];
                if (!RepeatsCharacters(place, s) && !excluded.Contains(place + s)) return s;
            }
        }
        for (var d = 0; d < suffixes.Count; d++)
        {
            var s = suffixes[(index + d) % suffixes.Count];
            if (!RepeatsCharacters(place, s)) return s;
        }
        return suffixes[index];
    }

    /// <summary>
    /// 実在校の名前に当たったら、<b>乱数を使わずに</b>接尾を隣へずらす（全部だめなら語幹も隣へ）。
    /// 引いた乱数の数は変わらない＝強さ・名声・校風の生成列も、除外に当たらなかった学校の名前も変わらない。
    /// 語彙の全組み合わせが除外されている異常時は、引いたままの組み合わせを返す（落とさない）。
    /// </summary>
    private static (string Stem, string Suffix) AvoidExcluded(SchoolNameVocab v, int si, int xi)
    {
        string At(IReadOnlyList<string> list, int i) => i < 0 ? "" : list[i];
        var excluded = v.ExcludedPrivateNames;
        if (excluded.Count == 0 || !excluded.Contains(At(v.PrivateStems, si) + At(v.PrivateSuffixes, xi)))
            return (At(v.PrivateStems, si), At(v.PrivateSuffixes, xi));

        int stems = Math.Max(1, v.PrivateStems.Count), suffixes = Math.Max(1, v.PrivateSuffixes.Count);
        for (var ds = 0; ds < stems; ds++)
        for (var dx = 0; dx < suffixes; dx++)
        {
            var s = si < 0 ? -1 : (si + ds) % stems;
            var x = xi < 0 ? -1 : (xi + dx) % suffixes;
            var stem = At(v.PrivateStems, s);
            var suffix = At(v.PrivateSuffixes, x);
            if (!excluded.Contains(stem + suffix)) return (stem, suffix);
        }
        return (At(v.PrivateStems, si), At(v.PrivateSuffixes, xi));
    }
}
