using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>統計シム（CareerEngine）が実ゲームと同じ監督メタ経路を通ること（issue #1222）。</summary>
public sealed class CareerEngineParityTests
{
    private static readonly SchoolNameVocab Vocab = new();
    private static readonly NationCoefficients NationCoeff = new();
    private static readonly CareerCoefficients C = new();

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    [InlineData(99UL)]
    public void ForcedTransfer_StaysInSamePrefecture(ulong seed)
    {
        var t = CareerEngine.Run(28, new Manager(), Vocab, NationCoeff, C, new Xoshiro256Random(seed));
        var forced = 0;
        for (var i = 1; i < t.Years.Count; i++)
        {
            var y = t.Years[i];
            if (!y.Transferred || y.Status != ManagerStatus.Teacher) continue;
            forced++;
            Assert.Equal(t.Years[i - 1].Prefecture, y.Prefecture);
        }
        Assert.True(forced > 0, "強制転任が1度も起きないシードでは検証にならない");
    }

    [Fact]
    public void NewGameHasNoScoutContract_SoNoScoutCostIsDeducted()
    {
        var m = new Manager();
        var funds0 = m.Funds;
        var t = CareerEngine.Run(1, m, Vocab, NationCoeff, C, new Xoshiro256Random(5));
        var y = t.Years[0];
        Assert.False(y.Transferred);
        Assert.False(y.ReachedKoshien);
        var expected = funds0
            + CareerEngine.AnnualBudgetIncome(m.Trust, C)
            + CareerEngine.AnnualDonationIncome(m.Fame, y.ReachedKoshien, y.NationalChampion, C)
            + CareerEngine.TournamentPrizeIncome(y.Wins, 0, C)   // 大会報奨（issue #1469）。この年は甲子園に出ていない
            - CareerEngine.AnnualFixedExpense(0, C);
        Assert.Equal(System.Math.Max(0, expected), m.Funds, 6);
    }

    [Fact]
    public void YearlyUpdate_GoesThroughManagerSeasonUpdate()
    {
        var m = new Manager();
        var start = m.TeamManagerGrade;
        CareerEngine.Run(2, m, Vocab, NationCoeff, C, new Xoshiro256Random(5));
        Assert.Equal(start + 2, m.TeamManagerGrade);
    }
}
