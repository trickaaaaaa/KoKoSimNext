using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 大会報奨（公式戦1勝ごと）とプロ入りOBの一時金（設計書04 §1.4・issue #1469 T1/O2・2026-11-23 オーナー決定）。
/// </summary>
public sealed class TournamentPrizeAndProDonationTests
{
    private static readonly CareerCoefficients C = new();

    [Fact]
    public void Prize_IsTenPerWin_AndThirtyPerNationalStageWin()
    {
        Assert.Equal(0, CareerEngine.TournamentPrizeIncome(0, 0, C), 9);
        Assert.Equal(10 * 5, CareerEngine.TournamentPrizeIncome(5, 0, C), 9);
        // 9勝のうち甲子園の2勝: 7×10 + 2×30。
        Assert.Equal(7 * 10 + 2 * 30, CareerEngine.TournamentPrizeIncome(9, 2, C), 9);
    }

    [Fact]
    public void Prize_ClampsNationalStageWinsToOfficialWins()
    {
        Assert.Equal(3 * 30, CareerEngine.TournamentPrizeIncome(3, 5, C), 9);
        Assert.Equal(0, CareerEngine.TournamentPrizeIncome(-1, 0, C), 9);
    }

    [Fact]
    public void YearEnd_AddsThePrizeToFunds()
    {
        Manager After(int wins, int national)
        {
            var m = new Manager { Funds = 500, Trust = 50, Fame = 20 };
            ManagerSeasonUpdate.Apply(m, false, false, wins, new Xoshiro256Random(3), C,
                contracts: ContractEffects.None, nationalStageWins: national);
            return m;
        }

        // 信頼度・名声は勝ち数で動くので、同じ勝ち数で甲子園の勝ちだけを変えて比べる＝差は単価の差だけ。
        var plain = After(wins: 6, national: 0);
        var koshien = After(wins: 6, national: 2);
        Assert.Equal(plain.Fame, koshien.Fame, 9);
        Assert.Equal(2 * (C.PrizePerNationalStageWin - C.PrizePerOfficialWin), koshien.Funds - plain.Funds, 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 100)]
    [InlineData(2, 70)]
    [InlineData(3, 70)]
    [InlineData(4, 50)]
    [InlineData(7, 50)]
    [InlineData(8, 30)]
    public void ProDonation_ByRound(int round, double expected)
        => Assert.Equal(expected, CareerEngine.ProDraftDonation(round, C), 9);
}
