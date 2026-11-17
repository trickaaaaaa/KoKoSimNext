using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 年契約の更新イベント（issue #1034）。出す週・出す条件（全部未契約の年は出さない）、
/// 何も触らなければ今と同じ額、選んだ段の年額が総括で引かれること、見込みが総括と同じ式であることを固定する。
/// </summary>
public sealed class ContractRenewalTests
{
    private static readonly ContractCatalog Catalog = ContractCatalog.Default;
    private static readonly SeasonCalendar Cal = new();

    private static ContractSet Signed(params (string id, int lv)[] levels)
    {
        var s = new ContractSet();
        foreach (var (id, lv) in levels) s.SetLevel(id, lv);
        return s;
    }

    // ── 出す週・出す条件 ───────────────────────────────────────────

    [Fact]
    public void RenewalWeek_IsTheWeekBeforeTheYearEndSummary()
        => Assert.Equal(Cal.ProspectConfirmWeek - 1, ContractRenewal.RenewalWeek(Cal));

    /// <summary>スカウトの念押し（確定週の2週前）とは別の週＝同じ週に会話が重ならない。</summary>
    [Fact]
    public void RenewalWeek_DoesNotCollideWithScoutDeadline()
        => Assert.NotEqual(ScoutMilestones.DeadlineWeek(Cal), ContractRenewal.RenewalWeek(Cal));

    [Fact]
    public void ShouldRaise_OnlyOnRenewalWeek()
    {
        var set = Signed(("expedition", 1));
        for (var w = 0; w < Cal.WeeksPerYear; w++)
            Assert.Equal(w == ContractRenewal.RenewalWeek(Cal), ContractRenewal.ShouldRaise(Cal, w, Catalog, set));
    }

    [Fact]
    public void ShouldRaise_NothingContracted_NeverFires()
    {
        var w = ContractRenewal.RenewalWeek(Cal);
        Assert.False(ContractRenewal.ShouldRaise(Cal, w, Catalog, new ContractSet()));
        Assert.False(ContractRenewal.ShouldRaise(Cal, w, Catalog, Signed(("scouting", 0), ("coach_batting", 0))));
    }

    /// <summary>スカウト網だけを契約している年は、更新を出さない（スカウト網は夏の会話で決める・issue #1064）。</summary>
    [Fact]
    public void ShouldRaise_OnlyScoutingContracted_DoesNotFire()
        => Assert.False(ContractRenewal.ShouldRaise(Cal, ContractRenewal.RenewalWeek(Cal), Catalog, Signed(("scouting", 3))));

    [Fact]
    public void ActiveContracts_AreOnlyThoseWithLevelOneOrMore_InCatalogOrder()
    {
        var set = Signed(("coach_defense", 1), ("scouting", 3), ("expedition", 0), ("coach_batting", 1));
        var ids = ContractRenewal.ActiveContracts(Catalog, set).Select(d => d.Id).ToArray();
        Assert.Equal(new[] { "coach_batting", "coach_defense" }, ids);   // スカウト網は入らない（issue #1064）
    }

    // ── 下書き ────────────────────────────────────────────────────

    /// <summary>既定は今の段のまま＝何も触らず閉じれば従来の自動更新と同じ額。</summary>
    [Fact]
    public void Draft_Untouched_KeepsCurrentLevelsAndCost()
    {
        var set = Signed(("scouting", 3), ("coach_batting", 1));
        var draft = new ContractRenewalDraft(Catalog, set);

        Assert.Equal(0, draft.LevelOf("scouting"));   // 更新の対象外
        Assert.Equal(1, draft.LevelOf("coach_batting"));
        Assert.Equal(Catalog.Aggregate(set).AnnualCost, draft.AnnualCost(set), 9);   // 対象外のスカウト網の年額も含む

        draft.ApplyTo(set);
        Assert.Equal(3, set.LevelOf("scouting"));   // 触らない
        Assert.Equal(1, set.LevelOf("coach_batting"));
    }

    [Fact]
    public void Draft_ChangingLevels_ChangesCost_ButNotTheSetUntilApplied()
    {
        var set = Signed(("scouting", 3), ("coach_batting", 1), ("expedition", 1));
        var draft = new ContractRenewalDraft(Catalog, set);

        draft.SetLevel("coach_batting", 0); // 50 → 0
        draft.SetLevel("scouting", 1);      // 対象外＝無視される

        Assert.Equal(140 + 20, draft.AnnualCost(set), 9);
        Assert.Equal(1, set.LevelOf("coach_batting"));   // 決めるまで契約は動かない

        draft.ApplyTo(set);
        Assert.Equal(0, set.LevelOf("coach_batting"));
        Assert.Equal(3, set.LevelOf("scouting"));
    }

    [Fact]
    public void Draft_ClampsLevel_AndIgnoresContractsThatWereNotActive()
    {
        var set = Signed(("expedition", 1));
        var draft = new ContractRenewalDraft(Catalog, set);

        draft.SetLevel("expedition", 99);
        Assert.Equal(Catalog.Find("expedition")!.MaxLevel, draft.LevelOf("expedition"));
        draft.SetLevel("expedition", -4);
        Assert.Equal(0, draft.LevelOf("expedition"));

        draft.SetLevel("coach_pitching", 1);   // 契約していなかった系統は、この場で新しく契約できない
        Assert.Equal(0, draft.LevelOf("coach_pitching"));
        draft.ApplyTo(set);
        Assert.Equal(0, set.LevelOf("coach_pitching"));
    }

