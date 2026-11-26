using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// issue #1644 の固定テスト。半イニング頭の守備固め（<see cref="ITacticsBrain.CallDefensiveSub"/>）と
/// 打席頭の代走・代打（CallPinchRun/CallPinchHit）・守備指示・守備伝令・攻撃伝令・攻撃サイン
/// （CallDefense/CallDefenseTimeout/CallOffenseTimeout/CallOffense）は、以前は本流rngを直接引いていた
/// ため、判断ロジックの内部でRNGの消費量を1つ変えるだけで以降のすべての打席（無関係な打席も含む）の
/// 結果が変わってしまっていた。専用Forkストリームから引くよう直した後は、判断内部で何をどれだけ
/// 引いても本流の列・試合結果は Tactics=null のベースラインと完全一致するはず（不変条件#2）。
/// </summary>
public sealed class TacticsDecisionForkIsolationTests
{
    /// <summary>
    /// 呼ばれるたびに大量にRNGを消費するが、返す決定は常に「無指示」（Tactics=null と同値）。
    /// 本流に波及しなければ、このBrainを挿した試合も Tactics=null の試合と1ビットも変わらない。
    /// </summary>
    private sealed class HeavyNoopBrain : ITacticsBrain
    {
        public int DefenseCalls, OffenseCalls, DefenseTimeoutCalls, OffenseTimeoutCalls;
        public int PinchHitCalls, PinchRunCalls, DefensiveSubCalls;

        /// <summary>判断ロジックが「気が変わる」ことを模す＝好きなだけRNGを引く。本流には波及しないはず。</summary>
        private static void Burn(IRandomSource rng)
        {
            for (var i = 0; i < 11; i++) rng.NextDouble();
            rng.NextGaussian();
            rng.Fork(0xBEEFUL).NextUInt64();
        }

        public OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng)
        {
            OffenseCalls++;
            Burn(rng);
            return OffensiveSign.Swing;
        }

        public DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng)
        {
            DefenseCalls++;
            Burn(rng);
            return DefensiveTactics.Default;
        }

        public bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng)
        {
            OffenseTimeoutCalls++;
            Burn(rng);
            return false;
        }

        public bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng)
        {
            DefenseTimeoutCalls++;
            Burn(rng);
            return false;
        }

        public Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng)
        {
            PinchHitCalls++;
            Burn(rng);
            return null;
        }

        public (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng)
        {
            PinchRunCalls++;
            Burn(rng);
            return null;
        }

        public (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng)
        {
            DefensiveSubCalls++;
            Burn(rng);
            return null;
        }

        public PitchingChangeDecision? CallPitchingChange(in PitchingChangeSituation s, IRandomSource rng)
        {
            // 継投（issue #209）は既に専用Forkで引いている。ここでも気前よく消費して退行していないことを併せて見る。
            Burn(rng);
            // Tactics=null の場合engineが疲労/週間上限で自動継投する（MaybeChangePitcher）。無指示（null）を
            // 返すとその自動継投が起きなくなってしまうので、ここは PitchTacticsBrainForkTests のProbeBrainと
            // 同じく「自動継投と同値」を返す＝Tactics=null ベースラインと完全一致させる。
            return s.FatigueTriggered || s.AtWeeklyLimit ? new PitchingChangeDecision(PitchingChangeReason.Fatigue) : null;
        }
    }

    [Fact]
    public void HeavyInternalRngUse_DoesNotAffectMainRngOrGameResult()
    {
        var ctx = new GameContext();

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var rngBaseline = new Xoshiro256Random(seed);
            var awayBaseline = StrengthTeamFactory.Create(55, "A", rngBaseline);
            var homeBaseline = StrengthTeamFactory.Create(55, "B", rngBaseline); // Tactics=null
            var baseline = GameEngine.Play(awayBaseline, homeBaseline, ctx, rngBaseline);

            var rngProbe = new Xoshiro256Random(seed);
            var awayProbe = StrengthTeamFactory.Create(55, "A", rngProbe);
            var brain = new HeavyNoopBrain();
            var homeProbe = StrengthTeamFactory.Create(55, "B", rngProbe) with { Tactics = brain };
            var probe = GameEngine.Play(awayProbe, homeProbe, ctx, rngProbe);

            // 配線未達（Forkが刺さっていない／呼ばれていない）だと以下の検知が無意味になるので、
            // 実際に全判断が呼ばれたことをまず確かめる。
            Assert.True(brain.DefenseCalls > 0, "CallDefenseが一度も呼ばれていない（配線未達の疑い）");
            Assert.True(brain.OffenseCalls > 0, "CallOffenseが一度も呼ばれていない（配線未達の疑い）");
            Assert.True(brain.DefensiveSubCalls > 0, "CallDefensiveSubが一度も呼ばれていない（配線未達の疑い）");
            Assert.True(brain.PinchRunCalls > 0 || brain.PinchHitCalls > 0,
                "CallPinchRun/CallPinchHitが一度も呼ばれていない（配線未達の疑い）");

            Assert.Equal(GameResultDigest.Sha256Of(baseline), GameResultDigest.Sha256Of(probe));
        }
    }
}
