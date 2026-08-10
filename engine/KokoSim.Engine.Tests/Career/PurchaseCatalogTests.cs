using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 買えるもののカタログ拡張（医務室・練習道具・備品）の検証。
/// 要点は「新しく足したものは練習効率・週練習時間を1ビットも動かさない」こと＝
/// 既存4系統だけで決まる (2.40 / 700分) の校正（#115）が保たれること。
/// </summary>
public sealed class PurchaseCatalogTests
{
    private static FacilityCatalog Catalog => FacilityCatalog.Default;

    private static FacilitySet Maxed()
    {
        var set = new FacilitySet();
        foreach (var f in Catalog.Facilities) set.SetLevel(f.Id, f.MaxLevel);
        return set;
    }

    // --- 1. 何も持っていない＝従来と1ビット一致 ---

    [Fact]
    public void AggregateEffects_AllZero_EqualsNone()
    {
        var e = Catalog.AggregateEffects(new FacilitySet());
        Assert.Equal(FacilityEffects.None, e);
    }

    [Fact]
    public void ResolveEffects_NoFacilities_IsNone()
        => Assert.Equal(FacilityEffects.None, new SeasonContext().ResolveEffects());

    // --- 2. 新しく足したものは練習効率・週練習時間を動かさない（#115の校正を保つ） ---

    [Fact]
    public void NewItems_DoNotChangeCoefOrBudget()
    {
        var e = Catalog.AggregateEffects(Maxed());
        // 既存4系統だけで 2.40 / 700分。医務室・道具・備品は CoefAdd / BudgetAdd を持たない。
        Assert.Equal(1.40, e.CoefAdd, 3);
        Assert.Equal(400, e.BudgetAdd);
    }

    [Fact]
    public void NewItems_HaveNoCoefOrBudget()
    {
        foreach (var f in Catalog.Facilities)
        {
            if (f.Kind == PurchaseKind.Facility && f.Id != "clinic") continue;
            foreach (var t in f.Tiers)
            {
                Assert.Equal(0.0, t.CoefAdd);
                Assert.Equal(0, t.BudgetAdd);
            }
        }
    }

    // --- 3. 医務室: 怪我が起きにくく・早く治る ---

    [Fact]
    public void Clinic_ReducesInjuryProbAndRecovery()
    {
        var set = new FacilitySet();
        set.SetLevel("clinic", 3);
        var e = Catalog.AggregateEffects(set);

        Assert.Equal(0.85 * 0.85 * 0.85, e.InjuryProbFactor, 6);
        Assert.Equal(0.90 * 0.90 * 0.90, e.InjuryRecoveryFactor, 6);
        // 練習には一切効かない。
        Assert.Equal(0.0, e.CoefAdd);
        Assert.Equal(0, e.BudgetAdd);
        Assert.Equal(EquipmentBonus.None, e.Equipment);
    }

    // --- 4. 練習道具: 分野ごとに独立、段は掛け算で累積 ---

    [Theory]
    [InlineData("gear_batting")]
    [InlineData("gear_defense")]
    [InlineData("gear_baserunning")]
    [InlineData("gear_pitching")]
    public void Equipment_HasTwoLevels(string id)
    {
        var def = Catalog.Find(id);
        Assert.NotNull(def);
        Assert.Equal(PurchaseKind.Equipment, def!.Kind);
        Assert.Equal(2, def.MaxLevel);   // 効果小 / 効果大
    }

    [Fact]
    public void BattingEquipment_AffectsOnlyBattingField()
    {
        var set = new FacilitySet();
        set.SetLevel("gear_batting", 2);
        var e = Catalog.AggregateEffects(set);

        Assert.Equal(1.05 * 1.07, e.Equipment.Batting, 6);
        Assert.Equal(1.0, e.Equipment.Defense);
        Assert.Equal(1.0, e.Equipment.BaseRunning);
        Assert.Equal(1.0, e.Equipment.Pitching);
    }

    [Fact]
    public void EquipmentLevel1_IsSmallerThanLevel2()
    {
        var small = new FacilitySet();
        small.SetLevel("gear_pitching", 1);
        var large = new FacilitySet();
        large.SetLevel("gear_pitching", 2);

        Assert.True(Catalog.AggregateEffects(small).Equipment.Pitching
                    < Catalog.AggregateEffects(large).Equipment.Pitching);
    }

    // --- 5. 備品: 買い切り1段 ---

    [Fact]
    public void Video_IsSingleLevelGear()
    {
        var def = Catalog.Find("video");
        Assert.NotNull(def);
        Assert.Equal(PurchaseKind.Gear, def!.Kind);
        Assert.Equal(1, def.MaxLevel);

        var set = new FacilitySet();
        set.SetLevel("video", 1);
        var e = Catalog.AggregateEffects(set);
        Assert.Equal(1.5, e.InsightProbFactor, 6);
        Assert.Equal(0.05, e.InsightAccuracyAdd, 6);
    }

    // --- 6. 購入経路は区分を問わず同じ（施設と同じ TryUpgrade で買える） ---

    [Fact]
    public void Purchase_WorksForEveryKind()
    {
        foreach (var f in Catalog.Facilities)
        {
            // 財布は区分で決まる（建物＝施設基金／道具・備品＝部費）ので両方に積んでおく。
            var m = new KokoSim.Engine.Career.Manager { Funds = 10_000, FacilityFund = 1_000_000 };
            var set = new FacilitySet();
            for (var lv = 1; lv <= f.MaxLevel; lv++)
                Assert.Equal(KokoSim.Engine.Career.FacilityPurchaseResult.Ok,
                    KokoSim.Engine.Career.FacilityPurchase.TryUpgrade(m, set, Catalog, f.Id));
            Assert.Equal(f.MaxLevel, set.LevelOf(f.Id));
            Assert.Equal(KokoSim.Engine.Career.FacilityPurchaseResult.AlreadyMaxLevel,
                KokoSim.Engine.Career.FacilityPurchase.TryUpgrade(m, set, Catalog, f.Id));
        }
    }
}
