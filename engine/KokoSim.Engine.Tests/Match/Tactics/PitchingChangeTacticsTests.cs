using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 継投の三層采配化（issue #209 Phase A, 設計書11 §4）。継投を <see cref="ITacticsBrain.CallPitchingChange"/> に載せ、
/// Standard＝従来の疲労球数トリガーそのまま（恒等・RNG不使用）、AI＝高度トリガー（崩れ・僅差終盤・動揺）＋三層を
/// 被せる。三層AIの効果は本テスト（AI vs AI 相当の直接検証＋GameEngine 結線）で確認し、既定係数（MinTier=99）では
/// 一切発火しない＝games_10k/tactics 帯・決定論baseline を1ビットも動かさないこと（D1=(A) 帯不変）を保証する。
/// </summary>
public sealed class PitchingChangeTacticsTests
{
    private static PitchingChangeSituation Sit(
        bool fatigueTriggered = false, bool atWeekly = false, bool reliefAvailable = true,
        int runsThisInning = 0, bool rattled = false, int inning = 5, int scoreDiff = 0,
        double fatiguePitches = 0, double staminaTarget = 90, double relieveMargin = 25)
        => new(inning, 9, 0, scoreDiff, runsThisInning, rattled, fatigueTriggered, atWeekly,
               fatiguePitches, staminaTarget, relieveMargin, reliefAvailable);

    /// <summary>異なるシードの独立rngで n 回問い合わせ、継投が返った回数を数える（①能力値ミスの分布を見る）。</summary>
    private static int CountFires(ITacticsBrain brain, PitchingChangeSituation s, int n = 300)
    {
        var count = 0;
        for (ulong seed = 1; seed <= (ulong)n; seed++)
        {
            if (brain.CallPitchingChange(s, new Xoshiro256Random(seed)) is not null) count++;
        }
        return count;
    }

    // ===== Standard: 従来トリガーの恒等再現（RNG不使用） =====

    [Fact]
    public void Standard_ReproducesFatigueAndWeeklyTriggers_WithoutRng()
    {
        var brain = new StandardTacticsBrain();
        var rng = new Xoshiro256Random(7);
        var before = rng.CaptureState();

        Assert.Null(brain.CallPitchingChange(Sit(), rng));
        Assert.Equal(PitchingChangeReason.Fatigue,
            brain.CallPitchingChange(Sit(fatigueTriggered: true), rng)!.Value.Reason);
        Assert.Equal(PitchingChangeReason.WeeklyLimit,
            brain.CallPitchingChange(Sit(atWeekly: true), rng)!.Value.Reason);

        // Standard は継投判断で一切 RNG を引かない（＝従来の engine 直判定と1ビットも変わらない）。
        Assert.Equal(before, rng.CaptureState());
    }

    // ===== AI: 既定係数では高度トリガーは無効（帯・決定論baseline 不変の担保） =====

