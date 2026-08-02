using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
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
        double fatiguePitches = 0, double staminaTarget = 90, double relieveMargin = 25,
        System.Collections.Generic.IReadOnlyList<Player>? candidates = null)
        => new(inning, 9, 0, scoreDiff, runsThisInning, rattled, fatigueTriggered, atWeekly,
               fatiguePitches, staminaTarget, relieveMargin, reliefAvailable,
               candidates ?? System.Array.Empty<Player>());

    // ---- Phase B 人選テスト用の候補ビルダ ----
    private static PitcherAttributes Attr(int velLevel, int control, int staminaLevel, int pitchRank)
        => new()
        {
            MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velLevel),
            Control = control,
            StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staminaLevel),
            PitchRank = pitchRank,
        };

    private static Player Reliever(string name, PitcherAttributes attr)
        => new() { Name = name, Position = FieldPosition.Pitcher, Pitching = attr };

    /// <summary>投手として置かれていない控え（投手能力は全選手が持つ＝#381）。</summary>
    private static Player FielderWithLatent(string name, PitcherAttributes latent)
        => new() { Name = name, Position = FieldPosition.FirstBase, Pitching = latent };

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

    // ===== Phase B: 横断ランキング人選（issue #209 Phase B） =====

    /// <summary>基本トリガーで決定を得た時、Reliever に横断ランキングの最良投手が入る（RNG非依存で決定論）。</summary>
    private static Player? PickedReliever(ITacticsBrain brain, params Player[] candidates)
        => brain.CallPitchingChange(
            Sit(fatigueTriggered: true, candidates: candidates), new Xoshiro256Random(1))!.Value.Reliever;

    [Fact]
    public void Ai_PicksHighestScoringReservePitcher_NotFirst()
    {
        var weak = Reliever("R1", Attr(velLevel: 30, control: 30, staminaLevel: 30, pitchRank: 30));
        var strong = Reliever("R2", Attr(velLevel: 75, control: 75, staminaLevel: 70, pitchRank: 72));
        var ai = new AiTacticsBrain(new AiProfile(60, 5, SchoolStyle.Standard));

        // 登板順の先頭(weak)でなく、投手能力が最良の R2 を選ぶ。
        Assert.Equal("R2", PickedReliever(ai, weak, strong)!.Name);
    }

    [Fact]
    public void Ai_PicksFielder_WhenAbilityOutranksReservePitchers()
    {
        var weakPitcher = Reliever("R1", Attr(velLevel: 30, control: 30, staminaLevel: 30, pitchRank: 30));
        // 強肩の野手（投手枠の控えの最上位を上回る）。
        var acePosition = FielderWithLatent("SS", Attr(velLevel: 78, control: 60, staminaLevel: 45, pitchRank: 55));
        var ai = new AiTacticsBrain(new AiProfile(60, 5, SchoolStyle.Standard));

        // D2: 投手枠の控えの最上位を1点でも上回るなら守備の選手を登板させる。
        Assert.Equal("SS", PickedReliever(ai, weakPitcher, acePosition)!.Name);
    }

    [Fact]
    public void Ai_PrefersPlacedPitcher_OnScoreTie()
    {
        var attr = Attr(velLevel: 60, control: 55, staminaLevel: 50, pitchRank: 58);
        var pitcher = Reliever("R1", attr);
        var fielder = FielderWithLatent("SS", attr); // 同一スコア
        var ai = new AiTacticsBrain(new AiProfile(60, 5, SchoolStyle.Standard));

        // 同点は本職投手を優先（D2 は「上回った時だけ」＝厳密超過）。
        Assert.Equal("R1", PickedReliever(ai, pitcher, fielder)!.Name);
    }

    [Fact]
    public void Ai_FallsBackToFielder_WhenNoReservePitcher()
    {
        var fielder = FielderWithLatent("SS", Attr(velLevel: 40, control: 30, staminaLevel: 25, pitchRank: 28));
        var ai = new AiTacticsBrain(new AiProfile(60, 5, SchoolStyle.Standard));

        // 投手枠の控えゼロ＝緊急登板で守備の選手を選ぶ。
        Assert.Equal("SS", PickedReliever(ai, fielder)!.Name);
    }

    [Fact]
    public void Standard_LeavesRelieverNull_EvenWithStrongFielderCandidate()
    {
        // Standard／無指示は人選しない＝Reliever=null（engine 既定＝名簿順で最初の投手枠の控え）。帯不変の担保。
        var strongFielder = FielderWithLatent("SS", Attr(velLevel: 90, control: 80, staminaLevel: 70, pitchRank: 80));
        var std = new StandardTacticsBrain();
        var decision = std.CallPitchingChange(
            Sit(fatigueTriggered: true, candidates: new[] { strongFielder }), new Xoshiro256Random(1));

        Assert.NotNull(decision);
        Assert.Null(decision!.Value.Reliever);
    }

    // ===== 全選手が投手能力を持つ（issue #381 / #366。旧「潜在投手能力の材料化」） =====

    [Fact]
    public void Roster_ProjectsPitching_ForEveryone_FromTrueLevels()
    {
        var dp = new DevelopingPlayer();
        dp.SetLevel(AbilityKind.Velocity, 80);
        dp.SetLevel(AbilityKind.Control, 75);
        dp.SetLevel(AbilityKind.Stamina, 60);
        dp.SetLevel(AbilityKind.PitchRank, 70);

        // 投手として置くかに関わらず、隠しの真の投手レベルがそのまま投手能力になる（#381）。
        // 旧実装は野手だけ LatentPitching という別の口へ入れていたが、材料も値も同一だった。
        var fielder = RosterTeamBuilder.ToPlayer(dp, FieldPosition.Shortstop);
        Assert.Equal(PitcherAttributes.VelocityKmhFromLevel(80), fielder.Pitching.MaxVelocityKmh, 3);
        Assert.Equal(75, fielder.Pitching.Control);

        // 配置が変わっても能力は変わらない＝「投手か否か」で能力を作り分けない。
        var pitcher = RosterTeamBuilder.ToPlayer(dp, FieldPosition.Pitcher);
        Assert.Equal(fielder.Pitching.MaxVelocityKmh, pitcher.Pitching.MaxVelocityKmh, 3);
        Assert.Equal(fielder.Pitching.Control, pitcher.Pitching.Control);
    }

    [Fact]
    public void StrengthFactory_PositionPlayer_HasPitching_AndIsDeterministic()
    {
        var pc = new PersonalityCoefficients();
        var a = StrengthTeamFactory.PositionPlayer(FieldPosition.CenterField, 60, new Xoshiro256Random(123), pc, 0xABCUL, "手");
        var b = StrengthTeamFactory.PositionPlayer(FieldPosition.CenterField, 60, new Xoshiro256Random(123), pc, 0xABCUL, "手");

        // AI校のマウンド外の選手も投手能力を持つ（肩からの推定＝低いだけで欠けてはいない）。
        Assert.True(a.Pitching.Control < PitcherAttributes.LeagueAverage.Control);   // 投げ慣れていない前提
        // 同シード同salt＝同じ能力（決定論）。
        Assert.Equal(a.Pitching.MaxVelocityKmh, b.Pitching.MaxVelocityKmh);
        Assert.Equal(a.Pitching.Control, b.Pitching.Control);
    }

    [Fact]
    public void StrengthFactory_UntrainedPitchingFork_DoesNotConsumeMainRng()
    {
        var pc = new PersonalityCoefficients();
        var rng1 = new Xoshiro256Random(99);
        _ = StrengthTeamFactory.PositionPlayer(FieldPosition.CenterField, 60, rng1, pc, 0x3UL, "手");
        var afterFactory = rng1.CaptureState();

        // PositionPlayer が主ストリームで引くのは能力8ドロー（contact/power/launch(50)/discipline/speed/arm/fielding/catching）
        // だけ。性格・マウンド外の投手能力はどちらも Fork＝主RNG非消費なので、能力8ドローだけ再現すれば状態は一致する。
        var rng2 = new Xoshiro256Random(99);
        foreach (var center in new[] { 60.0, 60, 50, 60, 60, 60, 60, 60 }) rng2.NextGaussian(center, 6);
        var afterAbilitiesOnly = rng2.CaptureState();

        Assert.Equal(afterAbilitiesOnly, afterFactory);
    }

    // ===== 誰が登板しても本人の投手能力で投げる（issue #209 Phase B → #381） =====

    [Fact]
    public void ChangePitcherTo_Fielder_PitchesWithOwnAbility()
    {
        Player F(string n, FieldPosition pos) => new() { Name = n, Position = pos };
        var order = new System.Collections.Generic.List<Player>
        {
            F("捕", FieldPosition.Catcher), F("一", FieldPosition.FirstBase), F("二", FieldPosition.SecondBase),
            F("三", FieldPosition.ThirdBase), F("遊", FieldPosition.Shortstop), F("左", FieldPosition.LeftField),
            F("中", FieldPosition.CenterField), F("右", FieldPosition.RightField),
            new() { Name = "先発", Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage },
        };
        var strongLatent = Attr(velLevel: 90, control: 60, staminaLevel: 45, pitchRank: 55);
        var superFielder = FielderWithLatent("二刀流", strongLatent);
        var team = new Team
        {
            Name = "T", BattingOrder = order, PitcherSlot = 8,
            Reserves = new[] { superFielder },
        };
        var st = new TeamState(team);

        Assert.True(st.ChangePitcherTo(superFielder));
        // 投手として置かれていない控えが登板しても、リーグ平均でなく本人の能力で投げる。
        Assert.Equal(strongLatent.MaxVelocityKmh, st.CurrentPitcher.Pitching.MaxVelocityKmh, 3);
        Assert.NotEqual(PitcherAttributes.LeagueAverage.MaxVelocityKmh, st.CurrentPitcher.Pitching.MaxVelocityKmh);
        // 能力の詰め替え（旧 Materialize）が無くなったので、控えのインスタンスがそのままマウンドに立つ。
        Assert.Same(superFielder, st.CurrentPitcher);
    }

    /// <summary>
    /// 同点の継投優先は<b>配置</b>で決まる（#381 / オーナー判断 2026-08-02「現状維持＝同点は本職投手」）。
    /// 投手能力は全選手が持つので「能力を持っているか」では表現できない＝守備位置が投手枠かで判定する。
    /// </summary>
    [Fact]
    public void PickReliever_Tie_PrefersThePlayerPlacedAsPitcher()
    {
        var same = Attr(velLevel: 60, control: 50, staminaLevel: 40, pitchRank: 50);
        var placed = Reliever("投手枠の控え", same);
        var unplaced = FielderWithLatent("守備の控え", same);   // 同一能力＝ReliefScore が同点
        var ai = new AiTacticsBrain(new AiProfile(60, 5, SchoolStyle.Standard));

        // 名簿では配置外が先。それでも投手枠の控えが選ばれる＝並び順でなく配置で決まっている。
        Assert.Equal("投手枠の控え", PickedReliever(ai, unplaced, placed)!.Name);

        // 1点でも上回れば配置外が選ばれる（優先はあくまで同点時のタイブレークであって、能力順の上書きではない）。
        var better = FielderWithLatent("守備の控え強", Attr(velLevel: 61, control: 50, staminaLevel: 40, pitchRank: 50));
        Assert.Equal("守備の控え強", PickedReliever(ai, better, placed)!.Name);
    }
}
