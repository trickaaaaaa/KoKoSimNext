using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 敵AI・委任采配の守備位置操作（issue #255, 設計書09 §2.3/§6.2・設計書11）。
/// オーナー決定 <b>A-2</b>＝守備位置の入替は僅差終盤限定、<b>B-1</b>＝コンバート継投は元投手の打力温存トリガー。
/// #209 と同じ2段運用（①既定オフで配線 → ②有効化して帯を実測 → 採否）で入れ、実測後に
/// 入替=D(3)・コンバート継投=B(5) で採用した（2026-07-26）。
/// この層を通るのは <see cref="AiTacticsBrain"/> だけで <see cref="StandardTacticsBrain"/> は恒等のまま＝
/// StandardTacticsBrain 駆動の games_10k/tactics 帯・決定論baseline は動かないことを併せて保証する。
/// </summary>
public sealed class DefensePositionTacticsTests
{
    // ===== 共通のダミー =====

    private static Player At(FieldPosition pos, string name, int fielding = 50, int contact = 50, int power = 50)
        => new() { Position = pos, Name = name, Fielding = fielding, Contact = contact, Power = power };

    /// <summary>
    /// 打順スロット順の守備配置。既定は「遊撃に下手（30）・一塁に上手（80）」＝入替すれば必ず改善する並び。
    /// スロット8は投手（入替対象外）。
    /// </summary>
    private static (List<Player> Lineup, List<bool> Swappable) Defense(int shortstopFielding = 30, int firstBaseFielding = 80)
    {
        var lineup = new List<Player>
        {
            At(FieldPosition.Catcher, "捕", 50),
            At(FieldPosition.FirstBase, "一", firstBaseFielding),
            At(FieldPosition.SecondBase, "二", 50),
            At(FieldPosition.ThirdBase, "三", 50),
            At(FieldPosition.Shortstop, "遊", shortstopFielding),
            At(FieldPosition.LeftField, "左", 50),
            At(FieldPosition.CenterField, "中", 50),
            At(FieldPosition.RightField, "右", 50),
            At(FieldPosition.Pitcher, "投", 50),
        };
        var swappable = Enumerable.Range(0, 9).Select(i => i != 8).ToList();
        return (lineup, swappable);
    }

    private static DefenseSwapSituation SwapSit(
        int inning = 8, int scoreDiff = 1, int shortstopFielding = 30, int firstBaseFielding = 80)
    {
        var (lineup, swappable) = Defense(shortstopFielding, firstBaseFielding);
        return new DefenseSwapSituation(inning, 9, scoreDiff, lineup, swappable);
    }

    /// <summary>異なるシードの独立rngで n 回問い合わせ、入替が返った回数を数える（①能力値ミスの分布を見る）。</summary>
    private static int CountSwaps(ITacticsBrain brain, DefenseSwapSituation s, int n = 300)
    {
        var count = 0;
        for (ulong seed = 1; seed <= (ulong)n; seed++)
        {
            if (brain.CallDefenseSwap(s, new Xoshiro256Random(seed)) is not null) count++;
        }
        return count;
    }

    private static EnemyAiCoefficients SwapEnabled(int minTier = 0) => new()
    {
        DefenseSwapMinTier = minTier,
        DefenseSwapFromInning = 7,
        DefenseSwapMaxScoreDiff = 2,
        DefenseSwapMinGain = 4.0,
    };

    // ===== StandardTacticsBrain は恒等（統計シムの帯・決定論baseline 不変の担保） =====

    [Fact]
    public void Standard_NeverSwaps_AndDrawsNoRng()
    {
        var brain = new StandardTacticsBrain();
        var rng = new Xoshiro256Random(11);
        var before = rng.CaptureState();

        Assert.Null(brain.CallDefenseSwap(SwapSit(), rng));
        Assert.Equal(before, rng.CaptureState());
    }

    [Fact]
    public void Standard_NeverConverts_EvenForStrongHittingPitcher()
    {
        // 統計シム（games_10k / games_10k_tactics）が使うのはこの脳。⑦層を有効化しても
        // コンバート継投を選ばない＝帯は動かない。
        var brain = new StandardTacticsBrain();
        var d = brain.CallPitchingChange(ReliefSit(Pitcher(99, 99)), new Xoshiro256Random(7));

        Assert.Null(d?.ConvertTo);
    }

