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
        var set = Signed(("scouting", 2));
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

    [Fact]
    public void ActiveContracts_AreOnlyThoseWithLevelOneOrMore_InCatalogOrder()
    {
        var set = Signed(("coach_defense", 1), ("scouting", 3), ("expedition", 0));
        var ids = ContractRenewal.ActiveContracts(Catalog, set).Select(d => d.Id).ToArray();
        Assert.Equal(new[] { "scouting", "coach_defense" }, ids);
    }

    // ── 下書き ────────────────────────────────────────────────────

    /// <summary>既定は今の段のまま＝何も触らず閉じれば従来の自動更新と同じ額。</summary>
    [Fact]
    public void Draft_Untouched_KeepsCurrentLevelsAndCost()
    {
        var set = Signed(("scouting", 3), ("coach_batting", 1));
        var draft = new ContractRenewalDraft(Catalog, set);

        Assert.Equal(3, draft.LevelOf("scouting"));
        Assert.Equal(1, draft.LevelOf("coach_batting"));
        Assert.Equal(Catalog.Aggregate(set).AnnualCost, draft.AnnualCost(set), 9);

        draft.ApplyTo(set);
        Assert.Equal(3, set.LevelOf("scouting"));
        Assert.Equal(1, set.LevelOf("coach_batting"));
    }

    [Fact]
    public void Draft_ChangingLevels_ChangesCost_ButNotTheSetUntilApplied()
    {
        var set = Signed(("scouting", 3), ("coach_batting", 1));
        var draft = new ContractRenewalDraft(Catalog, set);

        draft.SetLevel("scouting", 1);     // 140 → 30
        draft.SetLevel("coach_batting", 0); // 50 → 0

        Assert.Equal(30, draft.AnnualCost(set), 9);
        Assert.Equal(3, set.LevelOf("scouting"));   // 決めるまで契約は動かない
        Assert.Equal(1, set.LevelOf("coach_batting"));

        draft.ApplyTo(set);
        Assert.Equal(1, set.LevelOf("scouting"));
        Assert.Equal(0, set.LevelOf("coach_batting"));
    }

    [Fact]
    public void Draft_ClampsLevel_AndIgnoresContractsThatWereNotActive()
    {
        var set = Signed(("scouting", 2));
        var draft = new ContractRenewalDraft(Catalog, set);

        draft.SetLevel("scouting", 99);
        Assert.Equal(Catalog.Find("scouting")!.MaxLevel, draft.LevelOf("scouting"));
        draft.SetLevel("scouting", -4);
        Assert.Equal(0, draft.LevelOf("scouting"));

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
        var set = Signed(("scouting", 3), ("coach_batting", 1));
        var asIs = FundsAfterSummary(set);

        var draft = new ContractRenewalDraft(Catalog, set);
        draft.SetLevel("scouting", 1);      // −110
        draft.SetLevel("coach_batting", 0); // −50
        draft.ApplyTo(set);

        Assert.Equal(asIs + 160, FundsAfterSummary(set), 6);
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
        var net = CareerEngine.AnnualFundsNet(trust: 50, contractCost: 140, c);
        Assert.Equal(c.AnnualBudgetBase + c.BudgetPerTrust * 50 - (c.SummerCampCost + c.WinterCampCost + 140), net, 9);
    }

    /// <summary>見込みは総括が実際に出す値と一致する（信頼度が総括で動かない・勝数0の年で突き合わせる）。</summary>
    [Fact]
    public void ProjectedFunds_MatchesTheSummaryWhenTrustDoesNotMove()
    {
        var c = new CareerCoefficients();
        var set = Signed(("scouting", 3));
        var contractCost = Catalog.Aggregate(set).AnnualCost;

        // 勝数0の年は信頼度が TrustPoorSeasonPenalty だけ下がる＝見込みの前提（総括前の信頼度）を総括後に合わせて比べる。
        var m = new Manager { Funds = 500, Trust = 50, Fame = 10 };
        ManagerSeasonUpdate.Apply(m, false, false, 0, new Xoshiro256Random(7), contracts: Catalog.Aggregate(set));

        Assert.Equal(m.Funds, CareerEngine.ProjectedFunds(500, m.Trust, contractCost, c), 6);
    }

    [Fact]
    public void ProjectedFunds_NeverGoesNegative()
    {
        var c = new CareerCoefficients();
        Assert.Equal(0, CareerEngine.ProjectedFunds(funds: 0, trust: 0, contractCost: 1000, c), 9);
    }
}
