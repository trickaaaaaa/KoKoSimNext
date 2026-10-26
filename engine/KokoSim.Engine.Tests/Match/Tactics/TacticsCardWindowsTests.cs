using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 采配カードの拡充（2026-07-31）で増やしたプレイヤー窓口の受け入れ。
///
/// <para>要点は2つ。①指示がエンジンへ実際に届く（結果が変わる） ②<b>指示していない試合は1ビットも変わらない</b>
/// （既定は恒等・RNGを1発も引かない＝統計帯と digest は不変。不変条件#2/#5）。②は determinism-baseline が
/// 無変更で緑であることと合わせて担保する。</para>
/// </summary>
public sealed class TacticsCardWindowsTests
{
    // ── 打撃姿勢の純関数（設計書01 §1: 効果は実効能力とスイング頻度にしか落ちない）──

    [Fact]
    public void BattingApproach_Normal_IsIdentity()
    {
        var c = new TacticsCoefficients();
        var b = BatterAttributes.LeagueAverage;
        Assert.Equal(b, BattingApproachModel.Apply(b, BattingApproach.Normal, c));
        Assert.Equal(1.0, BattingApproachModel.SwingFactor(BattingApproach.Normal, c));
    }

    [Fact]
    public void BattingApproach_MovesTheIntendedAxes()
    {
        var c = new TacticsCoefficients();
        var b = BatterAttributes.LeagueAverage;

        var ground = BattingApproachModel.Apply(b, BattingApproach.GroundBall, c);
        Assert.True(ground.LaunchTendency < b.LaunchTendency, "転がせで弾道が下がっていない");
        Assert.True(ground.Contact > b.Contact, "転がせでミートが上がっていない");
        Assert.True(ground.Power < b.Power, "転がせでパワーが下がっていない");

        var slug = BattingApproachModel.Apply(b, BattingApproach.Slug, c);
        Assert.True(slug.Power > b.Power, "長打狙いでパワーが上がっていない");
        Assert.True(slug.LaunchTendency > b.LaunchTendency, "長打狙いで弾道が上がっていない");
        Assert.True(slug.Contact < b.Contact, "長打狙いでミートが下がっていない");

        var put = BattingApproachModel.Apply(b, BattingApproach.PutInPlay, c);
        Assert.True(put.Contact > b.Contact, "当てに行けでミートが上がっていない");
        Assert.True(put.Power < b.Power, "当てに行けでパワーが下がっていない");
        Assert.Equal(b.LaunchTendency, put.LaunchTendency);   // 弾道には触らない軸

        // 待球策は能力を動かさず、スイング頻度だけを落とす。
        Assert.Equal(b, BattingApproachModel.Apply(b, BattingApproach.WorkCount, c));
        Assert.True(BattingApproachModel.SwingFactor(BattingApproach.WorkCount, c) < 1.0);
    }

    /// <summary>待球策の倍率はスイング確率へそのまま掛かる（確率を作り直さない＝二層構造の一貫性）。</summary>
    [Fact]
    public void SwingProbability_ScalesByFactor()
    {
        var coeff = new BattingCoefficients();
        var b = BatterAttributes.LeagueAverage;
        var full = BatterDecision.SwingProbability(true, 0.0, 0.10, b, coeff);
        var damped = BatterDecision.SwingProbability(true, 0.0, 0.10, b, coeff, 0.5);
        Assert.Equal(full * 0.5, damped, 9);
    }

    // ── 窓口がエンジンへ届く（＝試合の結果が変わる）──

    [Theory]
    [InlineData(BattingApproach.WorkCount)]
    [InlineData(BattingApproach.GroundBall)]
    [InlineData(BattingApproach.Slug)]
    [InlineData(BattingApproach.PutInPlay)]
    public void SetBattingApproach_ChangesTheGame(BattingApproach approach)
    {
        Assert.NotEqual(BaselineSha(), ShaWith(prog => prog.SetBattingApproach(true, approach)));
    }

