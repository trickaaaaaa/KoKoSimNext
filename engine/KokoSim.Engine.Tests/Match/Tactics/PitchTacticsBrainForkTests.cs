using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 設計書15 Phase C-1 のFork隔離テスト（§5-2）。<see cref="IPitchTacticsBrain.CallPitchAction"/> が
/// 何を（どれだけRNGを）引いても、主RNGの消費列は不変であることを固定する。
/// </summary>
public sealed class PitchTacticsBrainForkTests
{
    /// <summary>
    /// 打席頭の采配はStandardと同じ既定（強攻/Default/伝令なし/交代なし）を返しつつ、1球ごとに
    /// Fork隔離されたRNGを大量に消費するが常にnull（無指示）を返す。主RNGに影響しなければ、
    /// Tactics=null のベースラインと試合結果が完全一致するはず。
    /// </summary>
    private sealed class ForkProbeBrain : ITacticsBrain, IPitchTacticsBrain
    {
        public int CallCount;

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
        {
            CallCount++;
            // Fork隔離されたストリームを好き放題消費しても、主RNGには波及しないはず。
            for (var i = 0; i < 7; i++) rng.NextDouble();
            rng.NextGaussian();
            rng.Fork(0xC0FFEEUL).NextUInt64();
            return null; // 無指示＝方針まかせ。この球のゲーム内挙動はTactics=nullと一致するはず。
        }
    }

    [Fact]
    public void CallPitchAction_HeavyRngUse_DoesNotAffectMainRngOrGameResult()
    {
        var ctx = new GameContext();

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var rngBaseline = new Xoshiro256Random(seed);
            var awayBaseline = StrengthTeamFactory.Create(52, "A", rngBaseline);
            var homeBaseline = StrengthTeamFactory.Create(52, "B", rngBaseline); // Tactics=null
            var baseline = GameEngine.Play(awayBaseline, homeBaseline, ctx, rngBaseline);

            var rngProbe = new Xoshiro256Random(seed);
            var awayProbe = StrengthTeamFactory.Create(52, "A", rngProbe);
            var probeBrain = new ForkProbeBrain();
            var homeProbe = StrengthTeamFactory.Create(52, "B", rngProbe) with { Tactics = probeBrain };
            var probe = GameEngine.Play(awayProbe, homeProbe, ctx, rngProbe);

            Assert.True(probeBrain.CallCount > 0, "probe brainの1球采配窓が一度も呼ばれていない（配線未達の疑い）");
            Assert.Equal(GameResultDigest.Sha256Of(baseline), GameResultDigest.Sha256Of(probe));
        }
    }
}
