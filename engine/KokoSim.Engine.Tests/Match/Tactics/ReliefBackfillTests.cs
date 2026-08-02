using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 在フィールドのレギュラー野手を継投候補に含め、投野交代で守備穴を backfill する（issue #209 Phase C）。
/// 二刀流の主力を緊急登板させ、退く投手をその守備位置へ回す（両者残留・ベンチ非消費・リエントリー禁止不抵触）。
/// Standard／無指示は不変（帯・決定論baseline は EngineDeterminismGateTests が別途担保）。
/// </summary>
public sealed class ReliefBackfillTests
{
    private static PitcherAttributes Attr(int velLevel, int control, int staminaLevel, int pitchRank)
        => new()
        {
            MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velLevel),
            Control = control,
            StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(staminaLevel),
            PitchRank = pitchRank,
        };

    private static Player Field(string name, FieldPosition pos, PitcherAttributes? latent = null)
        => latent is null
            ? new Player { Name = name, Position = pos }
            : new Player { Name = name, Position = pos, Pitching = latent };

    private static Player Arm(string name, PitcherAttributes a)
        => new() { Name = name, Position = FieldPosition.Pitcher, Pitching = a };

    /// <summary>非DHチーム。遊撃手(遊)に潜在投手能力を持たせ、ブルペンは1枚。</summary>
    private static Team NonDhTeam(string name, PitcherAttributes? ssLatent, PitcherAttributes bullpenAttr)
    {
        var order = new List<Player>
        {
            Field("捕", FieldPosition.Catcher), Field("一", FieldPosition.FirstBase),
            Field("二", FieldPosition.SecondBase), Field("三", FieldPosition.ThirdBase),
            Field("遊", FieldPosition.Shortstop, ssLatent), Field("左", FieldPosition.LeftField),
            Field("中", FieldPosition.CenterField), Field("右", FieldPosition.RightField),
            Arm(name + "先発", PitcherAttributes.LeagueAverage),
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = KokoSim.Engine.Match.Game.TeamReserves.Merge(new[] { Arm(name + "中継ぎ", bullpenAttr) }, new[] { Field(name + "控", FieldPosition.FirstBase) }),
        };
    }

    // ===== 候補プール: 在フィールドのレギュラー野手を含める（非DHのみ） =====

    [Fact]
    public void ReliefCandidates_IncludeOnFieldRegulars_NonDh()
    {
        var st = new TeamState(NonDhTeam("T", Attr(80, 60, 45, 55), Attr(30, 30, 30, 30)));
        var cands = st.ReliefRankingCandidates;

        // ブルペン1 + ベンチ1 + 在フィールド野手8（現投手を除く打順9-1）= 10。
        Assert.Equal(10, cands.Count);
        Assert.Contains(cands, p => p.Name == "遊");
        // 現投手（先発）は候補に含めない。
        Assert.DoesNotContain(cands, p => p.Name == "T先発");
    }

    /// <summary>DHチーム。遊撃(遊)に潜在投手能力・DHに打力/守備・ベンチ守備職人・ブルペン1枚を指定できる。</summary>
    private static Team DhTeam(PitcherAttributes ssLatent, PitcherAttributes bullpenAttr,
        int dhContact = 50, int dhPower = 50, int dhFielding = 50, int benchFielding = 50)
    {
        var order = new List<Player>
        {
            Field("捕", FieldPosition.Catcher), Field("一", FieldPosition.FirstBase),
            Field("二", FieldPosition.SecondBase), Field("三", FieldPosition.ThirdBase),
            Field("遊", FieldPosition.Shortstop, ssLatent), Field("左", FieldPosition.LeftField),
            Field("中", FieldPosition.CenterField), Field("右", FieldPosition.RightField),
            new()
            {
                Name = "DH", Position = FieldPosition.DesignatedHitter,
                Contact = dhContact, Power = dhPower, Fielding = dhFielding,
            },
        };
        return new Team
        {
            Name = "D", BattingOrder = order, DhSlot = 8,
            StartingPitcher = Arm("D先発", PitcherAttributes.LeagueAverage),
            Reserves = KokoSim.Engine.Match.Game.TeamReserves.Merge(new[] { Arm("D中継ぎ", bullpenAttr) }, new[] { new Player { Name = "守備職人", Position = FieldPosition.Shortstop, Fielding = benchFielding } }),
        };
    }

    [Fact]
    public void ReliefCandidates_IncludeOnFieldRegulars_InDh()
    {
        var st = new TeamState(DhTeam(Attr(80, 60, 45, 55), Attr(30, 30, 30, 30)));
        var cands = st.ReliefRankingCandidates;

        // DHでも在フィールド野手8（DH除く打順9-1）を横断候補に含める＝ブルペン1 + ベンチ1 + 8 = 10。
        Assert.Equal(10, cands.Count);
        Assert.Contains(cands, p => p.Name == "遊");
        // DHは守備に就かない＝候補に含めない。投手は打順外なので _lineup に居ない。
        Assert.DoesNotContain(cands, p => p.Name == "DH");
    }

    // ===== DH制の投野交代（issue #209。野手登板でDH解除・空き守備は②判定で補充・旧投手は必ず退場） =====

    [Fact]
    public void Dh_PromoteOnFieldFielder_ReleasesDh_ExDhTakesField_WhenStrongHitter()
    {
        // 元DHが好打者(80/80)・守備45、ベンチ守備職人55 → margin=10+0.3*30=19 > 守備差10 → 元DHが空き守備へ（打力温存）。
        var strong = Attr(velLevel: 90, control: 60, staminaLevel: 45, pitchRank: 55);
        var st = new TeamState(DhTeam(strong, Attr(30, 30, 30, 30),
            dhContact: 80, dhPower: 80, dhFielding: 45, benchFielding: 55));
        var ss = st.CurrentLineup.First(p => p.Name == "遊");
        var oldPitcher = st.CurrentPitcher;
        var dh = st.CurrentLineup[st.DhSlot];

        Assert.True(st.RelieveWith(ss));

        Assert.False(st.UsesDh);                                  // 野手登板でDHは消滅
        Assert.Equal("遊", st.CurrentPitcher.Name);               // 遊撃手がマウンドへ（潜在能力で投げる）
        Assert.Equal(strong.MaxVelocityKmh, st.CurrentPitcher.Pitching!.MaxVelocityKmh, 3);
        Assert.True(st.IsRetired(oldPitcher));                    // DHでは旧投手は打順外＝退場
        Assert.False(st.IsRetired(dh));                           // 好打の元DHは残す（②に倒さない）
        Assert.Equal("DH", st.PlayerAtPosition(FieldPosition.Shortstop)!.Name); // 元DHが空いた遊撃を守る
        Assert.Equal(2, st.Reserves.Count);                          // 控え非消費（中継ぎ＋守備職人が残る）
        Assert.Equal(9, st.DefensiveAlignment(new FieldGeometry()).Count);      // 守備穴なし
        Assert.Equal(1, st.PitcherChanges);
        Assert.Equal(PitchingChangeKind.FielderReliefWithBackfill, st.LastPitchingChange!.Value.Kind);
        Assert.Equal("DH", st.LastPitchingChange!.Value.BackfillName);
        Assert.Equal(FieldPosition.Shortstop, st.LastPitchingChange!.Value.BackfillPosition);
    }

    [Fact]
    public void Dh_PromoteOnFieldFielder_ReleasesDh_BenchBackfills_WhenWeakHitter()
    {
        // 元DHが弱打(20/20)・守備45、ベンチ守備職人55 → margin=10+0.3*(-30)=1 ≤ 守備差10 → ベンチが空き守備へ・元DH退場。
        var strong = Attr(velLevel: 90, control: 60, staminaLevel: 45, pitchRank: 55);
        var st = new TeamState(DhTeam(strong, Attr(30, 30, 30, 30),
            dhContact: 20, dhPower: 20, dhFielding: 45, benchFielding: 55));
        var ss = st.CurrentLineup.First(p => p.Name == "遊");
        var oldPitcher = st.CurrentPitcher;
        var dh = st.CurrentLineup[st.DhSlot];

        Assert.True(st.RelieveWith(ss));

        Assert.False(st.UsesDh);
        Assert.Equal("遊", st.CurrentPitcher.Name);
        Assert.True(st.IsRetired(oldPitcher));                    // 旧投手は退場
        Assert.True(st.IsRetired(dh));                            // 弱打の元DHも退場（②）
        Assert.Equal("守備職人", st.PlayerAtPosition(FieldPosition.Shortstop)!.Name); // ベンチ野手が空き守備へ
        Assert.Single(st.Reserves);                                  // 控えを1人消費（中継ぎだけ残る）
        Assert.Equal(9, st.DefensiveAlignment(new FieldGeometry()).Count);
        Assert.Equal(1, st.PitcherChanges);
        Assert.Equal(1, st.Substitutions);
        Assert.Equal("守備職人", st.LastPitchingChange!.Value.BackfillName);
    }

    [Fact]
    public void Dh_RelieveWithBullpen_UsesNormalChange_KeepsDh()
    {
        var st = new TeamState(DhTeam(Attr(80, 60, 45, 55), Attr(70, 70, 70, 70)));
        var oldPitcher = st.CurrentPitcher;
        var reliever = st.AvailableReserves[0];

        Assert.True(st.RelieveWith(reliever));

        Assert.True(st.UsesDh);                                   // ブルペン継投ではDHは維持（野手を上げていない）
        Assert.Equal(reliever.Name, st.CurrentPitcher.Name);
        Assert.True(st.IsRetired(oldPitcher));
        Assert.Equal(PitchingChangeKind.ReserveRelief, st.LastPitchingChange!.Value.Kind);
        Assert.Equal(1, st.PitcherChanges);
    }

    // ===== 投野交代（backfill path ①） =====

    [Fact]
    public void RelieveWith_OnFieldFielder_SwapsWithPitcher_NoDefensiveHole()
    {
        var strong = Attr(velLevel: 90, control: 60, staminaLevel: 45, pitchRank: 55);
        var st = new TeamState(NonDhTeam("T", strong, Attr(30, 30, 30, 30)));
        var ss = st.CurrentLineup.First(p => p.Name == "遊");
        var oldPitcher = st.CurrentPitcher;

        Assert.True(st.RelieveWith(ss));

        // 遊撃手がマウンドへ＝本人の潜在能力（リーグ平均でない）で投げる。
        Assert.Equal("遊", st.CurrentPitcher.Name);
        Assert.NotNull(st.CurrentPitcher.Pitching);
        Assert.Equal(strong.MaxVelocityKmh, st.CurrentPitcher.Pitching!.MaxVelocityKmh, 3);
        Assert.Equal(FieldPosition.Pitcher, st.CurrentPitcher.Position);

        // 旧投手は退場せず、空いた遊撃へ回る。
        var atShort = st.PlayerAtPosition(FieldPosition.Shortstop);
        Assert.NotNull(atShort);
        Assert.Equal(oldPitcher.Name, atShort!.Name);
        Assert.False(st.IsRetired(oldPitcher));

        // 守備に穴が開かない（9ポジション全て埋まる・各ポジションに実選手）。
        Assert.Equal(9, st.DefensiveAlignment(new FieldGeometry()).Count);
        foreach (var pos in new[]
        {
            FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
            FieldPosition.CenterField, FieldPosition.RightField,
        })
        {
            Assert.NotNull(st.PlayerAtPosition(pos));
        }

        // 控え非消費・選手交代カウンタは増えない・投手交代として1回計上。
        Assert.Equal(2, st.Reserves.Count);
        Assert.Equal(0, st.Substitutions);
        Assert.Equal(1, st.PitcherChanges);
    }

    [Fact]
    public void RelieveWith_BullpenPitcher_UsesNormalChange_RetiresOldPitcher()
    {
        var st = new TeamState(NonDhTeam("T", Attr(80, 60, 45, 55), Attr(70, 70, 70, 70)));
        var oldPitcher = st.CurrentPitcher;
        var reliever = st.AvailableReserves[0];

        Assert.True(st.RelieveWith(reliever));

        // ブルペン投手は従来交代＝旧投手は退場（リエントリー禁止）、投野交代ではない。
        Assert.Equal(reliever.Name, st.CurrentPitcher.Name);
        Assert.True(st.IsRetired(oldPitcher));
        Assert.Equal(1, st.PitcherChanges);
    }

    // ===== backfill path ②（旧投手退場＋ベンチ野手で補充。issue #209 Phase C path②） =====

    /// <summary>非DHチーム: 遊撃に潜在投手能力、ブルペン無し、旧投手の打力/守備とベンチ野手の守備を指定。</summary>
    private static (TeamState St, Player Ss, Player OldPitcher) SetupOnFieldPromotion(
        int pitcherContact, int pitcherPower, int pitcherFielding, int benchFielding)
    {
        var order = new List<Player>
        {
            Field("捕", FieldPosition.Catcher), Field("一", FieldPosition.FirstBase),
            Field("二", FieldPosition.SecondBase), Field("三", FieldPosition.ThirdBase),
            Field("遊", FieldPosition.Shortstop, Attr(90, 60, 45, 55)), Field("左", FieldPosition.LeftField),
            Field("中", FieldPosition.CenterField), Field("右", FieldPosition.RightField),
            new()
            {
                Name = "先発", Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage,
                Contact = pitcherContact, Power = pitcherPower, Fielding = pitcherFielding,
            },
        };
        var team = new Team
        {
            Name = "T", BattingOrder = order, PitcherSlot = 8,
            Reserves = KokoSim.Engine.Match.Game.TeamReserves.Merge(System.Array.Empty<Player>(), new[] { new Player { Name = "守備職人", Position = FieldPosition.Shortstop, Fielding = benchFielding } }),
        };
        var st = new TeamState(team);
        return (st, st.CurrentLineup.First(p => p.Name == "遊"), st.CurrentPitcher);
    }

    [Fact]
    public void Path2_RetiresWeakHittingPitcher_WhenBenchDefendsBetter()
    {
        // 旧投手: 打力弱(20/20)・守備平凡(45)。ベンチ守備職人(55)。既定係数: margin=10+0.3*((20+20)/2-50)=1、守備差=10≥1 → ②。
        var (st, ss, oldPitcher) = SetupOnFieldPromotion(20, 20, 45, 55);

        Assert.True(st.RelieveWith(ss));
        Assert.Equal("遊", st.CurrentPitcher.Name);                 // 野手がマウンドへ
        Assert.True(st.IsRetired(oldPitcher));                      // 打力の弱い旧投手は退場（②）
        Assert.Empty(st.Reserves);                                     // ベンチ野手を消費
        Assert.Equal("守備職人", st.PlayerAtPosition(FieldPosition.Shortstop)!.Name); // ベンチ野手が空き守備を埋める
        Assert.Equal(9, st.DefensiveAlignment(new FieldGeometry()).Count);           // 守備穴なし
        Assert.Equal(1, st.PitcherChanges);
        Assert.Equal(1, st.Substitutions);                         // ベンチ投入は選手交代として計上
    }

    [Fact]
    public void Path1_KeepsStrongHittingPitcher_AtSameDefensiveGain()
    {
        // 旧投手: 打力強(80/80)・守備平凡(45)。ベンチ守備職人(55)。margin=10+0.3*((80+80)/2-50)=19、守備差=10<19 → ①（打力温存）。
        var (st, ss, oldPitcher) = SetupOnFieldPromotion(80, 80, 45, 55);

        Assert.True(st.RelieveWith(ss));
        Assert.Equal("遊", st.CurrentPitcher.Name);
        Assert.False(st.IsRetired(oldPitcher));                     // 打力の強い旧投手は残す（②に倒さない）
        Assert.Single(st.Reserves);                                    // ベンチ非消費
        Assert.Equal("先発", st.PlayerAtPosition(FieldPosition.Shortstop)!.Name); // 旧投手が空き守備へ回る（①）
        Assert.Equal(0, st.Substitutions);
        Assert.Equal(1, st.PitcherChanges);
    }

    // ===== ボックススコア: 昇格した野手の投手行が欠落しない（issue #209 Phase C 表現） =====

    [Fact]
    public void BoxScore_IncludesPromotedFielderPitchingLine()
    {
        var strong = Attr(velLevel: 90, control: 60, staminaLevel: 45, pitchRank: 55);
        var st = new TeamState(NonDhTeam("T", strong, Attr(30, 30, 30, 30)));
        var ss = st.CurrentLineup.First(p => p.Name == "遊");
        st.RelieveWith(ss); // 投野交代で遊撃手がマウンドへ

        // 昇格した野手が実際に投げる（1打者三振）。Team.Reserves に居ないので旧実装では投手行が欠落した。
        st.RecordPitching(st.CurrentPitcher, PlateAppearanceResult.Strikeout, runs: 0, outs: 1, pitches: 4);

        var lines = st.BuildPitchingLines();
        Assert.Contains(lines, l => l.Name == "遊");
    }

    // ===== AI が在フィールドの二刀流を選び、GameEngine 結線で決定論を保つ =====

    // ===== 交代ログ（issue #209 表現。GameResult.PitchingChangeLog） =====

    [Fact]
    public void PitchingChangeLog_RecordsOnFieldPromotion_AndIsDeterministic()
    {
        var coeff = new EnemyAiCoefficients
        {
            BlowupReliefMinTier = 0, RattledReliefMinTier = 0, CloseLateReliefMinTier = 0, BlowupRunsInInning = 1,
        };
        Team Make(string n) => NonDhTeam(n, Attr(92, 80, 60, 82), Attr(25, 25, 25, 25)) with
        {
            Tactics = new AiTacticsBrain(new AiProfile(90, 6, SchoolStyle.DefensiveMinded), aiCoeff: coeff),
        };

        var sawFielderRelief = false;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var r1 = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            var r2 = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            // 同シード＝同一ログ（観測も決定論）。
            Assert.Equal(r1.PitchingChangeLog.Count, r2.PitchingChangeLog.Count);
            for (var i = 0; i < r1.PitchingChangeLog.Count; i++)
                Assert.Equal(r1.PitchingChangeLog[i], r2.PitchingChangeLog[i]);

            foreach (var e in r1.PitchingChangeLog)
            {
                Assert.InRange(e.Inning, 1, 15);
                Assert.False(string.IsNullOrEmpty(e.Change.InPitcherName));
                if (e.Change.Kind is PitchingChangeKind.TwoWaySwap or PitchingChangeKind.FielderReliefWithBackfill)
                {
                    sawFielderRelief = true;
                    Assert.NotNull(e.Change.BackfillPosition);           // backfill 先が記録される
                    Assert.False(string.IsNullOrEmpty(e.Change.BackfillName));
                }
            }
        }
        Assert.True(sawFielderRelief, "強力な遊撃×弱ブルペンで投野交代/野手登板が少なくとも1回は記録されるはず");
    }

    [Fact]
    public void PitchingChangeLog_RecordsBullpenRelief_ForNormalRelief()
    {
        // ブルペン投手を指名する通常継投は BullpenRelief として記録される（プレイヤー采配経路と同じ ChangePitcherTo）。
        var st = new TeamState(NonDhTeam("T", Attr(80, 60, 45, 55), Attr(70, 70, 70, 70)));
        Assert.True(st.ChangePitcherTo(st.AvailableReserves[0]));
        Assert.NotNull(st.LastPitchingChange);
        Assert.Equal(PitchingChangeKind.ReserveRelief, st.LastPitchingChange!.Value.Kind);
        Assert.Equal("T中継ぎ", st.LastPitchingChange!.Value.InPitcherName);
    }

    [Fact]
    public void Game_AiPromotesOnFieldAce_IsDeterministic()
    {
        var coeff = new EnemyAiCoefficients
        {
            BlowupReliefMinTier = 0, RattledReliefMinTier = 0, CloseLateReliefMinTier = 0, BlowupRunsInInning = 1,
        };
        // 遊撃に強力な潜在投手能力、ブルペンは弱い1枚＝AI は緊急時に遊撃をマウンドへ上げうる。
        Team Make(string n) => NonDhTeam(n, Attr(92, 80, 60, 82), Attr(25, 25, 25, 25)) with
        {
            Tactics = new AiTacticsBrain(new AiProfile(90, 6, SchoolStyle.DefensiveMinded), aiCoeff: coeff),
        };

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var r1 = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            var r2 = GameEngine.Play(Make("A"), Make("H"), new GameContext(), new Xoshiro256Random(seed));
            Assert.Equal(GameResultDigest.Sha256Of(r1), GameResultDigest.Sha256Of(r2));
        }
    }
}
