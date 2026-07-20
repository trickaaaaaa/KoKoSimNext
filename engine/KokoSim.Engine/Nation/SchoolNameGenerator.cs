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
            var stem = Pick(v.PrivateStems, rng);
            var suffix = Pick(v.PrivateSuffixes, rng);
            return (stem + suffix + "高校", Establishment.Private);
        }
        else
        {
            // 県別地名 → 無ければ共有地名。リスト選択は乱数を消費しない（Pick が1ドロー消費）。
            var places = PlacesFor(v, prefId);
            var place = Pick(places, rng);
            var suffix = Pick(v.PublicSuffixes, rng);
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
}
