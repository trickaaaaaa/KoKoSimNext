namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// センバツ選考枠（設計書05 §4, YAML駆動）。地区別の一般選考枠。21世紀枠は非採用（成績ベースのみ）。
/// 総数は基準年の実数（例年32校規模）。神宮枠は神宮優勝地区へ+1（§1.1）。
/// </summary>
public sealed record SenbatsuBerths
{
    /// <summary>地区→一般選考枠。合計31＋神宮枠1＝32校規模（設計書05 §4「例年32校規模」・21世紀枠は非採用）。
    /// 基準は2024年（第96回）以降の実数＝一般選考29（北海道1・東北3・関東東京6・北信越2・東海3・近畿6・中国2・四国2・九州4）
    /// ＋神宮1＋21世紀2＝32。21世紀枠2つぶんは、中国（実2→3）と九州（実4→5）へ1校ずつ上乗せして32に合わせている
    /// （枠の増やし先は、いずれも一般枠で次点の層が厚い西日本の地区としたゲーム側の簡略化）。</summary>
    public IReadOnlyDictionary<string, int> GeneralByRegion { get; init; } = new Dictionary<string, int>
    {
        ["hokkaido"] = 1,
        ["tohoku"] = 3,
        ["kanto"] = 5,
        ["tokyo"] = 1,      // 関東・東京で計6を融通（現実の「関東・東京6」に対応）
        ["hokushinetsu"] = 2,
        ["tokai"] = 3,
        ["kinki"] = 6,
        ["chugoku"] = 3,
        ["shikoku"] = 2,
        ["kyushu"] = 5,
    };

    public int GeneralFor(string region) => GeneralByRegion.TryGetValue(region, out var v) ? v : 0;
}

/// <summary>センバツ選考（設計書05 §4）。前年秋の地区大会順位＋神宮枠から機械的に選ぶ。</summary>
public static class SenbatsuSelection
{
    /// <summary>
    /// 選考を実行。regionPlacements=地区大会の最終順位（上位ほど当確）。jinguChampionRegion=神宮優勝校の地区（+1枠）。
    /// 各地区で「一般枠(＋神宮枠)」の上位校を選ぶ。同成績の比較は地区大会の勝ち上がり順（placementの並び）で解決済み。
    /// </summary>
    public static IReadOnlyList<School> Select(
        IReadOnlyDictionary<string, IReadOnlyList<School>> regionPlacements,
        SenbatsuBerths berths,
        string? jinguChampionRegion = null)
    {
        var selected = new List<School>();
        foreach (var (region, placement) in regionPlacements.OrderBy(kv => kv.Key))
        {
            var count = berths.GeneralFor(region);
            if (region == jinguChampionRegion) count += 1; // 神宮枠（§1.1）
            selected.AddRange(placement.Take(System.Math.Max(0, count)));
        }
        return selected;
    }
}