    /// <summary>解除（Normal）は「一度も呼んでいない試合」とバイト一致する＝既定が恒等であることの証明。</summary>
    [Fact]
    public void SetBattingApproach_Normal_MatchesUntouchedGame()
    {
        Assert.Equal(BaselineSha(), ShaWith(prog => prog.SetBattingApproach(true, BattingApproach.Normal)));
    }

    [Fact]
    public void SetStandingDefense_ChangesTheGame()
    {
        // 配球・ギア・内野/外野の深さ。どれも守備側（後攻）へ載る。
        // 配球は ControlFirst（散布σ）で見る。直球中心/変化球中心はストレート比率の増減なので、
        // レパートリーがストレートだけの投手（テスト用の LeagueAverage）では行き先が無く恒等になる
        // ＝エンジンの正しい振る舞いで、窓口が届いているかの判定には使えない。
        Assert.NotEqual(BaselineSha(), ShaWith(prog =>
            prog.SetStandingDefense(false, PitchPolicy.ControlFirst, null, null, null)));
        Assert.NotEqual(BaselineSha(), ShaWith(prog =>
            prog.SetStandingDefense(false, null, KokoSim.Engine.Match.Pitching.PitcherGear.Push, null, null)));
        // 陣形（守備位置をずらす）は**単一シードで判定しない**。指示は幾何にしか触らないので、
        // その試合にたまたま境目のプレーが1つも無ければ結果は同一になる（実測: 内野前進は 40シード中
        // 34〜35 で結果が変わる＝配線は生きているのに、特定の1シードだけ恒等になりうる）。
        AssertChangesSomeGame("内野前進", prog => prog.SetStandingDefense(false, null, null, DefenseDepth.In, null));
        AssertChangesSomeGame("外野深め", prog => prog.SetStandingDefense(false, null, null, null, DefenseDepth.Deep));
    }

    /// <summary>複数シードのうち1つでも結果が変われば「指示がエンジンへ届いている」。</summary>
    private static void AssertChangesSomeGame(string what, System.Action<MatchProgression> setup)
    {
        foreach (var seed in new ulong[] { Seed, 1, 2, 3, 4 })
        {
            if (ShaWith(setup, seed) != ShaWith(null, seed)) return;
        }
        Assert.Fail($"{what} が5シードのどれでも結果を変えない＝窓口がエンジンへ届いていない");
    }

    /// <summary>全項目 null＝「指示なし」なので、一度も呼んでいない試合と一致する。</summary>
    [Fact]
    public void SetStandingDefense_AllNull_MatchesUntouchedGame()
    {
        Assert.Equal(BaselineSha(), ShaWith(prog => prog.SetStandingDefense(false, null, null, null, null)));
    }

    /// <summary>エンドランは brain の判断より優先して次の打席に載る（一塁に走者がいて初めて効く）。</summary>
    [Fact]
    public void SetOffensiveSign_ChangesTheGame()
    {
        // 特定シードでは強攻策が結果を変えないこともある（RNG列に依存）。複数シードで一度でも変われば十分。
        var differs = false;
        for (ulong s = 1; s <= 40 && !differs; s++)
            differs = ShaFromRunnerOnFirst(null, s)
                != ShaFromRunnerOnFirst((prog, isTop) => prog.SetOffensiveSign(isTop, OffensiveSign.HitAndRun), s);
        Assert.True(differs, "強攻策(エンドラン)を出しても試合結果が一切変わらない");
    }

    /// <summary>盗塁指示は次の1球で試みられる（走者がいなければ在塁判定で自然に不発＝指示自体は無害）。</summary>
    [Fact]
    public void SetStealAttempt_ChangesTheGame()
    {
        // 特定シードでは盗塁の企図が結果を変えないこともある（RNG列に依存）。複数シードで一度でも変われば十分。
        var differs = false;
        for (ulong s = 1; s <= 40 && !differs; s++)
            differs = ShaFromRunnerOnFirst(null, s)
                != ShaFromRunnerOnFirst((prog, isTop) => prog.SetStealAttempt(isTop, StartType.Gamble), s);
        Assert.True(differs, "盗塁を指示しても試合結果が一切変わらない");
    }