    [Fact]
    public void Ai_DefaultCoefficients_NeverFireAdvancedTriggers_AndDrawNoRng()
    {
        // 最上位ティア・最高采配能力・守り勝つ校風でも、既定係数（MinTier=99）なら高度トリガーは発火しない。
        var ai = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.DefensiveMinded));
        var rng = new Xoshiro256Random(3);
        var before = rng.CaptureState();

        // 崩れ・動揺・僅差終盤の条件が全て揃った状況でも、既定では続投（＝null）かつ RNG は不消費。
        Assert.Null(ai.CallPitchingChange(
            Sit(runsThisInning: 12, rattled: true, inning: 9, scoreDiff: 1, fatiguePitches: 200), rng));
        Assert.Equal(before, rng.CaptureState());

        // 基本トリガー（疲労）は既定でも尊重（Standard と同じ・RNG不使用）。
        Assert.NotNull(ai.CallPitchingChange(Sit(fatigueTriggered: true), rng));
    }

    // ===== AI: 崩れ（連打・大量失点の火消し） =====

    [Fact]
    public void Ai_Blowup_FiresWhenEnabledAndTierAllows()
    {
        var enabled = new EnemyAiCoefficients { BlowupReliefMinTier = 0, BlowupRunsInInning = 4 };
        var s = Sit(runsThisInning: 5);

        // ティア0・采配能力100（optimalProb≈0.95）→ ほぼ毎回発火。
        var ok = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: enabled);
        Assert.True(CountFires(ok, s) > 240, "有効時は崩れで高頻度に継投するはず");
        Assert.Equal(PitchingChangeReason.Blowup,
            ok.CallPitchingChange(s, new Xoshiro256Random(1))!.Value.Reason);

        // ②引き出しに無い（MinTier=3, tier0）→ 一切発火しない。
        var gated = new EnemyAiCoefficients { BlowupReliefMinTier = 3, BlowupRunsInInning = 4 };
        Assert.Equal(0, CountFires(new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: gated), s));

        // 既定（無効）→ 発火しない。
        Assert.Equal(0, CountFires(new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard)), s));

        // 失点が閾値未満 → 発火しない。
        Assert.Equal(0, CountFires(ok, Sit(runsThisInning: 3)));

        // 交代先がいなければ発火しない。
        Assert.Equal(0, CountFires(ok, Sit(runsThisInning: 5, reliefAvailable: false)));
    }

    // ===== AI: 動揺・僅差終盤 =====

    [Fact]
    public void Ai_RattledAndCloseLate_FireWhenEnabled()
    {
        var coeff = new EnemyAiCoefficients
        {
            RattledReliefMinTier = 0,
            CloseLateReliefMinTier = 0,
            CloseLateFromInning = 7,
            CloseLateMaxScoreDiff = 2,
            CloseLateReliefMarginFactor = 0.6,
        };
        var ai = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: coeff);

        // 動揺投手 → 継投。
        Assert.True(CountFires(ai, Sit(rattled: true)) > 240);

        // 僅差終盤（8回・1点差・実効消費 105 ≥ 目安90 + margin25×0.6=15）→ 早め継投。
        Assert.True(CountFires(ai, Sit(inning: 8, scoreDiff: 1, fatiguePitches: 105)) > 240);
        Assert.Equal(PitchingChangeReason.CloseLate,
            ai.CallPitchingChange(Sit(inning: 8, scoreDiff: 1, fatiguePitches: 105), new Xoshiro256Random(1))!.Value.Reason);

        // 僅差でない（5点差）かつ動揺なし → 発火しない。
        Assert.Equal(0, CountFires(ai, Sit(inning: 8, scoreDiff: 5, fatiguePitches: 105)));

        // 終盤でない（6回）→ 早め継投は発火しない。
        Assert.Equal(0, CountFires(ai, Sit(inning: 6, scoreDiff: 1, fatiguePitches: 105)));
    }

    // ===== ③ 校風による実効ティア補正（豪腕依存＝引っ張る／守り勝つ＝早め） =====

    [Fact]
    public void Ai_SchoolStyle_ShiftsAdvancedReliefTierGate()
    {
        var coeff = new EnemyAiCoefficients
        {
            CloseLateReliefMinTier = 5,
            CloseLateFromInning = 7,
            CloseLateMaxScoreDiff = 2,
            CloseLateReliefMarginFactor = 0.6,
            AceDependentReliefTierPenalty = 2,
            DefensiveMindedReliefTierBonus = 1,
        };
        var s = Sit(inning: 8, scoreDiff: 0, fatiguePitches: 105);

        var standard = new AiTacticsBrain(new AiProfile(100, 5, SchoolStyle.Standard), aiCoeff: coeff);
        var aceDependent = new AiTacticsBrain(new AiProfile(100, 5, SchoolStyle.AceDependent), aiCoeff: coeff);
        var defensive = new AiTacticsBrain(new AiProfile(100, 5, SchoolStyle.DefensiveMinded), aiCoeff: coeff);

        Assert.True(CountFires(standard, s) > 240);   // 実効ティア5 ≥5 → 発火
        Assert.Equal(0, CountFires(aceDependent, s));  // 実効ティア3 <5 → 引っ張る（発火せず）
        Assert.True(CountFires(defensive, s) > 240);   // 実効ティア6 ≥5 → 早め（発火）
    }

    // ===== GameEngine 結線: 専用Fork による決定論保存 =====

    [Fact]
    public void Game_AdvancedRelief_IsDeterministic()
    {
        var coeff = new EnemyAiCoefficients
        {
            BlowupReliefMinTier = 0, RattledReliefMinTier = 0, CloseLateReliefMinTier = 0, BlowupRunsInInning = 2,
        };
        Team Make(string n) => DeterminismCards.Team(n) with
        {
            Tactics = new AiTacticsBrain(new AiProfile(80, 6, SchoolStyle.DefensiveMinded), aiCoeff: coeff),
        };

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var r1 = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            var r2 = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            Assert.Equal(GameResultDigest.Sha256Of(r1), GameResultDigest.Sha256Of(r2));
        }
    }

    // ===== GameEngine 結線: 高度継投を有効化すると継投回数が無指示ベースラインを上回る =====

    [Fact]
    public void Game_AdvancedRelief_IncreasesPitcherChangesVsBaseline()
    {
        var coeff = new EnemyAiCoefficients
        {
            BlowupReliefMinTier = 0, RattledReliefMinTier = 0, CloseLateReliefMinTier = 0,
            BlowupRunsInInning = 1, CloseLateFromInning = 6, CloseLateReliefMarginFactor = 0.4,
        };

        var baseline = 0;
        var advanced = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            // 無指示（Brain=null）＝従来の疲労球数トリガーだけ。
            var b = GameEngine.Play(
                DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), new Xoshiro256Random(seed));
            baseline += b.PitcherChanges;

            // 高度継投を有効化した AI 同士。
            Team Make(string n) => DeterminismCards.Team(n) with
            {
                Tactics = new AiTacticsBrain(new AiProfile(90, 7, SchoolStyle.DefensiveMinded), aiCoeff: coeff),
            };
            var a = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            advanced += a.PitcherChanges;
        }

        Assert.True(advanced > baseline, $"高度継投有効={advanced} が 無指示ベースライン={baseline} を上回るはず");
    }
}
