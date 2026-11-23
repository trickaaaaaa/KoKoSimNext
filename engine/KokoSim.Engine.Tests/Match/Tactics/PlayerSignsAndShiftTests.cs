using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// issue #1473: プレイヤーの采配に足した4つ（申告敬遠・ウエスト・バントシフト・セーフティバント。バスターは作らない＝2026-11-23 オーナー判断）。
///
/// <para>①指示がエンジンへ届く（結果が変わる・意図した形で） ②指示しない試合は1ビットも変わらない
/// ③中断保存→復元で再現する、の3点を見る。チームは brain を持たない（両軍とも手動）＝AI の判断が混ざらない。</para>
/// </summary>
public sealed class PlayerSignsAndShiftTests
{
    // ── 申告敬遠 ──

    [Fact]
    public void IntentionalWalk_AtHeadWindow_WalksWithoutAPitch()
    {
        var prog = NewGame(42);
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());   // 1回表・先頭打者の頭の窓
        prog.SetIntentionalWalk(teamIsAway: false);                     // 守備は後攻
        Assert.Equal(AdvancePitchResult.PlateAppearance, prog.AdvancePitch());
        Assert.Equal(PlateAppearanceResult.Walk, prog.Current!.Result);
        Assert.True(prog.Current.BaseFirstAfter);

