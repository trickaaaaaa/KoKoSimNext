using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

public class TournamentPressureTests
{
    [Theory]
    [InlineData(TournamentKind.Summer, 5, 1)]
    [InlineData(TournamentKind.Autumn, 3, 1)]
    [InlineData(TournamentKind.AutumnDistrict, 2, 1)]
    [InlineData(TournamentKind.Regional, 2, 1)]
    [InlineData(TournamentKind.SummerKoshien, 6, 2)]
    [InlineData(TournamentKind.SenbatsuKoshien, 4, 2)]
    [InlineData(TournamentKind.Jingu, 3, 2)]
    public void StageBonus_ByKind(TournamentKind kind, int roundsRemaining, int expected)
        => Assert.Equal(expected, TournamentPressure.StageBonus(kind, roundsRemaining));

    [Theory]
    [InlineData(TournamentKind.Summer)]
    [InlineData(TournamentKind.SummerKoshien)]
    [InlineData(TournamentKind.Autumn)]
    [InlineData(TournamentKind.Jingu)]
    public void StageBonus_FinalIsThree_AnyTournament(TournamentKind kind)
        => Assert.Equal(3, TournamentPressure.StageBonus(kind, 1));

    [Fact]
    public void StageBonus_LeagueConstantIsNotFinal()
        => Assert.Equal(1, TournamentPressure.StageBonus(TournamentKind.AutumnDistrict, BlockLeagueRunner.LeagueRoundsRemaining));

    [Fact]
    public void StageBonus_NoContext_IsNotFinal()
        => Assert.Equal(1, TournamentPressure.StageBonus(TournamentKind.Summer, null));

    [Theory]
    [InlineData(TournamentKind.Summer, true)]
    [InlineData(TournamentKind.SummerKoshien, true)]
    [InlineData(TournamentKind.Autumn, false)]
    [InlineData(TournamentKind.SenbatsuKoshien, false)]
    [InlineData(TournamentKind.Regional, false)]
    [InlineData(TournamentKind.Jingu, false)]
    public void RetirementOnLine_OnlyFinalSummer(TournamentKind kind, bool expected)
        => Assert.Equal(expected, TournamentPressure.RetirementOnLine(kind));

    [Fact]
    public void Apply_SetsBothFields_AndKeepsOthers()
    {
        var ctx = TournamentPressure.Apply(new GameContext { MercyRuleEnabled = true }, TournamentKind.SummerKoshien, 1);
        Assert.Equal(3, ctx.PressureStageBonus);
        Assert.True(ctx.RetirementOnLine);
        Assert.True(ctx.MercyRuleEnabled);
    }

    [Fact]
    public void Pressure_FinalSummerLateCloseRisp_ReachesCap()
    {
        var c = new PressureCoefficients();
        var ctx = TournamentPressure.Apply(new GameContext(), TournamentKind.Summer, 1);
        var p = PressureModel.Compute(
            new PressureSituation(ctx.PressureStageBonus, 9, 1, true, false, false, ctx.RetirementOnLine), c);
        Assert.Equal(System.Math.Min(c.MaxIndex, 3 + 1 + 1 + c.RispPoint + c.RetirementPoint), p);
    }
}
