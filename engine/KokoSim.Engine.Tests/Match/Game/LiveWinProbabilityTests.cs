using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

public sealed class LiveWinProbabilityTests
{
    private static double P(int away, int home, int inning, bool isTop, int outs = 0,
        bool b1 = false, bool b2 = false, bool b3 = false, double prior = 0.5)
        => LiveWinProbability.HomeWinProbability(away, home, inning, isTop, outs, b1, b2, b3, homePrior: prior);

    [Fact]
    public void GameStart_EqualsThePriorExactly()
    {
        Assert.Equal(0.5, P(0, 0, 1, true), 6);
        Assert.Equal(0.8, P(0, 0, 1, true, prior: 0.8), 6);
        Assert.Equal(0.3, P(0, 0, 1, true, prior: 0.3), 6);
    }

    [Fact]
    public void LateLead_IsDecisive_AndEarlyLead_IsNot()
    {
        Assert.True(P(0, 3, 8, false) > 0.9);
        var early = P(0, 3, 1, false);
        Assert.InRange(early, 0.6, 0.85);
    }

    [Fact]
    public void LastChanceDown_ByThreeWithTwoOutAndNobodyOn_IsAlmostLost()
        => Assert.True(P(3, 0, 9, false, outs: 2) < 0.02);

    [Fact]
    public void MoreHomeRuns_RaiseTheProbability()
    {
        Assert.True(P(2, 3, 6, true) > P(2, 2, 6, true));
        Assert.True(P(2, 2, 6, true) > P(2, 1, 6, true));
    }

    [Fact]
    public void RunnersHelpTheBattingSide_AndOutsHurtIt()
    {
        Assert.True(P(2, 2, 9, false, outs: 1, b2: true, b3: true) > P(2, 2, 9, false, outs: 1));
        Assert.True(P(2, 2, 9, true, outs: 1, b2: true, b3: true) < P(2, 2, 9, true, outs: 1));
        Assert.True(P(2, 2, 9, false, outs: 0) > P(2, 2, 9, false, outs: 2));
    }

    [Fact]
    public void ExtraInnings_StayInsideTheOpenInterval()
    {
        var p = P(4, 4, 11, true);
        Assert.InRange(p, 0.05, 0.95);
        Assert.True(P(4, 5, 11, false) > p);
    }

    [Fact]
    public void SameInputs_GiveTheSameAnswer()
        => Assert.Equal(P(1, 2, 5, false, 1, true), P(1, 2, 5, false, 1, true));

    [Fact]
    public void FromLastPlay_EmptyLog_IsGameStart()
        => Assert.Equal(0.5, LiveWinProbability.HomeWinProbability(null, 0, 0), 6);

    [Fact]
    public void FromLastPlay_ThirdOutOfTheTop_MovesToTheBottomOfTheSameInning()
    {
        var last = new PlayLogEntry(6, true, "打者", PlateAppearanceResult.InPlayOut, 0, OutsBefore: 2, OutsThisPa: 1);
        Assert.Equal(P(1, 3, 6, false), LiveWinProbability.HomeWinProbability(last, 1, 3), 9);
    }

    [Fact]
    public void FromLastPlay_ThirdOutOfTheBottom_MovesToTheTopOfTheNextInning()
    {
        var last = new PlayLogEntry(6, false, "打者", PlateAppearanceResult.Strikeout, 0, OutsBefore: 2, OutsThisPa: 1);
        Assert.Equal(P(1, 3, 7, true), LiveWinProbability.HomeWinProbability(last, 1, 3), 9);
    }

    [Fact]
    public void FromLastPlay_MidInning_KeepsOutsAndRunners()
    {
        var last = new PlayLogEntry(5, false, "打者", PlateAppearanceResult.Single, 0,
            OutsBefore: 1, BaseFirstAfter: true, BaseThirdAfter: true);
        Assert.Equal(P(2, 2, 5, false, 1, b1: true, b3: true), LiveWinProbability.HomeWinProbability(last, 2, 2), 9);
    }
}