    // ===== ⑦層の有効化ティア（既定係数）=====

    [Fact]
    public void Ai_Swap_DefaultCoefficients_GateAtTierD()
    {
        // 既定 DefenseSwapMinTier=3（D）。関門を通らないティアでは RNG も引かない。
        var below = new AiTacticsBrain(new AiProfile(100, 2, SchoolStyle.Standard));
        var rng = new Xoshiro256Random(3);
        var before = rng.CaptureState();
        Assert.Null(below.CallDefenseSwap(SwapSit(inning: 9, scoreDiff: 0), rng));
        Assert.Equal(before, rng.CaptureState());

        // ③校風で実効ティアが下がる側も同じ（豪腕依存 −1）。
        var aceDependent = new AiTacticsBrain(new AiProfile(100, 3, SchoolStyle.AceDependent));
        Assert.Equal(0, CountSwaps(aceDependent, SwapSit()));

        // D 以上は既定のまま発火する（＝有効化されている）。
        Assert.True(CountSwaps(new AiTacticsBrain(new AiProfile(100, 3, SchoolStyle.Standard)), SwapSit()) > 240);
    }

    // ===== A-2: 選ぶケース／選ばないケース =====

    [Fact]
    public void Ai_Swap_PutsBetterGloveAtHeavierPosition_WhenCloseAndLate()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: SwapEnabled());
        var s = SwapSit(inning: 8, scoreDiff: 1);

        Assert.True(CountSwaps(ai, s) > 240, "有効時は僅差終盤に高頻度で入替を選ぶはず");

        // 選ぶのは「一塁(80)⇔遊撃(30)」＝上手い方を負担の重い位置へ。スロットは (1, 4)。
        var pick = ai.CallDefenseSwap(s, new Xoshiro256Random(1));
        Assert.Equal((1, 4), pick);
    }

    [Fact]
    public void Ai_Swap_DoesNotFire_OutsideCloseAndLate()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: SwapEnabled());

        Assert.Equal(0, CountSwaps(ai, SwapSit(inning: 6, scoreDiff: 1)));   // 終盤でない
        Assert.Equal(0, CountSwaps(ai, SwapSit(inning: 8, scoreDiff: 5)));   // 僅差でない（リード）
        Assert.Equal(0, CountSwaps(ai, SwapSit(inning: 8, scoreDiff: -5)));  // 僅差でない（ビハインド）
    }

    [Fact]
    public void Ai_Swap_DoesNotFire_WhenGainIsTooSmall()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: SwapEnabled());

        // 既に最適（遊撃が上手い）＝改善量が負 → 動かさない。
        Assert.Equal(0, CountSwaps(ai, SwapSit(shortstopFielding: 80, firstBaseFielding: 30)));

        // 改善量が閾値（4.0）以下 → 動かさない。遊撃(1.00)⇔一塁(0.30) の差0.70 × 守備差5 = 3.5 < 4.0。
        Assert.Equal(0, CountSwaps(ai, SwapSit(shortstopFielding: 50, firstBaseFielding: 55)));
    }

    [Fact]
    public void Ai_Swap_NeverMovesPitcherOrCatcher()
    {
        // 投手スロットは Swappable=false、捕手は重み表を持たない＝どちらも候補に入らない。
        var ai = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: SwapEnabled());
        var (lineup, swappable) = Defense();
        // 捕手を極端に上手く（90）しても、捕手を動かす手は選ばれない。
        lineup[0] = lineup[0] with { Fielding = 90 };
        var s = new DefenseSwapSituation(8, 9, 1, lineup, swappable);

        for (ulong seed = 1; seed <= 100; seed++)
        {
            if (ai.CallDefenseSwap(s, new Xoshiro256Random(seed)) is not { } pick) continue;
            Assert.NotEqual(0, pick.SlotA);
            Assert.NotEqual(0, pick.SlotB);
            Assert.NotEqual(8, pick.SlotA);
            Assert.NotEqual(8, pick.SlotB);
        }
    }

    // ===== ② ティア関門 × ③ 校風 =====

    [Fact]
    public void Ai_Swap_TierGateAndSchoolStyle()
    {
        var coeff = SwapEnabled(minTier: 5) with
        {
            DefensiveMindedSwapTierBonus = 1,
            AceDependentSwapTierPenalty = 1,
        };
        var s = SwapSit();

        var standard = new AiTacticsBrain(new AiProfile(100, 5, SchoolStyle.Standard), aiCoeff: coeff);
        var aceDependent = new AiTacticsBrain(new AiProfile(100, 5, SchoolStyle.AceDependent), aiCoeff: coeff);
        var defensive = new AiTacticsBrain(new AiProfile(100, 4, SchoolStyle.DefensiveMinded), aiCoeff: coeff);
        var lowTier = new AiTacticsBrain(new AiProfile(100, 4, SchoolStyle.Standard), aiCoeff: coeff);

        Assert.True(CountSwaps(standard, s) > 240);    // 実効5 ≥5 → 動かす
        Assert.Equal(0, CountSwaps(aceDependent, s));   // 実効4 <5 → 投手任せで動かさない
        Assert.True(CountSwaps(defensive, s) > 240);    // 実効5 ≥5 → 守り勝つは組み替えに積極的
        Assert.Equal(0, CountSwaps(lowTier, s));        // 実効4 <5 → 引き出しにない
    }

    [Fact]
    public void Ai_Swap_TacticalSense_ShiftsFireRate()
    {
        var coeff = SwapEnabled();
        var s = SwapSit();
        var sharp = CountSwaps(new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: coeff), s);
        var dull = CountSwaps(new AiTacticsBrain(new AiProfile(0, 0, SchoolStyle.Standard), aiCoeff: coeff), s);

        // ① 采配能力が低いと改善に気づけず好機を逃す（発火率が下がる）。
        Assert.True(dull < sharp, $"采配能力が低い方が発火率が低いはず (dull={dull}, sharp={sharp})");
    }

    // ===== B-1: コンバート継投 =====

    private static PitchingChangeSituation ReliefSit(
        Player pitcher, bool usesDh = false, bool fatigueTriggered = true, IReadOnlyList<Player>? candidates = null)
    {
        var (lineup, swappable) = Defense();
        return new PitchingChangeSituation(
            8, 9, 0, 0, 0, false, fatigueTriggered, false, 0, 90, 25,
            ReliefAvailable: true,
            ReliefCandidates: candidates ?? new List<Player> { Relief("控えP") },
            CurrentPitcher: pitcher, DefenseLineup: lineup, SwappableSlots: swappable, UsesDh: usesDh);
    }

    private static Player Relief(string name)
        => new() { Name = name, Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage };

    private static Player Pitcher(int contact, int power)
        => new()
        {
            Name = "先発P", Position = FieldPosition.Pitcher, Contact = contact, Power = power,
            Pitching = PitcherAttributes.LeagueAverage,
        };

    private static EnemyAiCoefficients ConvertEnabled(int minTier = 0) => new()
    {
        ConvertReliefMinTier = minTier,
        ConvertReliefMinBatting = 60.0,
    };

    [Fact]
    public void Ai_ConvertRelief_DefaultCoefficients_GateAtTierB()
    {
        // 既定 ConvertReliefMinTier=5（B）。B未満は打力があっても通常継投のまま。
        var below = new AiTacticsBrain(new AiProfile(100, 4, SchoolStyle.Standard));
        var d = below.CallPitchingChange(ReliefSit(Pitcher(90, 90)), new Xoshiro256Random(1));
        Assert.NotNull(d);                    // 基本トリガー（疲労）は関門と無関係＝継投そのものは起きる
        Assert.Null(d!.Value.ConvertTo);

        // B 以上は既定のままコンバートを選ぶ（＝有効化されている）。
        var atTier = new AiTacticsBrain(new AiProfile(100, 5, SchoolStyle.Standard));
        var converted = atTier.CallPitchingChange(ReliefSit(Pitcher(90, 90)), new Xoshiro256Random(1));
        Assert.NotNull(converted!.Value.ConvertTo);
    }

    [Fact]
    public void Ai_ConvertRelief_FiresForStrongHittingPitcher_AtLightestPosition()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: ConvertEnabled());

        // 打力の高い投手（(90+70)/2=80 ≥ 60）→ 打順に残すため守備へ回す。行き先は最も負担の軽い一塁。
        var strong = ai.CallPitchingChange(ReliefSit(Pitcher(90, 70)), new Xoshiro256Random(1));
        Assert.Equal(FieldPosition.FirstBase, strong!.Value.ConvertTo);
        Assert.NotNull(strong.Value.Reliever);

        // 打力の低い投手（(30+30)/2=30 < 60）→ 普通に交代する（コンバートしない）。
        var weak = ai.CallPitchingChange(ReliefSit(Pitcher(30, 30)), new Xoshiro256Random(1));
        Assert.NotNull(weak);
        Assert.Null(weak!.Value.ConvertTo);
    }

    [Fact]
    public void Ai_ConvertRelief_DoesNotFire_UnderDh_OrWithoutTier()
    {
        // DH制＝元投手はもともと打席に立たない＝「打順に残す」判断が成り立たない。
        var ai = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: ConvertEnabled());
        Assert.Null(ai.CallPitchingChange(ReliefSit(Pitcher(90, 90), usesDh: true), new Xoshiro256Random(1))!.Value.ConvertTo);

        // ②引き出しに無い（MinTier=5, tier0）→ 通常継投のまま。
        var gated = new AiTacticsBrain(new AiProfile(100, 0, SchoolStyle.Standard), aiCoeff: ConvertEnabled(minTier: 5));
        Assert.Null(gated.CallPitchingChange(ReliefSit(Pitcher(90, 90)), new Xoshiro256Random(1))!.Value.ConvertTo);
    }

    // ===== GameEngine 結線 =====

    [Fact]
    public void Game_DefenseSwapAndConvertRelief_AreDeterministic()
    {
        var coeff = new EnemyAiCoefficients
        {
            DefenseSwapMinTier = 0, DefenseSwapFromInning = 1, DefenseSwapMaxScoreDiff = 99, DefenseSwapMinGain = 0.5,
            ConvertReliefMinTier = 0, ConvertReliefMinBatting = 0.0,
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

    [Fact]
    public void Game_DefaultAi_IsDeterministic_WithEnabledDefaults()
    {
        // ⑦層が既定で有効（入替D・コンバート継投B）になった後も、同シードなら同結果。
        // 発火判定は rng.Fork した専用ストリームなので、本流の乱数消費順は回ごとに揺れない。
        Team WithAi(string n) => DeterminismCards.Team(n) with
        {
            Tactics = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.DefensiveMinded)),
        };

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var withAi = GameEngine.Play(WithAi("A"), WithAi("H"), new GameContext(), new Xoshiro256Random(seed));
            var withAi2 = GameEngine.Play(WithAi("A"), WithAi("H"), new GameContext(), new Xoshiro256Random(seed));
            Assert.Equal(GameResultDigest.Sha256Of(withAi), GameResultDigest.Sha256Of(withAi2));
        }
    }

    [Fact]
    public void Game_EnabledSwap_ActuallyChangesDefensivePositions()
    {
        // 有効化すると実際に守備位置が動く（＝入口が engine に結線されている）ことを TeamState 直で確認する。
        var coeff = new EnemyAiCoefficients
        {
            DefenseSwapMinTier = 0, DefenseSwapFromInning = 1, DefenseSwapMaxScoreDiff = 99, DefenseSwapMinGain = 0.5,
        };
        var ai = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard), aiCoeff: coeff);
        var s = SwapSit(inning: 1, scoreDiff: 0);
        var pick = ai.CallDefenseSwap(s, new Xoshiro256Random(1));
        Assert.NotNull(pick);

        var state = new TeamState(new Team
        {
            Name = "テスト校",
            BattingOrder = s.Lineup.ToList(),
            PitcherSlot = 8,
        });
        var before = state.CurrentLineup.Select(p => p.Position).ToList();
        Assert.True(state.SwapDefensivePositions(pick!.Value.SlotA, pick.Value.SlotB));
        var after = state.CurrentLineup.Select(p => p.Position).ToList();

        Assert.NotEqual(before, after);
        Assert.Equal(0, state.Substitutions);   // 入替は交代回数を消費しない（設計書09 §2.3）
    }
}
