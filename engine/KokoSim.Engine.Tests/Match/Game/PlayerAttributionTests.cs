using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #91: 盗塁・失策を選手個人へ帰属させる記録の検証。判定ロジック（StealResolver/FieldingResolver）は
/// 変更していないため、ここではチーム計との整合と決定論の維持のみを確認する。
/// </summary>
public sealed class PlayerAttributionTests
{
    private static Player Pos(FieldPosition pos, int speed = 50, int steal = 50, int catching = 50) => new()
    {
        Position = pos, Contact = 50, Power = 50, Speed = speed, Steal = steal, Catching = catching,
    };

    private static Team TeamOf(string name, ITacticsBrain? tactics = null, int catching = 50)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher, catching: catching), Pos(FieldPosition.FirstBase, speed: 85, steal: 85, catching: catching),
            Pos(FieldPosition.SecondBase, speed: 85, steal: 85, catching: catching), Pos(FieldPosition.ThirdBase, catching: catching),
            Pos(FieldPosition.Shortstop, catching: catching), Pos(FieldPosition.LeftField, catching: catching),
            Pos(FieldPosition.CenterField, speed: 85, steal: 85, catching: catching), Pos(FieldPosition.RightField, catching: catching),
            new() { Name = name + "P", Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage, Catching = catching },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Tactics = tactics };
    }

    /// <summary>一塁走者・二塁空きの間、常に盗塁を試みるブレイン（StealUnificationTests と同型）。</summary>
    private sealed class AlwaysStealBrain : ITacticsBrain, IPitchTacticsBrain
    {
        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng) => OffensiveSign.Swing;
        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng) => DefensiveTactics.Default;
        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng) => false;
        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng) => null;
        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng) => null;
        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
            => s.FatigueTriggered || s.AtWeeklyLimit ? new PitchingChangeDecision(PitchingChangeReason.Fatigue) : null;

        public PitchTacticsDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng)
            => s.Base.OnFirst is not null && s.Base.OnSecond is null
                ? new PitchTacticsDirective(StealAttempt: StartType.Normal)
                : null;
    }

    // ===== TeamState 単体（盗塁） =====

    [Fact]
    public void RecordSteal_AttributesSuccessAndFailure_ToRunner()
    {
        var team = TeamOf("A");
        var runner = team.BattingOrder[1];
        var state = new TeamState(team);

        state.RecordSteal(runner, success: true);
        state.RecordSteal(runner, success: false);

        var line = state.BuildBattingLines().Single(l => l.Position == runner.Position);
        Assert.Equal(1, line.StolenBases);
        Assert.Equal(1, line.CaughtStealing);
        Assert.Equal(2, state.StealAttempts);
        Assert.Equal(1, state.StealSuccesses);
    }

    // ===== TeamState 単体（失策） =====

    [Fact]
    public void RecordFieldingError_AttributesToFielder_AndMatchesTeamTotal()
    {
        var team = TeamOf("A");
        var shortstop = team.BattingOrder.Single(p => p.Position == FieldPosition.Shortstop);
        var state = new TeamState(team);

        state.RecordFieldingError(shortstop, FieldPosition.Shortstop);
        state.RecordFieldingError(shortstop, FieldPosition.Shortstop);

        var lines = state.BuildFieldingLines();
        var line = Assert.Single(lines);
        Assert.Equal(shortstop.Name, line.Name);
        Assert.Equal(FieldPosition.Shortstop, line.Position);
        Assert.Equal(2, line.Errors);
        Assert.Equal(2, state.Errors);
    }

    [Fact]
    public void RecordFieldingError_WithUnresolvedFielder_StillCountsTeamTotal_ButNoIndividualLine()
    {
        var state = new TeamState(TeamOf("A"));
        state.RecordFieldingError(null, FieldPosition.Shortstop);

        Assert.Equal(1, state.Errors);
        Assert.Empty(state.BuildFieldingLines());
    }

    // ===== GameEngine 統合: 個人集計の合計がチーム計と一致する（完了条件） =====

    [Fact]
    public void FullGames_IndividualStolenBaseTotals_MatchTeamTactics()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var home = TeamOf("H", new AlwaysStealBrain());
            var away = TeamOf("A");
            var r = GameEngine.Play(away, home, new GameContext(), rng);

            var sb = r.HomeBatting.Sum(b => b.StolenBases);
            var cs = r.HomeBatting.Sum(b => b.CaughtStealing);
            Assert.Equal(r.HomeTactics.StealSuccesses, sb);
            Assert.Equal(r.HomeTactics.StealAttempts - r.HomeTactics.StealSuccesses, cs);
        }
    }

    [Fact]
    public void FullGames_IndividualFieldingErrorTotals_MatchTeamErrors()
    {
        var errorSeen = false;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            // 捕球1（クランプ後の最大失策率）で失策を頻発させ、帰属の整合を強く検証する。
            var home = TeamOf("H", catching: 1);
            var away = TeamOf("A", catching: 1);
            var r = GameEngine.Play(away, home, new GameContext(), rng);

            if (r.AwayErrors > 0 || r.HomeErrors > 0) errorSeen = true;
            Assert.Equal(r.AwayErrors, r.AwayFielding.Sum(f => f.Errors));
            Assert.Equal(r.HomeErrors, r.HomeFielding.Sum(f => f.Errors));
        }
        Assert.True(errorSeen, "低捕球設定でも一度も失策が発生しなかった（帰属検証が意味を持たない）");
    }

    [Fact]
    public void SameSeed_ProducesIdenticalStolenBaseAndErrorAttribution()
    {
        GameResult Run()
        {
            var rng = new Xoshiro256Random(777);
            var home = TeamOf("H", new AlwaysStealBrain(), catching: 1);
            var away = TeamOf("A", catching: 1);
            return GameEngine.Play(away, home, new GameContext(), rng);
        }

        var a = Run();
        var b = Run();

        Assert.Equal(a.HomeBatting.Select(l => (l.Name, l.StolenBases, l.CaughtStealing)),
            b.HomeBatting.Select(l => (l.Name, l.StolenBases, l.CaughtStealing)));
        Assert.Equal(a.AwayFielding.Select(l => (l.Name, l.Errors)), b.AwayFielding.Select(l => (l.Name, l.Errors)));
        Assert.Equal(a.HomeFielding.Select(l => (l.Name, l.Errors)), b.HomeFielding.Select(l => (l.Name, l.Errors)));
    }
}
