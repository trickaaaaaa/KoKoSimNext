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
/// <para>財布は2つある。建物（<see cref="PurchaseKind.Facility"/>）は <see cref="Manager.FacilityFund"/>、
/// 練習道具・備品は <see cref="Manager.Funds"/>（部費）から出る。どちらから出るかは区分だけで決まり、
/// 呼び出し側は意識しない（<see cref="WalletOf"/> が単一ソース）。</para>
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

    /// <summary>
    /// この区分の支払いに使う財布（設計書04 §1.4）。建物は施設基金、道具・備品は部費。
    /// 判定はここにしか置かない（画面もこの関数で残高を出す）。
    /// </summary>
    public static PurchaseWallet WalletOf(PurchaseKind kind) =>
        kind == PurchaseKind.Facility ? PurchaseWallet.FacilityFund : PurchaseWallet.Funds;

    /// <summary>指定の財布の残高[万円]。</summary>
    public static double BalanceOf(Manager manager, PurchaseWallet wallet) =>
        wallet == PurchaseWallet.FacilityFund ? manager.FacilityFund : manager.Funds;

    /// <summary>資金が足り最大未満なら1段上げて費用を減算する（区分に応じた財布から引く）。</summary>
    public static FacilityPurchaseResult TryUpgrade(
        Manager manager, FacilitySet set, FacilityCatalog catalog, string facilityId)
    {
        var def = catalog.Find(facilityId);
        if (def is null) return FacilityPurchaseResult.UnknownFacility;

        var lv = set.LevelOf(facilityId);
        if (lv >= def.MaxLevel) return FacilityPurchaseResult.AlreadyMaxLevel;

        var cost = def.Tiers[lv + 1].Cost;
        var wallet = WalletOf(def.Kind);
        if (BalanceOf(manager, wallet) < cost) return FacilityPurchaseResult.InsufficientFunds;

        if (wallet == PurchaseWallet.FacilityFund) manager.FacilityFund -= cost;
        else manager.Funds -= cost;
        set.SetLevel(facilityId, lv + 1);
        return FacilityPurchaseResult.Ok;
    }
}

/// <summary>支払いに使う財布（設計書04 §1.4）。</summary>
public enum PurchaseWallet
{
    /// <summary>部費（運営費）。練習道具・備品・合宿・スカウト・遠征。</summary>
    Funds,
    /// <summary>施設基金（学校の設備投資＋後援会・OB寄付）。建物だけ。</summary>
    FacilityFund,
}
