using KokoSim.Engine.Season;

namespace KokoSim.Engine.Career;

/// <summary>施設購入の結果（Issue #128）。</summary>
public enum FacilityPurchaseResult
{
    Ok,
    UnknownFacility,   // カタログに無いID
    AlreadyMaxLevel,   // これ以上上げられない（青天井防止の上限）
    InsufficientFunds, // 資金不足
}

/// <summary>
/// 買えるものへの支出経路（Issue #128・設計書04 §1.4）。監督の資金[万円]を減算してレベルを1段上げる。
/// 決定論（副作用は Manager の資金と FacilitySet のみ）。青天井は各系統の MaxLevel で止める。
/// <para>財布は部費（<see cref="Manager.Funds"/>）1つ。建物・練習道具・備品のどれも部費から出る（issue #1082）。</para>
/// </summary>
public static class FacilityPurchase
{
    /// <summary>次レベルへ上げる購入費[万円]。最大レベル/未知IDなら null。</summary>
    public static double? NextLevelCost(FacilitySet set, FacilityCatalog catalog, string facilityId)
    {
        var def = catalog.Find(facilityId);
        if (def is null) return null;
        var lv = set.LevelOf(facilityId);
        if (lv >= def.MaxLevel) return null;
        return def.Tiers[lv + 1].Cost;
    }

    /// <summary>部費が足り最大未満なら1段上げて費用を部費から引く。</summary>
    public static FacilityPurchaseResult TryUpgrade(
        Manager manager, FacilitySet set, FacilityCatalog catalog, string facilityId)
    {
        var def = catalog.Find(facilityId);
        if (def is null) return FacilityPurchaseResult.UnknownFacility;

        var lv = set.LevelOf(facilityId);
        if (lv >= def.MaxLevel) return FacilityPurchaseResult.AlreadyMaxLevel;

        var cost = def.Tiers[lv + 1].Cost;
        if (manager.Funds < cost) return FacilityPurchaseResult.InsufficientFunds;

        manager.Funds -= cost;
        set.SetLevel(facilityId, lv + 1);
        return FacilityPurchaseResult.Ok;
    }
}
