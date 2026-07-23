using KokoSim.Engine.Career;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>施設経済（Issue #128）: 多軸施設の効果集計・購入支出・従来一致の検証。</summary>
public sealed class FacilityEconomyTests
{
    private static FacilityCatalog Catalog => FacilityCatalog.Default;

    [Fact]
    public void AllZero_MatchesLegacyBaseline()
    {
        // 全系統Lv0＝施設なし。SeasonContext.ResolveFacility が基準 (1.0 / 300分) を返す（不変条件#2）。
        var ctx = new SeasonContext { Facilities = new FacilitySet(), FacilityCatalog = Catalog };
        Assert.Equal((1.0, 300), ctx.ResolveFacility());
    }

    [Fact]
    public void AllMaxLevel_ReproducesLegacyTier4Envelope()
    {
        // 全系統Lv3で 効率2.40 / 練習700分 ＝ 旧 facility_tiers Lv4 と一致（#115の校正を保つ）。
        var set = new FacilitySet();
        foreach (var f in Catalog.Facilities) set.SetLevel(f.Id, f.MaxLevel);

        var ctx = new SeasonContext { Facilities = set, FacilityCatalog = Catalog };
        var (coef, budget) = ctx.ResolveFacility();
        Assert.Equal(2.40, coef, 3);
        Assert.Equal(700, budget);
    }

    [Fact]
    public void Aggregate_IsAdditiveAcrossSystems()
    {
        // 照明Lv1(+80分) と 寮Lv1(+0.15) だけ。加算されて (1.15, 380分)。
        var set = new FacilitySet();
        set.SetLevel("lighting", 1);
        set.SetLevel("dorm", 1);
        var ctx = new SeasonContext { Facilities = set, FacilityCatalog = Catalog };
        var (coef, budget) = ctx.ResolveFacility();
        Assert.Equal(1.15, coef, 3);
        Assert.Equal(380, budget);
    }

    [Fact]
    public void Purchase_DeductsFundsAndRaisesLevel()
    {
        var m = new Manager { Funds = 100 };
        var set = new FacilitySet();
        var r = FacilityPurchase.TryUpgrade(m, set, Catalog, "lighting");
        Assert.Equal(FacilityPurchaseResult.Ok, r);
        Assert.Equal(1, set.LevelOf("lighting"));
        Assert.Equal(60, m.Funds); // 100 - 40(Lv1費用)
    }

    [Fact]
    public void Purchase_InsufficientFunds_DoesNotMutate()
    {
        var m = new Manager { Funds = 30 };
        var set = new FacilitySet();
        var r = FacilityPurchase.TryUpgrade(m, set, Catalog, "lighting"); // Lv1=40万必要
        Assert.Equal(FacilityPurchaseResult.InsufficientFunds, r);
        Assert.Equal(0, set.LevelOf("lighting"));
        Assert.Equal(30, m.Funds);
    }

    [Fact]
    public void Purchase_StopsAtMaxLevel()
    {
        var m = new Manager { Funds = 10000 };
        var set = new FacilitySet();
        var def = Catalog.Find("lighting")!;
        for (var i = 0; i < def.MaxLevel; i++)
            Assert.Equal(FacilityPurchaseResult.Ok, FacilityPurchase.TryUpgrade(m, set, Catalog, "lighting"));

        Assert.Equal(def.MaxLevel, set.LevelOf("lighting"));
        Assert.Equal(FacilityPurchaseResult.AlreadyMaxLevel,
            FacilityPurchase.TryUpgrade(m, set, Catalog, "lighting"));
        Assert.Null(FacilityPurchase.NextLevelCost(set, Catalog, "lighting"));
    }

    [Fact]
    public void Purchase_UnknownFacility_IsRejected()
    {
        var m = new Manager { Funds = 100 };
        Assert.Equal(FacilityPurchaseResult.UnknownFacility,
            FacilityPurchase.TryUpgrade(m, new FacilitySet(), Catalog, "nope"));
    }

    [Fact]
    public void FacilitiesNull_FallsBackToLegacySingleAxis()
    {
        // Facilities 未注入なら従来の単一軸（Issue #115）に従い、施設0で基準一致。
        var ctx = new SeasonContext();
        Assert.Null(ctx.Facilities);
        Assert.Equal((1.0, 300), ctx.ResolveFacility());
    }
}
