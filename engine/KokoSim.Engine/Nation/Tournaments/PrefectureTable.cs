namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>1都道府県の識別情報（設計書05 §1.5, data/prefectures.yaml）。JIS順のId・ローマ字名・所属地区。</summary>
public sealed record PrefectureInfo(int Id, string Name, string Region);

/// <summary>
/// 47都道府県 → 実名・所属地区の対応表（data/prefectures.yaml）。
/// 生成される全国は無名の第N県だが、この表で JIS順Id を実名・地区へ結び付け、
/// 秋季県大会 → 地区大会 の集約と regional-tournaments.yaml の枠キー参照を成立させる（不変条件#4）。
/// </summary>
public sealed record PrefectureTable(IReadOnlyList<PrefectureInfo> Prefectures)
{
    public PrefectureInfo? ById(int id) => Prefectures.FirstOrDefault(p => p.Id == id);

    /// <summary>指定地区に属する県（Id順）。</summary>
    public IReadOnlyList<PrefectureInfo> InRegion(string region)
        => Prefectures.Where(p => p.Region == region).OrderBy(p => p.Id).ToList();

    /// <summary>表に現れる全地区名（重複なし）。</summary>
    public IReadOnlyList<string> Regions()
        => Prefectures.Select(p => p.Region).Distinct().ToList();
}