        while (prog.Advance()) { }
        var r = prog.BuildResult();
        Assert.Equal(1, r.IntentionalWalkCount);
        Assert.Equal(0, r.Log[0].Pitches);
    }

    /// <summary>カウントの途中で押しても、次の球を投げずにその打席を四球で終える（投げた球はそのまま数える）。</summary>
    [Fact]
    public void IntentionalWalk_MidPlateAppearance_EndsBeforeTheNextPitch()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var prog = NewGame(seed);
            Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;   // 初球で決着した打席は使わない
            var at = prog.CurrentAtBat!;
            Assert.Equal(2, at.PitchNumberInPa);
            prog.SetIntentionalWalk(teamIsAway: false);
            Assert.Equal(AdvancePitchResult.PlateAppearance, prog.AdvancePitch());
            Assert.Equal(PlateAppearanceResult.Walk, prog.Current!.Result);
            Assert.Single(prog.Current.PitchSeq.Pitches);   // 初球だけ。敬遠の後は1球も投げない
            while (prog.Advance()) { }
            var r = prog.BuildResult();
            Assert.Equal(1, r.IntentionalWalkCount);
            Assert.Equal(1, r.Log[0].Pitches);
            return;
        }
        Assert.Fail("初球で打席が終わらない試合が20シードで見つからない");
    }

    [Fact]
    public void IntentionalWalk_ReproducesViaSaveRestore()
    {
        AssertReproduces(prog =>
        {
            prog.AdvancePitch();
            prog.SetIntentionalWalk(teamIsAway: false);
        }, GameDecisionKind.IntentionalWalk);
    }

    // ── ウエスト ──

    /// <summary>
    /// 毎球ウエストを予約すると、どの打席も3ボールまで外し、3ボールからは外さない（四球にしない＝AI の読みと同じ規則）。
    /// </summary>
    [Fact]
    public void Pitchout_WastesUntilThreeBalls_NeverForBallFour()
    {
        var prog = NewGame(7);
        var r = PlayPitchwise(prog, (p, at) => { if (at.IsTop) p.SetPitchout(teamIsAway: false); });
        var top = r.Log.Where(e => e.IsTop && e.PitchLog is { Count: > 0 }).ToList();
        Assert.NotEmpty(top);
        foreach (var e in top)
        {
            var log = e.PitchLog!;
            for (var i = 0; i < log.Count; i++)
            {
                if (i < 3) Assert.True(log[i].Pitchout, $"{i + 1}球目が外されていない");
                else Assert.False(log[i].Pitchout, "3ボールからも外している（四球になる）");
                if (log[i].Pitchout) Assert.Equal(PitchKind.Ball, log[i].Kind);
            }
        }
        // 外した球は打者が振れない＝外した球では打席が終わらない（4球目以降の普通の球で決着する）。
        Assert.All(top, e => Assert.True(e.PitchLog!.Count >= 4));
    }

    /// <summary>ウエストとスクイズが重なると、外された三塁走者が挟まれてアウトになる（設計書09 §1）。</summary>
    [Fact]
    public void Pitchout_AgainstSqueeze_CatchesTheThirdRunner()
    {
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var prog = NewGame(seed);
            var res = prog.AdvancePitch();
            while (res != AdvancePitchResult.Finished)
            {
                if (res == AdvancePitchResult.Pitch && prog.CurrentAtBat is { IsTop: true, BaseThird: true, Outs: < 2, Balls: < 3 } at)
                {
                    prog.SetPitchBattingOverride(true, PitchBattingOverride.Squeeze);
                    prog.SetPitchout(teamIsAway: false);
                    prog.AdvancePitch();
                    var thrown = prog.LastPitch!;
                    Assert.Equal(PitchKind.Ball, thrown.Pitch.Kind);
                    Assert.False(thrown.BaseThird, "外されたのに三塁走者が残っている");
                    Assert.Equal(at.Outs + 1, thrown.Outs);
                    return;
                }
                res = prog.AdvancePitch();
            }
        }
        Assert.Fail("無死・一死で三塁に走者がいる窓が60シードで見つからない");
    }

    /// <summary>ウエストと盗塁が重なると、捕手が優位に立って刺しやすい（StealResolver へピッチアウトとして渡る）。</summary>
    [Fact]
    public void Pitchout_AgainstSteal_CatchesMoreRunners()
    {
        var (plainTry, plainSafe) = StealTally(pitchout: false);
        var (outTry, outSafe) = StealTally(pitchout: true);
        Assert.True(plainTry > 20 && outTry > 20, $"盗塁の企図が少ない（{plainTry}/{outTry}）");
        Assert.True((double)outSafe / outTry < (double)plainSafe / plainTry,
            $"ウエストで盗塁の成功率が下がっていない（なし{plainSafe}/{plainTry} → あり{outSafe}/{outTry}）");
    }

    private static (int Attempts, int Safe) StealTally(bool pitchout)
    {
        int attempts = 0, safe = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var r = PlayPitchwise(NewGame(seed), (p, at) =>
            {
                if (!at.IsTop || !at.BaseFirst || at.BaseSecond || at.Balls >= 3) return;
                p.SetStealAttempt(true, StartType.Normal);
                if (pitchout) p.SetPitchout(teamIsAway: false);
            });
            attempts += r.AwayTactics.StealAttempts;
            safe += r.AwayTactics.StealSuccesses;
        }
        return (attempts, safe);
    }

    /// <summary>
    /// ウエストとエンドランが重なった球（2026-11-23 オーナー判断）: 外した球で捕手が優位なまま、一塁走者を二塁で判定する
    /// ＝盗塁と同じ解決。打者は外された球に当てられない（ボール1つ）。
    /// </summary>
    [Fact]
    public void Pitchout_AgainstHitAndRun_DecidesTheRunnerAtSecond()
    {
        int decided = 0;
        for (ulong seed = 1; seed <= 40 && decided < 3; seed++)
        {
            var prog = NewGame(seed);
            var res = prog.AdvancePitch();
            while (res != AdvancePitchResult.Finished)
            {
                if (res == AdvancePitchResult.Pitch
                    && prog.CurrentAtBat is { IsTop: true, BaseFirst: true, BaseSecond: false, BaseThird: false, Balls: < 3, Outs: < 2 } at)
                {
                    prog.SetOffensiveSign(true, OffensiveSign.HitAndRun);
                    prog.SetPitchout(teamIsAway: false);
                    prog.AdvancePitch();
                    var thrown = prog.LastPitch!;
                    Assert.Equal(PitchKind.Ball, thrown.Pitch.Kind);
                    Assert.False(thrown.BaseFirst, "一塁走者が一塁に残っている（二塁で判定されていない）");
                    // 二塁でセーフか、アウトが1つ増えたかのどちらか
                    Assert.True(thrown.BaseSecond ^ (thrown.Outs == at.Outs + 1), "二塁の判定が付いていない");
                    decided++;
                    break;
                }
                res = prog.AdvancePitch();
            }
        }
        Assert.True(decided >= 3, $"無死・一死で一塁だけに走者がいる窓が足りない（{decided}）");
    }

    [Fact]
    public void Pitchout_ReproducesViaSaveRestore()
    {
        AssertReproduces(prog =>
        {
            prog.AdvancePitch();
            prog.SetPitchout(teamIsAway: false);
        }, GameDecisionKind.Pitchout);
    }

    // ── 取り消し・既定は恒等 ──

    /// <summary>取り消し（on=false）と未指示は「一度も押していない試合」とバイト一致する＝既定は乱数を引かない。</summary>
    [Fact]
    public void CancelledSigns_MatchUntouchedGame()
    {
        var plain = Sha(PlayPitchwise(NewGame(42), null));
        var cancelled = Sha(PlayPitchwise(NewGame(42), (p, at) =>
        {
            p.SetIntentionalWalk(!at.IsTop, on: false);
            p.SetPitchout(!at.IsTop, on: false);
            p.SetPitchDefenseOverride(at.IsTop, null, null, buntShift: null);
        }));
        Assert.Equal(plain, cancelled);
    }

    // ── バントシフト ──

    /// <summary>
    /// バントシフトは一・三塁手の初期守備位置を前へ出す＝送りバントの結果が変わる（効果は幾何＝物理から出る）。
    /// 陣形と同じく単一シードでは判定しない（境目のプレーが無い試合は同じ結果になる）。
    /// </summary>
    [Fact]
    public void BuntShift_ChangesBuntOutcomes()
    {
        var changed = false;
        for (ulong seed = 1; seed <= 8 && !changed; seed++)
        {
            var plain = Sha(PlayPitchwise(NewGame(seed), (p, at) => BuntAgainst(p, at, shift: false)));
            var shifted = Sha(PlayPitchwise(NewGame(seed), (p, at) => BuntAgainst(p, at, shift: true)));
            changed = plain != shifted;
        }
        Assert.True(changed, "バントシフトを敷いてもバントの結果が一切変わらない");
    }

    /// <summary>先攻は走者がいれば毎球バント、後攻（守備）はバントシフトを毎球再予約（UI と同じ「解除するまで続く」形）。</summary>
    private static void BuntAgainst(MatchProgression p, LiveAtBat at, bool shift)
    {
        if (!at.IsTop) return;
        if (at.BaseFirst || at.BaseSecond) p.SetPitchBattingOverride(true, PitchBattingOverride.Bunt);
        if (shift) p.SetPitchDefenseOverride(true, null, null, buntShift: true);
    }

    [Fact]
    public void BuntShift_ReproducesViaSaveRestore()
    {
        var prog = NewGame(3);
        var direct = PlayPitchwise(prog, (p, at) => BuntAgainst(p, at, shift: true));
        var save = prog.Save();
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.PitchDefenseOverride && d.BuntShift == true);
        Assert.Equal(Sha(direct), RestoredSha(save));
    }

    // ── セーフティバント ──

    /// <summary>走者がいなくてもセーフティバントは転がせる（内野安打狙い）。犠打には数えない。</summary>
    [Fact]
    public void SafetyBunt_WorksWithBasesEmpty_AndIsNotASacrifice()
    {
        int buntsInPlay = 0, infieldHits = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var r = PlayPitchwise(NewGame(seed), (p, at) =>
            {
                if (at.IsTop && !at.BaseFirst && !at.BaseSecond && !at.BaseThird && at.Strikes < 2)
                    p.SetPitchBattingOverride(true, PitchBattingOverride.SafetyBunt);
            });
            foreach (var e in r.Log.Where(e => e.IsTop && !e.BaseFirstBefore && !e.BaseSecondBefore && !e.BaseThirdBefore))
            {
                Assert.False(e.IsSacBunt, "走者なしのセーフティバントが犠打になっている");
                if (e.PitchLog is { Count: > 0 } log && log[^1].Kind == PitchKind.InPlay
                    && log[^1].Sign == PitchBattingOverride.SafetyBunt)
                {
                    buntsInPlay++;
                    if (e.Result == PlateAppearanceResult.Single) infieldHits++;
                }
            }
        }
        Assert.True(buntsInPlay > 10, $"セーフティバントが転がっていない（{buntsInPlay}）");
        Assert.True(infieldHits > 0, "セーフティバントの内野安打が1本も無い");
    }

    // ── ヘルパ ──

    /// <summary>1球ずつ進め、采配窓ごとに perWindow を呼んで最後まで流す。</summary>
    private static GameResult PlayPitchwise(MatchProgression prog, Action<MatchProgression, LiveAtBat>? perWindow)
    {
        var res = prog.AdvancePitch();
        var guard = 0;
        while (res != AdvancePitchResult.Finished && guard++ < 5000)
        {
            if (res == AdvancePitchResult.Pitch && prog.CurrentAtBat is { } at) perWindow?.Invoke(prog, at);
            res = prog.AdvancePitch();
        }
        Assert.Equal(AdvancePitchResult.Finished, res);
        return prog.BuildResult();
    }

    private static void AssertReproduces(Action<MatchProgression> setup, GameDecisionKind kind)
    {
        var prog = NewGame(42);
        setup(prog);
        while (prog.Advance()) { }
        var direct = Sha(prog.BuildResult());
        var save = prog.Save();
        Assert.Contains(save.Decisions, d => d.Kind == kind);
        Assert.Equal(direct, RestoredSha(save));
    }

    private static string RestoredSha(GameSaveState save)
    {
        var resumed = GameReplay.Restore(Team("A"), Team("H"), Ctx, save);
        while (resumed.Steps.MoveNext()) { }
        return Sha(GameEngine.BuildResult(resumed.Progress));
    }

    private static string Sha(GameResult r) => GameResultDigest.Sha256Of(r);

    // 1球の窓（CurrentAtBat）と投げた球（LastPitch）を観測できるようにする（観測専用＝結果は変えない）。
    private static GameContext Ctx => new() { CaptureTimelines = true, CapturePitches = true };

    private static MatchProgression NewGame(ulong seed) => new(Team("A"), Team("H"), Ctx, seed);

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    private static Player Pos(FieldPosition p) => new()
    {
        Name = p.ToString(),
        Position = p,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50, Speed = 50,
    };
}