    // ── 総括との接続 ──────────────────────────────────────────────

    private static double FundsAfterSummary(ContractSet set)
    {
        var m = new Manager { Funds = 500, Trust = 50, Fame = 10 };
        ManagerSeasonUpdate.Apply(m, false, false, 3, new Xoshiro256Random(1234), contracts: Catalog.Aggregate(set));
        return m.Funds;
    }

    /// <summary>更新で段を変えた年は、その段の年額が総括で引かれる。</summary>
    [Fact]
    public void Summary_ChargesTheRenewedLevel()
    {
        var set = Signed(("scouting", 3), ("coach_batting", 1), ("expedition", 1));
        var asIs = FundsAfterSummary(set);

        var draft = new ContractRenewalDraft(Catalog, set);
        draft.SetLevel("expedition", 0);    // −20
        draft.SetLevel("coach_batting", 0); // −50
        draft.ApplyTo(set);

        Assert.Equal(asIs + 70, FundsAfterSummary(set), 6);
    }

    /// <summary>何も変えなければ、総括で引かれる額は更新イベントが無かった年と同じ。</summary>
    [Fact]
    public void Summary_UntouchedRenewal_ChargesSameAsBefore()
    {
        var set = Signed(("scouting", 2), ("expedition", 1));
        var asIs = FundsAfterSummary(set);

        new ContractRenewalDraft(Catalog, set).ApplyTo(set);

        Assert.Equal(asIs, FundsAfterSummary(set), 9);
    }

    // ── 部費の見込み（経営タブの「部費 年間差引」と同じ式） ──────────────

    [Fact]
    public void AnnualFundsNet_IsIncomeMinusCampsAndContracts()
    {
        var c = new CareerCoefficients();
        var net = CareerEngine.AnnualFundsNet(trust: 50, fame: 40, contractCost: 140, c);
        var income = c.AnnualBudgetBase + c.BudgetPerTrust * 50                 // 学校予算＋信頼度ぶん
                     + c.SchoolInvestment + c.BoosterDonationPerFame * 40;      // 設備投資＋後援会・OB寄付（issue #1082）
        Assert.Equal(income - (c.SummerCampCost + c.WinterCampCost + 140), net, 9);
    }

    /// <summary>見込みは総括が実際に出す値と一致する（勝数0の年で、総括後の信頼度・名声を前提に突き合わせる）。</summary>
    [Fact]
    public void ProjectedFunds_MatchesTheSummaryWhenTrustDoesNotMove()
    {
        var c = new CareerCoefficients();
        var set = Signed(("scouting", 3));
        var contractCost = Catalog.Aggregate(set).AnnualCost;

        // 勝数0の年は信頼度が TrustPoorSeasonPenalty だけ下がる＝見込みの前提（総括前の信頼度）を総括後に合わせて比べる。
        var m = new Manager { Funds = 500, Trust = 50, Fame = 10 };
        ManagerSeasonUpdate.Apply(m, false, false, 0, new Xoshiro256Random(7), contracts: Catalog.Aggregate(set));

        Assert.Equal(m.Funds, CareerEngine.ProjectedFunds(500, m.Trust, m.Fame, contractCost, c), 6);
    }

    [Fact]
    public void ProjectedFunds_NeverGoesNegative()
    {
        var c = new CareerCoefficients();
        Assert.Equal(0, CareerEngine.ProjectedFunds(funds: 0, trust: 0, fame: 0, contractCost: 5000, c), 9);
    }

    // ── 年度末の寄付・一時金（issue #1082: 施設基金を部費へ統合・額は半分） ──────────────

    [Fact]
    public void AnnualDonationIncome_UsesTheHalvedAmounts()
    {
        var c = new CareerCoefficients();
        Assert.Equal(50 + 10 * 50, CareerEngine.AnnualDonationIncome(50, false, false, c), 9);
        Assert.Equal(50 + 10 * 50 + 500, CareerEngine.AnnualDonationIncome(50, true, false, c), 9);
        Assert.Equal(50 + 10 * 50 + 500 + 2500, CareerEngine.AnnualDonationIncome(50, true, true, c), 9);
    }

    /// <summary>総括で寄付・一時金が部費に入る（甲子園・全国優勝の年は一時金が乗る）。</summary>
    [Fact]
    public void Summary_PutsDonationsIntoFunds()
    {
        var c = new CareerCoefficients();
        var contracts = Catalog.Aggregate(Signed(("scouting", 1)));

        double FundsAfter(bool koshien, bool champion)
        {
            var m = new Manager { Funds = 0, Trust = 50, Fame = 50 };
            ManagerSeasonUpdate.Apply(m, koshien, champion, 5, new Xoshiro256Random(7), contracts: contracts);
            return m.Funds;
        }

        var plain = FundsAfter(false, false);
        Assert.True(plain > 0);
        // 名声は総括で動くので、一時金だけを差で取り出す（同じシード・同じ勝数＝それ以外は同じ）
        var koshienYear = FundsAfter(true, false);
        var titleYear = FundsAfter(true, true);
        Assert.True(koshienYear - plain >= c.KoshienYearBonus - 1e-6, $"甲子園の年の差 {koshienYear - plain}");
        Assert.True(titleYear - koshienYear >= c.NationalTitleBonus - 1e-6, $"全国優勝の年の差 {titleYear - koshienYear}");
    }
}
