using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 指導力の自然増は格上・大舞台ほど大きい（設計書04 §1.1・issue #1484）。
/// 節目イベントは勝数・甲子園到達にしか依存しないので、同じ入力で金星／甲子園勝ちだけを変えれば差は自然増の分だけ。
/// </summary>
public sealed class CoachingNaturalGrowthTests
{
    private static readonly CareerCoefficients C = new();

    private static Manager Season(int wins, int nationalStageWins, double upsetFameGain)
    {
        var m = new Manager();
        ManagerSeasonUpdate.Apply(m, reachedKoshien: false, nationalChampion: false, wins: wins,
            milestoneRng: new Xoshiro256Random(7), careerCoeff: C,
            upsetFameGain: upsetFameGain, nationalStageWins: nationalStageWins);
        return m;
    }

    [Fact]
    public void NationalStageWins_GrowCoachingMoreThanSameWinsAtHome()
    {
        var plain = Season(wins: 5, nationalStageWins: 0, upsetFameGain: 0);
        var koshien = Season(wins: 5, nationalStageWins: 3, upsetFameGain: 0);

        Assert.True(koshien.CoachingBatting > plain.CoachingBatting);
        Assert.True(koshien.TacticalSense > plain.TacticalSense);
        Assert.True(koshien.TalentEye > plain.TalentEye);
    }

    [Fact]
    public void UpsetWins_GrowCoachingMoreThanOrderlyWins()
    {
        var orderly = Season(wins: 5, nationalStageWins: 0, upsetFameGain: 0);
        var upset = Season(wins: 5, nationalStageWins: 0, upsetFameGain: 6);

        Assert.True(upset.CoachingPitching > orderly.CoachingPitching);
        Assert.True(upset.CoachingDefense > orderly.CoachingDefense);
    }

    [Fact]
    public void GrowthExtra_MatchesCoefficients_ForBattingCoaching()
    {
        var plain = Season(wins: 5, nationalStageWins: 0, upsetFameGain: 0);
        var big = Season(wins: 5, nationalStageWins: 2, upsetFameGain: 4);

        var headroom = (C.CoachingCap - 20.0) / C.CoachingCap;
        var expectedExtra = (C.CoachingGrowthPerNationalStageWin * 2 + C.CoachingGrowthPerUpsetFame * 4) * headroom;
        Assert.Equal(expectedExtra, big.CoachingBatting - plain.CoachingBatting, 6);
    }

    [Fact]
    public void NoExtras_KeepsTheOldFormula()
    {
        var m = Season(wins: 5, nationalStageWins: 0, upsetFameGain: 0);
        var headroom = (C.CoachingCap - 20.0) / C.CoachingCap;
        var expected = 20.0 + (C.CoachingGrowthPerYear + C.CoachingGrowthPerWin * 5) * headroom;
        Assert.Equal(expected, m.CoachingBatting, 6);
    }

    [Fact]
    public void CoachingStaysUnderTheCap_EvenWithHugeExperience()
    {
        var m = new Manager { CoachingBatting = 98.5 };
        ManagerSeasonUpdate.Apply(m, reachedKoshien: true, nationalChampion: true, wins: 15,
            milestoneRng: new Xoshiro256Random(3), careerCoeff: C, upsetFameGain: 8, nationalStageWins: 6);
        Assert.True(m.CoachingBatting <= C.CoachingCap);
    }
}
