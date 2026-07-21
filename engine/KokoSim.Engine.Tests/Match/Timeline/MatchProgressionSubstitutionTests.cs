using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 試合中の選手交代 seam（issue #22 B・設計書09 §6）。
/// 代打・代走・投手交代（指名）・守備交代・DH解除の5機能が <see cref="MatchProgression"/> から操作でき、
/// リエントリー禁止が守られ、交代を挟んでも「同シード＋同交代操作 → 同結果」「Save/Load 往復で同結果」
/// （不変条件#2 決定論）であることを保証する。
/// </summary>
public sealed class MatchProgressionSubstitutionTests
{
    private static Player P(FieldPosition pos, string name) => new()
    {
        Position = pos, Name = name,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Rp(string name) => P(FieldPosition.Pitcher, name) with { Pitching = PitcherAttributes.LeagueAverage };

    /// <summary>非DH制のチーム（控え3人・ブルペン3人）。交代の供給元を厚めに用意する。</summary>
    private static Team Team(string n) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕"), P(FieldPosition.FirstBase, n + "一"),
            P(FieldPosition.SecondBase, n + "二"), P(FieldPosition.ThirdBase, n + "三"),
            P(FieldPosition.Shortstop, n + "遊"), P(FieldPosition.LeftField, n + "左"),
            P(FieldPosition.CenterField, n + "中"), P(FieldPosition.RightField, n + "右"),
            Rp(n + "先発P"),
        },
        PitcherSlot = 8,
        Bullpen = new[] { Rp(n + "二番手"), Rp(n + "三番手"), Rp(n + "四番手") },
        Bench = new[]
        {
            P(FieldPosition.FirstBase, n + "控1"), P(FieldPosition.SecondBase, n + "控2"),
            P(FieldPosition.LeftField, n + "控3"),
        },
    };

    /// <summary>DH制のチーム（打順9人＝野手8＋DH、投手は打順外）。</summary>
    private static Team DhTeam(string n) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕"), P(FieldPosition.FirstBase, n + "一"),
            P(FieldPosition.SecondBase, n + "二"), P(FieldPosition.ThirdBase, n + "三"),
            P(FieldPosition.Shortstop, n + "遊"), P(FieldPosition.LeftField, n + "左"),
            P(FieldPosition.CenterField, n + "中"), P(FieldPosition.RightField, n + "右"),
            P(FieldPosition.Catcher, n + "DH"),
        },
        DhSlot = 8,
        StartingPitcher = Rp(n + "先発P"),
        Bullpen = new[] { Rp(n + "二番手") },
        Bench = new[] { P(FieldPosition.FirstBase, n + "控1") },
    };

    private static MatchProgression NewProg(ulong seed)
        => new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed);

    // ===== 交代の適用先を安定して作るヘルパ =====

    /// <summary>teamIsAway 側が攻撃で、かつ塁上に走者がいる打席境界まで進める（見つからなければ null）。</summary>
    private static SubstitutionOptions? AdvanceUntilRunnerOn(MatchProgression prog, bool teamIsAway)
    {
        while (prog.Advance())
        {
            var o = prog.SubstitutionOptions(teamIsAway);
            if (o.CanPinchRun) return o;
        }
        return null;
    }

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

    // ===== 1. 局面の問い合わせ =====

    [Fact]
    public void SubstitutionOptions_OffersOffensiveChoicesWhileBatting_AndDefensiveChoicesWhileFielding()
    {
        var prog = NewProg(11UL);
        prog.Advance();

        // 先攻(away)が打っているので away は攻撃側・home は守備側。
        var away = prog.SubstitutionOptions(teamIsAway: true);
        var home = prog.SubstitutionOptions(teamIsAway: false);

        Assert.True(away.IsOffense);
        Assert.True(away.CanPinchHit);
        Assert.False(away.CanChangePitcher);
        Assert.Equal("攻撃中は投手交代できない。", away.BlockedReasonFor(SubstitutionKind.ChangePitcher));

        Assert.False(home.IsOffense);
        Assert.True(home.CanChangePitcher);
        Assert.True(home.CanDefensiveSub);
        Assert.False(home.CanPinchHit);
        Assert.Equal("守備中は代打を送れない。", home.BlockedReasonFor(SubstitutionKind.PinchHit));
        // 非DH制なので DH 解除は出せない。
        Assert.False(home.CanReleaseDh);
        Assert.Equal("DHを使っていない（または解除済み）。", home.BlockedReasonFor(SubstitutionKind.ReleaseDh));
    }

    [Fact]
    public void SubstitutionOptions_ExhaustedBench_ReportsReason()
    {
        var prog = new MatchProgression(
            Team("A") with { Bench = System.Array.Empty<Player>(), Bullpen = System.Array.Empty<Player>() },
            Team("H"), new GameContext(), 5UL);
        prog.Advance();

        var away = prog.SubstitutionOptions(teamIsAway: true);
        Assert.False(away.CanPinchHit);
        Assert.Equal("控えの野手が残っていない。", away.BlockedReasonFor(SubstitutionKind.PinchHit));
    }

    // ===== 2. 5機能それぞれの単体（1機能=1テスト以上） =====

    [Fact]
    public void PinchHit_ReplacesUpcomingBatter()
    {
        var prog = NewProg(3UL);
        prog.Advance();
        var opt = prog.SubstitutionOptions(teamIsAway: true);
        var sub = opt.Bench[0];
        var slot = opt.UpcomingBatterSlot;

        Assert.True(prog.PinchHit(teamIsAway: true, sub));

        var after = prog.SubstitutionOptions(teamIsAway: true);
        Assert.Equal(sub.Name, after.Lineup[slot].Name);
        Assert.DoesNotContain(after.Bench, p => p.Name == sub.Name);
    }

    [Fact]
    public void PinchRun_ReplacesTheRunnerOnBase_AndTheLineupSlot()
    {
        var prog = NewProg(7UL);
        var opt = AdvanceUntilRunnerOn(prog, teamIsAway: true);
        Assert.NotNull(opt);

        var choice = opt!.Runners[0];
        var sub = opt.Bench[0];
        Assert.True(prog.PinchRun(teamIsAway: true, choice.BaseIndex, sub));

        var after = prog.SubstitutionOptions(teamIsAway: true);
        // 打順スロットが代走に置き換わり、塁上の参照も同じ選手を指す。
        Assert.Contains(after.Lineup, p => p.Name == sub.Name);
        Assert.DoesNotContain(after.Lineup, p => p.Name == choice.Runner.Name);
        Assert.Contains(after.Runners, r => r.BaseIndex == choice.BaseIndex && r.Runner.Name == sub.Name);
        Assert.DoesNotContain(after.Bench, p => p.Name == sub.Name);
    }

    [Fact]
    public void ChangePitcher_DesignatesAnyBullpenPitcher()
    {
        var prog = NewProg(13UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);

        var third = opt!.Bullpen[2];   // 先頭ではなく3人目を指名する（TryChangePitcher との違い）
        Assert.True(prog.ChangePitcher(teamIsAway: false, third));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal(third.Name, after.CurrentPitcher.Name);
        Assert.DoesNotContain(after.Bullpen, p => p.Name == third.Name);
        Assert.Contains(after.Bullpen, p => p.Name == opt.Bullpen[0].Name);   // 飛ばした投手はまだ使える
    }

    [Fact]
    public void DefensiveSub_SwapsAFielder_AndInheritsThePosition()
    {
        var prog = NewProg(17UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);

        var outgoing = opt!.Lineup.First(p => p.Position == FieldPosition.Shortstop);
        var sub = opt.Bench[0];
        Assert.True(prog.DefensiveSub(teamIsAway: false, outgoing, sub));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Contains(after.Lineup, p => p.Name == sub.Name && p.Position == FieldPosition.Shortstop);
        Assert.DoesNotContain(after.Lineup, p => p.Name == outgoing.Name);
    }

    [Fact]
    public void ReleaseDh_BothBranches_AreReachableFromTheSeam()
    {
        // 分岐1: DHの選手が守備に就く（その守備位置の選手が退場）。
        var prog = new MatchProgression(DhTeam("A"), DhTeam("H"), new GameContext(), 23UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        Assert.True(opt!.CanReleaseDh);
        Assert.Contains(FieldPosition.LeftField, opt.DhFieldingChoices());

        var dh = opt.Lineup[opt.DhSlot];
        var displaced = opt.Lineup.First(p => p.Position == FieldPosition.LeftField);
        Assert.True(prog.ReleaseDh(teamIsAway: false, FieldPosition.LeftField));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.False(after.UsesDh);
        Assert.Contains(after.Lineup, p => p.Name == dh.Name && p.Position == FieldPosition.LeftField);
        Assert.DoesNotContain(after.Lineup, p => p.Name == displaced.Name);
        // 投手が打順へ入り、以降DHは復活しない（不可逆）。
        Assert.Equal(after.CurrentPitcher.Name, after.Lineup[after.PitcherSlot].Name);
        Assert.False(prog.ReleaseDh(teamIsAway: false, null));

        // 分岐2: DHの選手をそのまま退かせる（投手がDHの打順へ立つだけ）。
        var prog2 = new MatchProgression(DhTeam("A"), DhTeam("H"), new GameContext(), 23UL);
        var opt2 = AdvanceUntilDefense(prog2, teamIsAway: false);
        Assert.NotNull(opt2);
        var dh2 = opt2!.Lineup[opt2.DhSlot];
        Assert.True(prog2.ReleaseDh(teamIsAway: false, null));

        var after2 = prog2.SubstitutionOptions(teamIsAway: false);
        Assert.False(after2.UsesDh);
        Assert.DoesNotContain(after2.Lineup, p => p.Name == dh2.Name);
        Assert.Equal(after2.CurrentPitcher.Name, after2.Lineup[after2.DhSlot].Name);
    }

    // ===== 3. リエントリー禁止 =====

    [Fact]
    public void RetiredPlayers_CannotReenter()
    {
        var prog = NewProg(29UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);

        var outgoing = opt!.Lineup.First(p => p.Position == FieldPosition.CenterField);
        var sub = opt.Bench[0];
        Assert.True(prog.DefensiveSub(teamIsAway: false, outgoing, sub));

        // 退場した選手を控えとして指定しても false（そもそも控えに居ない）。
        Assert.False(prog.DefensiveSub(teamIsAway: false, sub, outgoing));
        // 一度使った控えは二度使えない。
        var another = prog.SubstitutionOptions(teamIsAway: false).Lineup.First(p => p.Position == FieldPosition.RightField);
        Assert.False(prog.DefensiveSub(teamIsAway: false, another, sub));

        // 降板した投手も再登板できない。
        var starter = prog.SubstitutionOptions(teamIsAway: false).CurrentPitcher;
        var reliever = prog.SubstitutionOptions(teamIsAway: false).Bullpen[0];
        Assert.True(prog.ChangePitcher(teamIsAway: false, reliever));
        Assert.False(prog.ChangePitcher(teamIsAway: false, starter));

        // 使い切った控え・登板済みの投手は UI のグレーアウト用に別枠で出る（選択肢からは外れている）。
        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Contains(after.UsedBench, p => p.Name == sub.Name);
        Assert.DoesNotContain(after.Bench, p => p.Name == sub.Name);
        Assert.Contains(after.UsedBullpen, p => p.Name == reliever.Name);
    }

    // ===== 4. 決定論（同シード＋同交代操作／Save-Load 往復） =====

    /// <summary>交代を織り交ぜて最後まで進める共通シナリオ（両方の呼び出しで完全に同じ操作列を辿る）。</summary>
    private static MatchProgression RunWithSubstitutions(ulong seed, out string digest)
    {
        var prog = NewProg(seed);
        var pinchRunDone = false;
        var pinchHitDone = false;
        var pitcherDone = false;
        var defSubDone = false;

        while (prog.Advance())
        {
            var off = prog.SubstitutionOptions(teamIsAway: true);
            if (!pinchRunDone && off.CanPinchRun && prog.ConfirmedPlateAppearances > 5)
                pinchRunDone = prog.PinchRun(true, off.Runners[0].BaseIndex, off.Bench[0]);
            else if (!pinchHitDone && pinchRunDone && off.CanPinchHit)
                pinchHitDone = prog.PinchHit(true, off.Bench[0]);

            var def = prog.SubstitutionOptions(teamIsAway: false);
            if (!pitcherDone && def.CanChangePitcher && prog.ConfirmedPlateAppearances > 12)
                pitcherDone = prog.ChangePitcher(false, def.Bullpen[1]);
            else if (!defSubDone && pitcherDone && def.CanDefensiveSub)
                defSubDone = prog.DefensiveSub(false, def.Lineup[0], def.Bench[0]);
        }

        Assert.True(pinchRunDone && pinchHitDone && pitcherDone && defSubDone,
            "シナリオが5機能を実際に行使していない（テストの前提が崩れている）");
        digest = GameResultDigest.Sha256Of(prog.BuildResult());
        return prog;
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void SameSeedAndSameSubstitutions_ProduceTheSameResult(ulong seed)
    {
        RunWithSubstitutions(seed, out var a);
        RunWithSubstitutions(seed, out var b);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void SaveLoadRoundTrip_WithSubstitutions_ProducesTheSameResult(ulong seed)
    {
        // 交代を挟みつつ途中まで進めて保存する。
        var prog = NewProg(seed);
        var applied = 0;
        while (prog.ConfirmedPlateAppearances < 30 && prog.Advance())
        {
            var off = prog.SubstitutionOptions(teamIsAway: true);
            if (applied == 0 && off.CanPinchRun && prog.PinchRun(true, off.Runners[0].BaseIndex, off.Bench[0])) applied++;
            var def = prog.SubstitutionOptions(teamIsAway: false);
            if (applied == 1 && def.CanChangePitcher && prog.ChangePitcher(false, def.Bullpen[1])) applied++;
            if (applied == 2 && def.CanDefensiveSub && prog.DefensiveSub(false, def.Lineup[0], def.Bench[0])) applied++;
        }
        Assert.Equal(3, applied);

        // ここ（30打席時点・交代3件適用済み）で保存し、その後は中断なしで最後まで走らせる。
        var save = prog.Save();
        var straight = GameResultDigest.Sha256Of(prog.FinishRemaining());

        // 保存物（JSON往復でシリアライズ可能性も実証）→ 復元 → 続行。
        var json = JsonSerializer.Serialize(save);
        var back = JsonSerializer.Deserialize<GameSaveState>(json)!;
        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, back);
        while (resumed.Steps.MoveNext()) { /* 続行 */ }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(straight, restored);
    }

    [Fact]
    public void Substitutions_DoNotConsumeRandomness_SoNoSubstitutionMatchesBatchPlay()
    {
        // 交代の問い合わせ（観測）だけを毎打席行っても、結果はバッチ Play と1ビットも変わらない。
        var batch = GameEngine.Play(Team("A"), Team("H"), new GameContext { CaptureTimelines = true },
            new Xoshiro256Random(41UL));

        var prog = NewProg(41UL);
        while (prog.Advance())
        {
            prog.SubstitutionOptions(teamIsAway: true);
            prog.SubstitutionOptions(teamIsAway: false);
        }

        Assert.Equal(GameResultDigest.Sha256Of(batch), GameResultDigest.Sha256Of(prog.BuildResult()));
    }

    // ===== 5. 途中出場選手の成績が独立行として出ること =====

    [Fact]
    public void SubstitutePlayers_GetTheirOwnBoxScoreRows()
    {
        var prog = NewProg(3UL);
        prog.Advance();
        var opt = prog.SubstitutionOptions(teamIsAway: true);
        var sub = opt.Bench[0];
        var starter = opt.Lineup[opt.UpcomingBatterSlot];
        Assert.True(prog.PinchHit(teamIsAway: true, sub));

        var result = prog.FinishRemaining();

        // 途中出場の代打が独立した打撃成績行を持ち、退いた先発の行も残る（Playerキー集計）。
        Assert.Contains(result.AwayBatting, l => l.Name == sub.Name);
        Assert.Contains(result.AwayBatting, l => l.Name == starter.Name);
        Assert.True(result.AwayBatting.First(l => l.Name == sub.Name).PlateAppearances > 0);
    }

    [Fact]
    public void ReliefPitcher_GetsItsOwnPitchingRow()
    {
        var prog = NewProg(13UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var reliever = opt!.Bullpen[1];
        Assert.True(prog.ChangePitcher(teamIsAway: false, reliever));

        var result = prog.FinishRemaining();

        Assert.Contains(result.HomePitching, l => l.Name == reliever.Name);
        Assert.True(result.HomePitching.First(l => l.Name == reliever.Name).BattersFaced > 0);
    }
}