    /// <summary>一塁に走者が出た打席まで進め、そこで指示してから最後まで流した試合の digest。</summary>
    private static string ShaFromRunnerOnFirst(System.Action<MatchProgression, bool>? setup, ulong seed = Seed)
    {
        var prog = NewGame(seed);
        for (var i = 0; i < 200 && prog.Advance(); i++)
        {
            if (prog.Current!.BaseFirstAfter) break;
        }
        Assert.True(prog.Current!.BaseFirstAfter, "一塁に走者が出る場面へ到達しなかった");
        setup?.Invoke(prog, prog.Current.IsTop);
        while (prog.Advance()) { }
        return GameResultDigest.Sha256Of(prog.BuildResult());
    }

    // ── 中断保存→復元で再現する（PitchBattingOverrideDecision_ReproducesViaSaveRestore と同型）──

    [Fact]
    public void NewWindows_ReproduceViaSaveRestore()
    {
        var prog = NewGame();
        while (prog.Advance() && prog.Current!.Inning < 3) { }
        var offenseIsAway = prog.Current!.IsTop;
        prog.SetBattingApproach(offenseIsAway, BattingApproach.Slug);
        prog.SetStandingDefense(!offenseIsAway, PitchPolicy.BreakingHeavy,
            KokoSim.Engine.Match.Pitching.PitcherGear.Push, DefenseDepth.In, DefenseDepth.Deep);
        prog.SetOffensiveSign(offenseIsAway, OffensiveSign.HitAndRun);
        prog.SetStealAttempt(offenseIsAway, StartType.Gamble);
        while (prog.Advance()) { }
        var direct = GameResultDigest.Sha256Of(prog.BuildResult());

        var save = prog.Save();
        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), save);
        while (resumed.Steps.MoveNext()) { }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(direct, restored);
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.BattingApproach);
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.StandingDefense);
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.OffensiveSign);
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.StealAttempt);
    }

    /// <summary>
    /// 待球策を通した打者はスイングの割合が下がる（＝球数を投げさせる姿勢が絵空事でない）。
    /// 1試合ぶんでは揺れるので、複数シードの合計で見る。
    /// </summary>
    [Fact]
    public void WorkCount_LowersSwingRate()
    {
        var (plainSwings, plainPitches) = SwingShare(approach: null);
        var (workSwings, workPitches) = SwingShare(approach: BattingApproach.WorkCount);

        var plainRate = (double)plainSwings / plainPitches;
        var workRate = (double)workSwings / workPitches;
        Assert.True(workRate < plainRate,
            $"待球策でスイング率が下がっていない（通常{plainRate:P1} → 待球策{workRate:P1}）");
    }

    private static (int Swings, int Pitches) SwingShare(BattingApproach? approach)
    {
        var swings = 0;
        var pitches = 0;
        foreach (var seed in new ulong[] { 1UL, 7UL, 42UL, 99UL, 2026UL, 314UL })
        {
            var prog = NewGame(seed);
            if (approach is { } a) prog.SetBattingApproach(true, a);   // 先攻の攻撃だけに載せる
            while (prog.Advance())
            {
                if (!prog.Current!.IsTop) continue;   // 姿勢を載せた側の打席だけ数える
                foreach (var p in prog.Current.PitchSeq.Pitches)
                {
                    pitches++;
                    if (p.Kind is PitchKind.SwingingStrike or PitchKind.Foul or PitchKind.InPlay) swings++;
                }
            }
        }
        return (swings, pitches);
    }

    // ── ヘルパ ──

    private const ulong Seed = 42UL;

    private static MatchProgression NewGame(ulong seed = Seed)
        => new(Team("A"), Team("H"), new GameContext(), seed);

    private static string BaselineSha() => ShaWith(null);

    private static string ShaWith(System.Action<MatchProgression>? setup, ulong seed = Seed)
    {
        var prog = NewGame(seed);
        // 打席が始まる前に指示しておく（「試合開始時から解除するまで続く」の最短形）。
        setup?.Invoke(prog);
        while (prog.Advance()) { }
        return GameResultDigest.Sha256Of(prog.BuildResult());
    }

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
