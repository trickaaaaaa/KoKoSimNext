using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 対話進行ドライバ <see cref="MatchProgression"/> の受け入れ（設計者Claード条件4-5）:
///  (4b) 采配なしで全打席「次へ」した結果が、最初から Play() 一括と完全一致（＝単一コードパス）。
///  (5a) スキップ委任で残りを流した試合が完走する。
///  (代打) 7回に代打を1回入れると、以降の打席にその代打が現れる（采配がエンジンに届く実証）。
/// </summary>
public sealed class MatchProgressionTests
{
    private static Player Pos(FieldPosition pos, string? name = null) =>
        name is null
            ? new Player { Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50, Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50 }
            : new Player { Position = pos, Name = name, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50, Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50 };

    private const string PinchHitterName = "代打ヒーロー";

    private static Team Team(string name, bool withBench)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = KokoSim.Engine.Match.Game.TeamReserves.Merge(new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } }, withBench
                ? new[] { Pos(FieldPosition.FirstBase, PinchHitterName) with { Contact = 60 } }
                : System.Array.Empty<Player>()),
        };
    }

    // (4b) 采配なし全打席「次へ」＝バッチ Play() と完全一致。
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void ManualStepThroughAllPa_EqualsBatchPlay(ulong seed)
    {
        var batch = GameEngine.Play(Team("A", true), Team("H", true), new GameContext { CaptureTimelines = true },
            new Xoshiro256Random(seed));

        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, seed);
        while (prog.Advance()) { /* 采配なしで次へ */ }
        var manual = prog.BuildResult();

        Assert.Equal(GameResultDigest.Sha256Of(batch), GameResultDigest.Sha256Of(manual));
    }

    // (4c) 注入乱数 ctor で全打席「次へ」＝同じ rng を渡した Play() と完全一致（大会の隔離Fork をそのまま渡す経路）。
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void InjectedRng_ManualStepThroughAllPa_EqualsBatchPlay(ulong seed)
    {
        var batch = GameEngine.Play(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));

        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, (KokoSim.Engine.Core.IRandomSource)new Xoshiro256Random(seed));
        while (prog.Advance()) { /* 采配なしで次へ */ }
        var manual = prog.BuildResult();

        Assert.Equal(GameResultDigest.Sha256Of(batch), GameResultDigest.Sha256Of(manual));
    }

    // (観戦不変) CaptureTimelines は RNG 中立＝観戦(capture=true)しても自動消化(capture=false)とスコア一致。
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void CaptureTimelines_DoesNotChangeOutcome(ulong seed)
    {
        var noCapture = GameEngine.Play(Team("A", true), Team("H", true),
            new GameContext(), new Xoshiro256Random(seed));

        var live = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, (KokoSim.Engine.Core.IRandomSource)new Xoshiro256Random(seed));
        while (live.Advance()) { }
        var watched = live.BuildResult();

        Assert.Equal(noCapture.AwayRuns, watched.AwayRuns);
        Assert.Equal(noCapture.HomeRuns, watched.HomeRuns);
        Assert.Equal(noCapture.InningsPlayed, watched.InningsPlayed);
    }

    // (5a) スキップ委任で残りを流した試合が完走する。
    [Fact]
    public void SkipDelegate_CompletesGame()
    {
        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, 42UL);

        // 数打席だけ手動で進めてからスキップ。
        for (var i = 0; i < 5 && prog.Advance(); i++) { }
        var result = prog.SkipDelegateToAi(managerIsAway: false, managerTacticalSense: 70);

        Assert.True(prog.IsFinished);
        Assert.True(result.InningsPlayed >= 9, $"試合が完走していない（{result.InningsPlayed}回）");
        // 規定回で決着 or 引き分け上限。いずれにせよ有効な終局。
        Assert.True(result.AwayRuns >= 0 && result.HomeRuns >= 0);
    }

    // (PlayLog seam) 確定済み打席の記録が Advance のたびに1件ずつ増え、確定打席数と常に一致する。
    // 打者HUD「本日の打撃成績」の略記列は UI がこの読み取り専用 seam から引く（観測専用・帯不変）。
    [Fact]
    public void PlayLog_CountMatchesConfirmedPlateAppearances()
    {
        var prog = new MatchProgression(Team("A", true), Team("H", true), new GameContext(), 7UL);
        Assert.Empty(prog.PlayLog);

        var confirmed = 0;
        for (var i = 0; i < 10 && prog.Advance(); i++)
        {
            confirmed++;
            Assert.Equal(confirmed, prog.PlayLog.Count);
            // 末尾のエントリは今表示中の打席と同一（結果が一致）。
            Assert.Equal(prog.Current!.Result, prog.PlayLog[^1].Result);
        }
        Assert.Equal(10, confirmed); // 試合序盤の10打席は必ず存在する
    }

    // (代打) 7回に代打→以降の打席にその代打が現れる（采配がエンジンに届く）。
    [Fact]
    public void PinchHitInSeventh_AppearsInSubsequentPlateAppearances()
    {
        // 「7回に到達し、代打を送れて、その後に代打の打順が回ってくる」局面はRNG列に依存する
        // （Issue #24/#169 で過去にシードを更新してきた。issue #402 で係数既定が動きRNG列が再度変わったため、
        //  固定シードをやめて条件を満たすシードを探索する）。
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var prog = new MatchProgression(Team("A", true), Team("H", true),
                new GameContext { CaptureTimelines = true }, seed);

            // 7回に到達するまで手動で進める。到達前に終わったシードは飛ばす。
            while (prog.Advance() && prog.Current!.Inning < 7) { }
            if (prog.IsFinished) continue;

            // 自校（後攻 home）に代打を送る（采配窓）。送れないシードは飛ばす。
            if (!prog.PinchHitUpcoming(offenseIsAway: false, benchIndex: 0)) continue;

            // 以降の打席に代打ヒーローが現れることを確認。
            var appeared = false;
            while (prog.Advance())
                if (prog.Current!.BatterName == PinchHitterName) { appeared = true; break; }
            if (!appeared) continue;

            // 保存状態に代打決定が記録されている（中断保存で再現可能）。
            var save = prog.Save();
            Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.PinchHit && !d.OffenseIsAway);
            return; // 条件を満たすシードで検証完了
        }
        Assert.Fail("7回到達→代打→以降の打席に代打が登場するシードが 1..200 で見つからなかった");
    }

    // 代打の采配決定は、保存→復元でも再現される（決定を積んだ状態が一致）。
    [Fact]
    public void PinchHitDecision_ReproducesViaSaveRestore()
    {
        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, 42UL);
        while (prog.Advance() && prog.Current!.Inning < 7) { }
        prog.PinchHitUpcoming(offenseIsAway: false, benchIndex: 0);
        while (prog.Advance()) { }
        var direct = GameResultDigest.Sha256Of(prog.BuildResult());

        // 保存→復元（同シードで全打席を再生し、代打決定を同じ打席で適用して状態を再構築）。
        var save = prog.Save(); // ConfirmedPlateAppearances=全打席数, Decisions=代打1件
        var resumed = GameReplay.Restore(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, save);
        while (resumed.Steps.MoveNext()) { /* 既に終局まで再生済み */ }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(direct, restored);
    }

    /// <summary>
    /// 設計書15 Phase B-4: 対話進行の PitchSeq は <see cref="PitchSequenceSynthesizer"/> の架空合成ではなく、
    /// AtBatSession が実際に解いた PlayLogEntry.PitchLog（実データ）と1球ずつ一致する。
    /// </summary>
    [Fact]
    public void Advance_SurfacesRealPitchData_NotSynthesized()
    {
        const ulong seed = 5UL;
        var batch = GameEngine.Play(Team("A", false), Team("H", false), new GameContext(), new Xoshiro256Random(seed));

        var prog = new MatchProgression(Team("A", false), Team("H", false), new GameContext(), seed);
        var index = 0;
        while (prog.Advance())
        {
            var entry = batch.Log[index];
            var seq = prog.Current!.PitchSeq.Pitches;

            Assert.NotNull(entry.PitchLog);
            Assert.Equal(entry.PitchLog!.Count, seq.Count);
            for (var i = 0; i < seq.Count; i++)
            {
                Assert.Equal(entry.PitchLog[i].Kind, seq[i].Kind);
                Assert.Equal(entry.PitchLog[i].BallsAfter, seq[i].BallsAfter);
                Assert.Equal(entry.PitchLog[i].StrikesAfter, seq[i].StrikesAfter);
                // 球種・球速も実記録のまま貫通する（1球ごとの判定オーバーレイ表示用・表示専用）。
                Assert.Equal(entry.PitchLog[i].PitchType, seq[i].PitchType);
                Assert.Equal(entry.PitchLog[i].VelocityKmh, seq[i].VelocityKmh);
                Assert.NotNull(seq[i].VelocityKmh);
                Assert.InRange(seq[i].VelocityKmh!.Value, 60.0, 180.0);
            }
            index++;
        }
        Assert.Equal(batch.Log.Count, index);
    }

    /// <summary>
    /// Issue #332: コース（LocationX/Y）と観測弾道（Trajectory）が実1球記録のまま <see cref="PitchToken"/> へ載る。
    /// 対決ビューがボールを実コースへ飛ばすための配管で、写しのみ＝新たな抽選をしない（不変条件#2）。
    /// 弾道は <c>CaptureTimelines</c> の試合だけ積分される（統計シム・裏試合はゼロコストのまま）。
    /// </summary>
    [Fact]
    public void Advance_SurfacesPitchLocationAndTrajectory()
    {
        const ulong seed = 5UL;
        var batch = GameEngine.Play(Team("A", false), Team("H", false),
            new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));

        var prog = new MatchProgression(Team("A", false), Team("H", false),
            new GameContext { CaptureTimelines = true }, seed);
        var index = 0;
        var trajectoryCount = 0;
        while (prog.Advance())
        {
            var entry = batch.Log[index];
            var seq = prog.Current!.PitchSeq.Pitches;
            Assert.NotNull(entry.PitchLog);
            for (var i = 0; i < seq.Count; i++)
            {
                Assert.Equal(entry.PitchLog![i].LocationX, seq[i].LocationX);
                Assert.Equal(entry.PitchLog[i].LocationY, seq[i].LocationY);
                Assert.NotNull(seq[i].LocationX);
                Assert.NotNull(seq[i].LocationY);
                // 本塁面の実着弾位置。極端な暴投でも人が投げた球の範囲に収まる
                Assert.InRange(seq[i].LocationX!.Value, -1.5, 1.5);
                Assert.InRange(seq[i].LocationY!.Value, -0.5, 3.0);

                // record の値等価で比べる（batch と prog は別インスタンスの試合＝参照は別だが決定論で同値）
                Assert.Equal(entry.PitchLog[i].Trajectory, seq[i].Trajectory);
                if (seq[i].Trajectory is { } tr)
                {
                    trajectoryCount++;
                    Assert.InRange(tr.FlightTimeSeconds, 0.3, 0.8);
                }
            }
            index++;
        }
        Assert.True(trajectoryCount > 0, "CaptureTimelines の試合なのに弾道が1つも載っていない");
    }

    /// <summary>
    /// Issue #332: 弾道を積分しない試合（既定＝統計シム・裏試合）では Trajectory が null のまま。
    /// コースは判定に使う値なので常に載る（弾道だけが CaptureTimelines 依存）。
    /// </summary>
    [Fact]
    public void Advance_WithoutCaptureTimelines_LeavesTrajectoryNull()
    {
        var prog = new MatchProgression(Team("A", false), Team("H", false), new GameContext(), 5UL);
        var pitches = 0;
        while (prog.Advance())
        {
            foreach (var p in prog.Current!.PitchSeq.Pitches)
            {
                Assert.Null(p.Trajectory);
                Assert.NotNull(p.LocationX);
                pitches++;
            }
        }
        Assert.True(pitches > 0);
    }

    /// <summary>
    /// 設計書15 Phase C-3: プレイヤーの手動1球指示（ITacticsBrainを経由しない）が、次に解決される打席の
    /// 最初の1球にだけ効く。ForceTake は積極的な打者でも必ずBall/CalledStrikeにする。
    /// </summary>
    [Fact]
    public void SetPitchBattingOverride_ForcesFirstPitchOfNextPlateAppearance()
    {
        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, 42UL);
        while (prog.Advance() && prog.Current!.Inning < 3) { /* 適当に進める */ }
        Assert.False(prog.IsFinished, "3回到達前に試合が終わった（別シードを使う）");

        // 後攻(home)が攻撃側の打席で、次の1球にForceTakeを予約する。
        var offenseIsAway = prog.Current!.IsTop;
        prog.SetPitchBattingOverride(offenseIsAway, PitchBattingOverride.ForceTake);
        Assert.True(prog.Advance(), "打席を進められなかった");

        var firstPitch = prog.Current!.PitchSeq.Pitches[0];
        Assert.True(firstPitch.Kind is PitchKind.Ball or PitchKind.CalledStrike,
            $"ForceTakeを予約したのに初球が{firstPitch.Kind}");

        var save = prog.Save();
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.PitchBattingOverride && d.OffenseIsAway == offenseIsAway);
    }

    /// <summary>手動1球指示は、保存→復元でも再現される（PinchHitDecision_ReproducesViaSaveRestore と同型）。</summary>
    [Fact]
    public void PitchBattingOverrideDecision_ReproducesViaSaveRestore()
    {
        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, 42UL);
        while (prog.Advance() && prog.Current!.Inning < 3) { }
        var offenseIsAway = prog.Current!.IsTop;
        prog.SetPitchBattingOverride(offenseIsAway, PitchBattingOverride.ForceTake);
        while (prog.Advance()) { }
        var direct = GameResultDigest.Sha256Of(prog.BuildResult());

        var save = prog.Save();
        var resumed = GameReplay.Restore(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, save);
        while (resumed.Steps.MoveNext()) { }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(direct, restored);
    }

    // ── 設計書15 Phase D-1: 真の1球進行 ──

    /// <summary>
    /// AdvancePitch() を無指示（override一切なし）で回し続けた結果は、Advance() 一括と
    /// バイト一致する（進行粒度が細かくなるだけ＝帯不変。Phase A の Session(null)==ResolveDetailed と同型のDoD）。
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void AdvancePitch_NoManualInput_EqualsAdvance(ulong seed)
    {
        var batch = GameEngine.Play(Team("A", true), Team("H", true), new GameContext { CaptureTimelines = true },
            new Xoshiro256Random(seed));

        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, seed);
        AdvancePitchResult r;
        do { r = prog.AdvancePitch(); } while (r != AdvancePitchResult.Finished);
        var viaPitch = prog.BuildResult();

        Assert.Equal(GameResultDigest.Sha256Of(batch), GameResultDigest.Sha256Of(viaPitch));
    }

    /// <summary>AdvancePitch() は各打席で PitchLog の球数ぶん Pitch を返してから PlateAppearance を返す。</summary>
    [Fact]
    public void AdvancePitch_StopsOnceForEachRecordedPitch_ThenPlateAppearance()
    {
        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, 42UL);

        var checkedPas = 0;
        for (var guard = 0; guard < 20; guard++)
        {
            var pitchStops = 0;
            AdvancePitchResult r;
            do
            {
                r = prog.AdvancePitch();
                if (r == AdvancePitchResult.Pitch) pitchStops++;
            } while (r == AdvancePitchResult.Pitch);

            if (r == AdvancePitchResult.Finished) break;
            Assert.Equal(pitchStops, prog.Current!.PitchSeq.Pitches.Count);
            checkedPas++;
        }
        Assert.True(checkedPas > 0, "打席が1つも確定しなかった");
    }

    /// <summary>
    /// AdvancePitch() で打席途中の Pitch 窓に止まっている間に予約すると、その1球だけに効く
    /// （従来の Advance() ベースでは「打席の最初の1球」にしか届かなかった＝Q12-7 の解決）。
    /// </summary>
    [Fact]
    public void SetPitchBattingOverride_ViaAdvancePitch_TargetsExactPitchMidAtBat()
    {
        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, 42UL);

        var found = false;
        for (var guard = 0; guard < 60 && !found; guard++)
        {
            var pitchStops = 0;
            AdvancePitchResult r;
            do
            {
                r = prog.AdvancePitch();
                if (r == AdvancePitchResult.Pitch)
                {
                    pitchStops++;
                    if (pitchStops == 2)
                    {
                        // 2球目の窓＝PendingPitchIndex==1（0始まり）。相手側が誤って読んでも
                        // この打席の判定には影響しないため、攻撃側を厳密に特定せず両側へ予約する。
                        Assert.Equal(1, prog.PendingPitchIndex);
                        prog.SetPitchBattingOverride(true, PitchBattingOverride.ForceTake);
                        prog.SetPitchBattingOverride(false, PitchBattingOverride.ForceTake);
                        found = true;
                    }
                }
            } while (r == AdvancePitchResult.Pitch);

            if (r == AdvancePitchResult.Finished) break;
            if (found)
            {
                Assert.Equal(AdvancePitchResult.PlateAppearance, r);
                var seq = prog.Current!.PitchSeq.Pitches;
                Assert.True(seq.Count >= 2, "2球目を狙ったのに1球で終わる打席になった");
                Assert.True(seq[1].Kind is PitchKind.Ball or PitchKind.CalledStrike,
                    $"2球目にForceTakeを予約したのに{seq[1].Kind}");
            }
        }
        Assert.True(found, "2球以上ある打席が見つからず、2球目への予約を試せなかった");
    }

    /// <summary>打席途中の1球指示も、保存→復元で再現される（PitchIndex付きGameDecisionの往復）。</summary>
    [Fact]
    public void MidAtBatPitchDirective_ReproducesViaSaveRestore()
    {
        var prog = new MatchProgression(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, 42UL);

        var found = false;
        for (var guard = 0; guard < 60 && !found; guard++)
        {
            var pitchStops = 0;
            AdvancePitchResult r;
            do
            {
                r = prog.AdvancePitch();
                if (r == AdvancePitchResult.Pitch)
                {
                    pitchStops++;
                    if (pitchStops == 2)
                    {
                        prog.SetPitchBattingOverride(true, PitchBattingOverride.ForceTake);
                        prog.SetPitchBattingOverride(false, PitchBattingOverride.ForceTake);
                        found = true;
                    }
                }
            } while (r == AdvancePitchResult.Pitch);
            if (r == AdvancePitchResult.Finished) break;
        }
        Assert.True(found, "2球以上ある打席が見つからなかった（別シードを使う）");

        while (prog.Advance()) { }
        var direct = GameResultDigest.Sha256Of(prog.BuildResult());

        var save = prog.Save();
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.PitchBattingOverride && d.PitchIndex == 1);

        var resumed = GameReplay.Restore(Team("A", true), Team("H", true),
            new GameContext { CaptureTimelines = true }, save);
        while (resumed.Steps.MoveNext()) { }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(direct, restored);
    }

    // ── 自校の投手交代をプレイヤーの采配権にする（GameContext.PlayerControlsPitcherChanges） ──
    // 試合中に投手が勝手に交代される報告への対応。週間球数制限（WeeklyPitchLimit）に達しても
    // engine は機械的に選ばず、MatchProgression が「人選待ち」を表明して進行をブロックする。

    [Fact]
    public void PlayerControlsPitcherChanges_WeeklyLimit_BlocksUntilRelieved()
    {
        var away = Team("A", true);
        var home = Team("H", true);
        // 上限1球＝守備側（表なので home）の先発が1球投げた時点で必ず週間上限に到達する。
        var ctx = new GameContext { WeeklyPitchLimit = 1, PlayerControlsPitcherChanges = true };
        var prog = new MatchProgression(away, home, ctx, 7UL);

        // 最初の打席（表）を1つ確定させる。まだ人選待ちにはならない（打席の途中で投手を差し替えない）。
        AdvancePitchResult r;
        do { r = prog.AdvancePitch(); } while (r == AdvancePitchResult.Pitch);
        Assert.Equal(AdvancePitchResult.PlateAppearance, r);

        // 次の打席へ進もうとすると、週間上限に達した投手がまだ交代されていないため人選待ちで止まる。
        Assert.True(prog.PendingForcedPitcherChange);
        Assert.Equal(AdvancePitchResult.AwaitingForcedPitcherChange, prog.AdvancePitch());
        Assert.False(prog.Advance());   // Advance 系はどれも進めない

        // プレイヤーが継投すれば人選待ちが解け、以降は普通に進む。
        var reliever = home.Reserves.First(p => p.Position == FieldPosition.Pitcher);
        Assert.True(prog.Relieve(teamIsAway: false, reliever));
        Assert.False(prog.PendingForcedPitcherChange);
        Assert.NotEqual(AdvancePitchResult.AwaitingForcedPitcherChange, prog.AdvancePitch());
    }

    /// <summary>
    /// 週の持ち越し球数が<b>観戦経路（MatchProgression）にも届く</b>こと（issue: 球数制限の可視化）。
    /// ここが無いと自動消化（GameEngine.Play）だけ制限が効き、観戦した試合だけ効かない＝
    /// 「観戦しても結果が変わらない」契約が壊れる。持ち越しはチームごとに別辞書で渡す。
    /// </summary>
    [Fact]
    public void PriorWeekPitches_ReachTheLivePath_AndAreScopedPerTeam()
    {
        var away = Team("A", true);
        var home = Team("H", true);
        var homeStarter = home.BattingOrder[home.PitcherSlot];
        var ctx = new GameContext { WeeklyPitchLimit = 500, PlayerControlsPitcherChanges = true };
        var carry = new Dictionary<Player, int> { [homeStarter] = 500 };

        // home 側の辞書へ入れれば、最初の打席を投げ終えた時点で人選待ちになる。
        var prog = new MatchProgression(away, home, ctx, new Xoshiro256Random(7UL),
            homePriorWeekPitches: carry);
        Assert.True(prog.Advance());
        Assert.True(prog.PendingForcedPitcherChange);

        // 同じ辞書を away 側へ入れても home の投手には効かない（チーム分離）。
        var misplaced = new MatchProgression(away, home, ctx, new Xoshiro256Random(7UL),
            awayPriorWeekPitches: carry);
        Assert.True(misplaced.Advance());
        Assert.False(misplaced.PendingForcedPitcherChange);
    }

    [Fact]
    public void PlayerControlsPitcherChanges_Default_KeepsAutomaticReliefAndNeverBlocks()
    {
        // 既定（false＝裏試合・統計シム・既存テスト全部）は従来どおり機械的に交代し、進行はブロックしない。
        var away = Team("A", true);
        var home = Team("H", true);
        var ctx = new GameContext { WeeklyPitchLimit = 1 };
        var prog = new MatchProgression(away, home, ctx, 7UL);
        var guard = 0;
        while (prog.Advance() && guard++ < 20)
        {
            Assert.False(prog.PendingForcedPitcherChange);
        }
    }
}
