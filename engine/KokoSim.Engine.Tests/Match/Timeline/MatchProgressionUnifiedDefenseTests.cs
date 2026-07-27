using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;   // GameResultDigest
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 試合中の守備側交代を「守備の割当」1操作へ統合した seam（issue #256・設計書09 §6）。継投・投野交代・守備固め・
/// DH解除がプレイヤー采配（<see cref="MatchProgression"/>）から <see cref="MatchProgression.Relieve"/>／
/// <see cref="MatchProgression.DefenseAssign"/>／<see cref="MatchProgression.SwapDefensivePositions"/>／
/// <see cref="MatchProgression.ReleaseDh"/> で行え、AIと同じ継投候補集合（<see cref="SubstitutionOptions.ReliefCandidates"/>）
/// を持ち、決定列（<see cref="GameDecision"/>）へ載って「同シード＋同操作 → 同結果」「Save/Load 往復で同結果」
/// （不変条件#2 決定論）であることを保証する。
/// </summary>
public sealed class MatchProgressionUnifiedDefenseTests
{
    private static Player P(FieldPosition pos, string name) => new()
    {
        Position = pos, Name = name,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Rp(string name) => P(FieldPosition.Pitcher, name) with { Pitching = PitcherAttributes.LeagueAverage };

    private static PitcherAttributes Latent => new()
    {
        MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(85),
        Control = 60, StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(45), PitchRank = 60,
    };

    /// <summary>非DHチーム。遊撃手に潜在投手能力（在フィールド野手の登板を意味のある形で検証する）。</summary>
    private static Team Team(string n) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕"), P(FieldPosition.FirstBase, n + "一"),
            P(FieldPosition.SecondBase, n + "二"), P(FieldPosition.ThirdBase, n + "三"),
            P(FieldPosition.Shortstop, n + "遊") with { LatentPitching = Latent }, P(FieldPosition.LeftField, n + "左"),
            P(FieldPosition.CenterField, n + "中"), P(FieldPosition.RightField, n + "右"),
            Rp(n + "先発P"),
        },
        PitcherSlot = 8,
        Bullpen = new[] { Rp(n + "二番手"), Rp(n + "三番手") },
        Bench = new[] { P(FieldPosition.FirstBase, n + "控1"), P(FieldPosition.LeftField, n + "控2") },
    };

    /// <summary>DH制チーム（打順9人＝野手8＋DH、投手は打順外）。遊撃手に潜在投手能力。</summary>
    private static Team DhTeam(string n) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕"), P(FieldPosition.FirstBase, n + "一"),
            P(FieldPosition.SecondBase, n + "二"), P(FieldPosition.ThirdBase, n + "三"),
            P(FieldPosition.Shortstop, n + "遊") with { LatentPitching = Latent }, P(FieldPosition.LeftField, n + "左"),
            P(FieldPosition.CenterField, n + "中"), P(FieldPosition.RightField, n + "右"),
            P(FieldPosition.DesignatedHitter, n + "DH"),
        },
        DhSlot = 8,
        StartingPitcher = Rp(n + "先発P"),
        Bullpen = new[] { Rp(n + "二番手") },
        Bench = new[] { P(FieldPosition.FirstBase, n + "控1") },
    };

    private static MatchProgression NewProg(ulong seed)
        => new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed);

    private static MatchProgression NewDhProg(ulong seed)
        => new(DhTeam("A"), DhTeam("H"), new GameContext { CaptureTimelines = true }, seed);

    /// <summary>teamIsAway 側が守備の打席境界まで進める。</summary>
    private static SubstitutionOptions? AdvanceUntilDefense(MatchProgression prog, bool teamIsAway)
    {
        while (prog.Advance())
        {
            var o = prog.SubstitutionOptions(teamIsAway);
            if (!o.IsOffense) return o;
        }
        return null;
    }

    // ===== 1. 継投候補集合がAIと同一（ブルペン＋ベンチ＋在フィールド野手） =====

    [Fact]
    public void ReliefCandidates_MatchAiSet_IncludeOnFieldFielders_NonDh()
    {
        var prog = NewProg(11UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);

        // ブルペン2 + ベンチ2 + 在フィールド野手8（現投手を除く打順9-1）= 12。
        Assert.Equal(12, opt!.ReliefCandidates.Count);
        Assert.Contains(opt.ReliefCandidates, p => p.Name == "H遊");   // 在フィールド野手が候補に入る
        Assert.DoesNotContain(opt.ReliefCandidates, p => p.Name == opt.CurrentPitcher.Name); // 現投手は除外
    }

    [Fact]
    public void ReliefCandidates_MatchAiSet_IncludeOnFieldFielders_Dh()
    {
        var prog = NewDhProg(11UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);

        // DHでも在フィールド野手8（DH除く打順9-1）を含める＝ブルペン1 + ベンチ1 + 8 = 10。
        Assert.Equal(10, opt!.ReliefCandidates.Count);
        Assert.Contains(opt.ReliefCandidates, p => p.Name == "H遊");
        Assert.DoesNotContain(opt.ReliefCandidates, p => p.Name == "HDH");  // DHは守備に就かない＝除外
    }

    // ===== 2. ベンチ→投（継投） =====

    [Fact]
    public void Relieve_BenchSidePitcher_ChangesPitcher_NonDh()
    {
        var prog = NewProg(13UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var reliever = opt!.Bullpen[1];   // 先頭固定ではないことも兼ねる
        var old = opt.CurrentPitcher;

        Assert.True(prog.Relieve(teamIsAway: false, reliever));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal(reliever.Name, after.CurrentPitcher.Name);
        Assert.DoesNotContain(after.ReliefCandidates, p => p.Name == old.Name);   // 旧投手は退場（再登板不可）
    }

    [Fact]
    public void Relieve_BenchSidePitcher_ChangesPitcher_KeepsDh()
    {
        var prog = NewDhProg(13UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var reliever = opt!.Bullpen[0];

        Assert.True(prog.Relieve(teamIsAway: false, reliever));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal(reliever.Name, after.CurrentPitcher.Name);
        Assert.True(after.UsesDh);   // ブルペン継投ではDHは維持（野手を上げていない）
    }

    // ===== 3. 在フィールド野手→投（投野交代・空き守備を backfill） =====

    [Fact]
    public void Relieve_OnFieldFielder_PromotesToMound_BackfillsHole_NonDh()
    {
        var prog = NewProg(7UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var ss = opt!.Lineup.First(p => p.Position == FieldPosition.Shortstop);
        var oldPitcher = opt.CurrentPitcher;

        Assert.True(prog.Relieve(teamIsAway: false, ss));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal(ss.Name, after.CurrentPitcher.Name);                 // 遊撃手がマウンドへ
        // 空き守備を backfill：旧投手が遊撃へ回る（能力同値なので path①・両者残留）。守備に穴が開かない。
        Assert.Contains(after.Lineup, p => p.Name == oldPitcher.Name && p.Position == FieldPosition.Shortstop);
        Assert.Equal(9, after.Lineup.Select(p => p.Position).Distinct().Count());
    }

    [Fact]
    public void Relieve_OnFieldFielder_PromotesToMound_ReleasesDh_Dh()
    {
        var prog = NewDhProg(7UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var ss = opt!.Lineup.First(p => p.Position == FieldPosition.Shortstop);
        var oldPitcher = opt.CurrentPitcher;

        Assert.True(prog.Relieve(teamIsAway: false, ss));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal(ss.Name, after.CurrentPitcher.Name);
        Assert.False(after.UsesDh);                                       // 野手登板でDHは消滅（DH解除と同じ終着）
        Assert.DoesNotContain(after.ReliefCandidates, p => p.Name == oldPitcher.Name);   // DHでは旧投手は打順外＝退場
        Assert.NotNull(after.Lineup.FirstOrDefault(p => p.Position == FieldPosition.Shortstop));  // 遊撃の穴は埋まる
    }

    // ===== 4. 在フィールド同士の入替（守備位置の入替・誰も退場しない） =====

    [Fact]
    public void SwapDefensivePositions_TwoFielders_NoRetire_NonDh()
    {
        var prog = NewProg(17UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var before = opt!.Lineup.Select(p => p.Name).ToList();

        Assert.True(prog.SwapDefensivePositions(teamIsAway: false, 0, 4));   // 捕 ⇄ 遊

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal(before, after.Lineup.Select(p => p.Name).ToList());     // 顔ぶれ不変＝誰も退場しない
        Assert.Equal(FieldPosition.Shortstop, after.Lineup[0].Position);
        Assert.Equal(FieldPosition.Catcher, after.Lineup[4].Position);
    }

    [Fact]
    public void SwapDefensivePositions_TwoFielders_NoRetire_Dh()
    {
        var prog = NewDhProg(17UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var before = opt!.Lineup.Select(p => p.Name).ToList();

        Assert.True(prog.SwapDefensivePositions(teamIsAway: false, 0, 4));   // 捕 ⇄ 遊（DH枠8は対象外）

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal(before, after.Lineup.Select(p => p.Name).ToList());
        Assert.Equal(FieldPosition.Shortstop, after.Lineup[0].Position);
        Assert.Equal(FieldPosition.Catcher, after.Lineup[4].Position);
        // DHスロットは入替対象外（守備に就かない）。
        Assert.False(prog.SwapDefensivePositions(teamIsAway: false, 0, after.DhSlot));
    }

    // ===== 5. 指→守備位置（DH解除。非DHには指枠が無いので DH のみ） =====

    [Fact]
    public void ReleaseDh_DhTakesPosition_NonDhHasNoDhSlot()
    {
        // DH制: 指→左翼（左翼手は退場）、投手が打順へ。
        var prog = NewDhProg(23UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        Assert.True(opt!.CanReleaseDh);
        var dh = opt.Lineup[opt.DhSlot];
        var displaced = opt.Lineup.First(p => p.Position == FieldPosition.LeftField);

        Assert.True(prog.ReleaseDh(teamIsAway: false, FieldPosition.LeftField));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.False(after.UsesDh);
        Assert.Contains(after.Lineup, p => p.Name == dh.Name && p.Position == FieldPosition.LeftField);
        Assert.DoesNotContain(after.Lineup, p => p.Name == displaced.Name);

        // 非DH制には指枠が無い＝DH解除は出せない（指→守備位置の非DH対応物は存在しない）。
        var nonDh = AdvanceUntilDefense(NewProg(23UL), teamIsAway: false);
        Assert.False(nonDh!.CanReleaseDh);
    }

    // ===== 6. ベンチ→守備位置（守備固め・割当） =====

    [Fact]
    public void DefenseAssign_BenchFielderToSlot_RetiresIncumbent_NonDh()
    {
        var prog = NewProg(29UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var slot = opt!.Lineup.ToList().FindIndex(p => p.Position == FieldPosition.CenterField);
        var incumbent = opt.Lineup[slot];
        var sub = opt.Bench[0];

        Assert.True(prog.DefenseAssign(teamIsAway: false, slot, sub));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Contains(after.Lineup, p => p.Name == sub.Name && p.Position == FieldPosition.CenterField);
        Assert.DoesNotContain(after.Lineup, p => p.Name == incumbent.Name);   // 就いていた選手は退場
        Assert.DoesNotContain(after.Bench, p => p.Name == sub.Name);          // 控えから消費
    }

    [Fact]
    public void DefenseAssign_BenchFielderToSlot_RetiresIncumbent_Dh()
    {
        var prog = NewDhProg(29UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var slot = opt!.Lineup.ToList().FindIndex(p => p.Position == FieldPosition.FirstBase);
        var incumbent = opt.Lineup[slot];
        var sub = opt.Bench[0];

        Assert.True(prog.DefenseAssign(teamIsAway: false, slot, sub));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Contains(after.Lineup, p => p.Name == sub.Name && p.Position == FieldPosition.FirstBase);
        Assert.DoesNotContain(after.Lineup, p => p.Name == incumbent.Name);
        Assert.True(after.UsesDh);   // 守備固めはDHに触れない

        // 投手スロット・DHスロットへの割当は拒否（登板の変更・DH解除は別操作）。
        Assert.False(prog.DefenseAssign(teamIsAway: false, after.DhSlot, sub));
    }

    // ===== 7. 決定論（同シード＋同操作／Save-Load 往復。新コマンドの再現配線を実証） =====

    /// <summary>継投（在フィールド野手登板）＋守備割当を織り交ぜて最後まで進める共通シナリオ。</summary>
    private static void RunUnified(ulong seed, out string digest)
    {
        var prog = NewProg(seed);
        var relieved = false;
        var assigned = false;
        while (prog.Advance())
        {
            var def = prog.SubstitutionOptions(teamIsAway: false);
            if (def.IsOffense) continue;
            if (!relieved && prog.ConfirmedPlateAppearances > 6)
            {
                var ss = def.Lineup.FirstOrDefault(p => p.Position == FieldPosition.Shortstop);
                if (ss is not null) relieved = prog.Relieve(false, ss);   // 在フィールド野手→投
            }
            else if (relieved && !assigned && def.Bench.Count > 0)
            {
                var slot = def.Lineup.ToList().FindIndex(p => p.Position == FieldPosition.RightField);
                if (slot >= 0) assigned = prog.DefenseAssign(false, slot, def.Bench[0]);   // ベンチ→守備位置
            }
        }
        Assert.True(relieved && assigned, "シナリオが継投・守備割当を実際に行使していない（テストの前提が崩れている）");
        digest = GameResultDigest.Sha256Of(prog.BuildResult());
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void SameSeedAndSameUnifiedOps_ProduceTheSameResult(ulong seed)
    {
        RunUnified(seed, out var a);
        RunUnified(seed, out var b);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void SaveLoadRoundTrip_WithUnifiedOps_ProducesTheSameResult(ulong seed)
    {
        var prog = NewProg(seed);
        var relieved = false;
        var assigned = false;
        while (prog.ConfirmedPlateAppearances < 30 && prog.Advance())
        {
            var def = prog.SubstitutionOptions(teamIsAway: false);
            if (def.IsOffense) continue;
            if (!relieved && prog.ConfirmedPlateAppearances > 6)
            {
                var ss = def.Lineup.FirstOrDefault(p => p.Position == FieldPosition.Shortstop);
                if (ss is not null) relieved = prog.Relieve(false, ss);
            }
            else if (relieved && !assigned && def.Bench.Count > 0)
            {
                var slot = def.Lineup.ToList().FindIndex(p => p.Position == FieldPosition.RightField);
                if (slot >= 0) assigned = prog.DefenseAssign(false, slot, def.Bench[0]);
            }
        }
        Assert.True(relieved && assigned);

        var save = prog.Save();
        var straight = GameResultDigest.Sha256Of(prog.FinishRemaining());

        var json = JsonSerializer.Serialize(save);
        var back = JsonSerializer.Deserialize<GameSaveState>(json)!;
        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, back);
        while (resumed.Steps.MoveNext()) { /* 続行 */ }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(straight, restored);
    }

    [Fact]
    public void UnifiedOpsQueriesOnly_DoNotConsumeRandomness_MatchesBatchPlay()
    {
        // 観測（SubstitutionOptions／ReliefCandidates）だけを毎打席引いても、結果はバッチ Play と1ビットも変わらない。
        var batch = GameEngine.Play(Team("A"), Team("H"), new GameContext { CaptureTimelines = true },
            new Xoshiro256Random(41UL));

        var prog = NewProg(41UL);
        while (prog.Advance())
        {
            _ = prog.SubstitutionOptions(teamIsAway: true).ReliefCandidates;
            _ = prog.SubstitutionOptions(teamIsAway: false).ReliefCandidates;
        }

        Assert.Equal(GameResultDigest.Sha256Of(batch), GameResultDigest.Sha256Of(prog.BuildResult()));
    }

    // ===== 8. 継投直後の進行が有限ステップで打席を返す（issue #288 再現ガード） =====
    //
    // 症状: 対決ビュー中に自校の継投を確定して試合画面へ戻ると Unity ごとフリーズした。
    // 単一スレッドの engine から見れば Relieve() は進行体（GameStep 列・ラインナップ）を壊さず、
    // 直後の Advance()／FinishRemaining() は必ず有限ステップで打席を返し切る（＝engine 単体に
    // 無限ループは無い。打席は AtBatResolver.MaxPitches、半イニングは3アウト、試合は MaxInnings で有界）。
    // 実機のフリーズはメイン／背景スレッドの競合（#278 と同型）が主因で、UI 側の二重ガード（Apply の
    // BackgroundGameOp.Running チェック）と、万一競合が滑り込んでも MatchProgression が安全上限で
    // 例外化して落ちる（フリーズしない）ことで対処する。ここでは engine 側の有界性を回帰で固定する。

    [Theory]
    [InlineData(3UL)]
    [InlineData(7UL)]
    [InlineData(13UL)]
    [InlineData(31UL)]
    [InlineData(44UL)]
    public void Advance_AfterBullpenRelief_ReturnsPlateAppearancesInFiniteSteps(ulong seed)
    {
        var prog = NewProg(seed);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);

        // ブルペン継投（対決ビューで確定した「控え投手の投入」に相当）。
        var reliever = opt!.Bullpen[0];
        Assert.True(prog.Relieve(teamIsAway: false, reliever));
        Assert.Equal(reliever.Name, prog.SubstitutionOptions(teamIsAway: false).CurrentPitcher.Name);

        // 継投直後（試合画面へ戻った直後に background で走る Advance に相当）から、進行が
        // 有限ステップで打席を返し続けること。安全上限に達すれば例外で落ちる（＝止まらない＝フリーズしない）。
        var advanced = 0;
        while (prog.Advance()) advanced++;
        Assert.True(advanced > 0, "継投直後に1打席も進まなかった（進行が止まっている）");
        Assert.True(prog.IsFinished, "継投を挟んでも試合は有限で終局する");
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(7UL)]
    [InlineData(31UL)]
    public void Advance_AfterOnFieldFielderRelief_ReturnsPlateAppearancesInFiniteSteps(ulong seed)
    {
        // 在フィールド野手登板（投野交代で守備穴を backfill する経路）でも同様に有限で進むこと。
        var prog = NewProg(seed);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);

        var ss = opt!.Lineup.First(p => p.Position == FieldPosition.Shortstop);
        Assert.True(prog.Relieve(teamIsAway: false, ss));
        Assert.Equal(ss.Name, prog.SubstitutionOptions(teamIsAway: false).CurrentPitcher.Name);

        var advanced = 0;
        while (prog.Advance()) advanced++;
        Assert.True(advanced > 0, "継投直後に1打席も進まなかった（進行が止まっている）");
        Assert.True(prog.IsFinished, "投野交代を挟んでも試合は有限で終局する");
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void FinishRemaining_AfterRelief_TerminatesFinitely(ulong seed)
    {
        // 「委任スキップ／残り一括消化」経路（DrainToEnd）も継投後に有界であること。
        var prog = NewProg(seed);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        Assert.True(prog.Relieve(teamIsAway: false, opt!.Bullpen[0]));

        var result = prog.FinishRemaining();
        Assert.True(prog.IsFinished);
        Assert.True(result.InningsPlayed > 0);
    }
}
