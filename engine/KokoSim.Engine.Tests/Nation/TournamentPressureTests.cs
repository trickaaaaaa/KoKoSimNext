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

    // 練習試合（大会の文脈なし）は GameSession.Kind に直前の大会の値（既定 Summer）が残っていても加点しない（PR #1555 レビュー）。
    [Theory]
    [InlineData(TournamentKind.Summer)]
    [InlineData(TournamentKind.SummerKoshien)]
    public void Apply_NoMatchContext_IsPracticeMatch_NoBonus(TournamentKind staleKind)
    {
        var ctx = TournamentPressure.Apply(new GameContext { MercyRuleEnabled = true }, staleKind, null);
        Assert.Equal(new GameContext().PressureStageBonus, ctx.PressureStageBonus);
        Assert.False(ctx.RetirementOnLine);
        Assert.True(ctx.MercyRuleEnabled);
    }

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
        var ctx = TournamentPressure.Apply(new GameContext { MercyRuleEnabled = true }, TournamentKind.SummerKoshien, Final());
        Assert.Equal(3, ctx.PressureStageBonus);
        Assert.True(ctx.RetirementOnLine);
        Assert.True(ctx.MercyRuleEnabled);
    }

    [Fact]
    public void Pressure_FinalSummerLateCloseRisp_ReachesCap()
    {
        var c = new PressureCoefficients();
        var ctx = TournamentPressure.Apply(new GameContext(), TournamentKind.Summer, Final());
        var p = PressureModel.Compute(
            new PressureSituation(ctx.PressureStageBonus, 9, 1, true, false, false, ctx.RetirementOnLine), c);
        Assert.Equal(System.Math.Min(c.MaxIndex, 3 + 1 + 1 + c.RispPoint + c.RetirementPoint), p);
    }

    private static TournamentMatchContext Final() => new(RoundsRemaining: 1, MatchDay: 1, Ledger: new TournamentPitchLedger());
}
