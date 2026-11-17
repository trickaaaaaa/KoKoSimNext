using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>スカウトの一年の入口（契約→分野）の選択肢（issue #1064）。年額・枠数は契約カタログから引く。</summary>
public sealed class ScoutPitchTests
{
    [Fact]
    public void ContractChoices_AreNoContractThenEachTier_WithCostAndSlotsFromCatalog()
    {
        var catalog = ContractCatalog.Default;
        var def = catalog.Find(ScoutPitch.ContractId)!;
        var choices = ScoutPitch.ContractChoices(catalog);

        Assert.Equal(def.MaxLevel + 1, choices.Count);
        for (var lv = 0; lv < choices.Count; lv++)
        {
            Assert.Equal(lv, choices[lv].Level);
            Assert.Equal(def.Tiers[lv].AnnualCost, choices[lv].AnnualCost);
            Assert.Equal(def.Tiers[lv].ScoutSlots, choices[lv].Slots);
        }
    }

    [Fact]
    public void ContractChoices_LabelsCarryAnnualCostAndSlots()
    {
        var choices = ScoutPitch.ContractChoices(ContractCatalog.Default);
        Assert.Equal("契約しない（0万）", choices[0].Label);
        Assert.Equal("Lv1（30万・枠2人）", choices[1].Label);
        Assert.Equal("Lv2（80万・枠3人）", choices[2].Label);
        Assert.Equal("Lv3（140万・枠4人）", choices[3].Label);
    }

    [Fact]
    public void ContractChoices_NoScoutingInCatalog_IsEmpty()
        => Assert.Empty(ScoutPitch.ContractChoices(new ContractCatalog { Contracts = new[] { ContractCatalog.Default.Find("expedition")! } }));

    [Fact]
    public void FieldChoices_CoverEveryScoutField_OnceEach()
    {
        var fields = ScoutPitch.FieldChoices.Select(c => c.Field).ToList();
        Assert.Equal(System.Enum.GetValues<ScoutField>().OrderBy(f => f), fields.OrderBy(f => f));
        Assert.Equal(fields.Count, fields.Distinct().Count());
        Assert.Equal(ScoutField.Omakase, fields[0]);
    }

    [Fact]
    public void ResultSentence_NamesLevelCostSlotsAndField()
    {
        var c = ScoutPitch.ContractChoices(ContractCatalog.Default)[2];
        Assert.Equal("スカウト網 Lv2（年80万・枠3人）　探す分野: 投球", ScoutPitch.ResultSentence(c, ScoutField.Pitching));
    }

    /// <summary>更新イベントの対象から外したスカウト網は、夏の入口のカタログ引きと同じIDで繋がっている。</summary>
    [Fact]
    public void ContractId_IsInTheDefaultCatalog_AndNotRenewable()
    {
        Assert.NotNull(ContractCatalog.Default.Find(ScoutPitch.ContractId));
        var set = new ContractSet();
        set.SetLevel(ScoutPitch.ContractId, 3);
        Assert.Empty(ContractRenewal.ActiveContracts(ContractCatalog.Default, set));
    }
}
