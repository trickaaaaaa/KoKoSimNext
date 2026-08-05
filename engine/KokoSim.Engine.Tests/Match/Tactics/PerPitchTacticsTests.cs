using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 采配カードを全部1球単位にする（issue #492）。UI と同じポンプ順序（<see cref="MatchProgression.AdvancePitch"/> の
/// 采配窓で押す）で、押した指示が<b>その打席／その次の1球</b>に効くことを固定する。
///
/// <para>要点は2つ。①窓で押した指示が「いま見ている打席」に届く（打撃姿勢・エンドラン・陣形を毎球評価）、
/// ②無指示なら1ビットも変わらない（determinism-baseline と NullReReservationEachWindow_MatchesUntouchedGame が担保）。
/// 打席頭で凍結していた頃は、窓（＝エンジンが実際に止まる地点）で押した指示は現在の打席に構造上効かなかった。</para>
/// </summary>
public sealed class PerPitchTacticsTests
{
    private static Player Pos(FieldPosition p) => new()
    {
        Name = p.ToString(), Position = p,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

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

    private static MatchProgression NewGame(ulong seed)
        => new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true, CapturePitches = true }, seed);

    // 現在の打席をこの投球列・結果・塁で一意に表す文字列（「いま見ている打席」だけを比較する隔離キー）。
    private static string CurrentAtBatKey(LivePlateAppearance c)
        => $"{c.Result}|{string.Join(",", c.PitchSeq.Pitches.Select(p => p.Kind))}|r{c.RunsScored}" +
           $"|{c.BaseFirstAfter}{c.BaseSecondAfter}{c.BaseThirdAfter}";

    // ── B: エンドランを采配窓で押すと「いま見ている打席」に効く（issue #492 B） ──

    /// <summary>
    /// 打席頭の窓（一塁走者あり）でエンドランを押した試合と押さない試合で、その打席の解決が変わるシードが在る。
    /// 打席頭で凍結していた頃は、窓はエンジンがサイン消費点を通り過ぎた後に開くため、押しても現在の打席に
    /// 効かなかった（17/20 消滅・3/20 は次打席に1打席遅れ）＝この打席だけを見ると一度も変わらなかった。
    /// </summary>
    [Fact]
    public void ManualEndrun_PushedAtPitchWindow_AffectsCurrentAtBat()
    {
        var differs = false;
        for (ulong seed = 1; seed <= 80 && !differs; seed++)
        {
            var plain = EndrunAtFirstRunnerOnFirstWindow(seed, push: false);
            var pushed = EndrunAtFirstRunnerOnFirstWindow(seed, push: true);
            if (plain is null || pushed is null) continue;
            if (plain != pushed) differs = true;
        }
        Assert.True(differs,
            "エンドランを采配窓で押しても、いま見ている打席の解決が一度も変わらない（B: 窓を通っていない）");
    }

    // 一塁走者ありの「打席頭の窓」まで AdvancePitch で進め、push ならエンドランを予約してからその打席を
    // 確定させ、CurrentAtBatKey を返す。到達しなければ null。両呼び出しは同シード＝窓まで完全一致する。
    private static string? EndrunAtFirstRunnerOnFirstWindow(ulong seed, bool push)
    {
        var prog = NewGame(seed);
        for (var guard = 0; guard < 6000; guard++)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) return null;
            if (r != AdvancePitchResult.Pitch) continue;
            var ab = prog.CurrentAtBat;
            if (ab is not { PitchNumberInPa: 1, BaseFirst: true }) continue;

            if (push) prog.SetOffensiveSign(ab.IsTop, OffensiveSign.HitAndRun);
            AdvancePitchResult rr;
            do { rr = prog.AdvancePitch(); } while (rr == AdvancePitchResult.Pitch);
            return rr == AdvancePitchResult.Finished ? null : CurrentAtBatKey(prog.Current!);
        }
        return null;
    }

    // ── 打撃姿勢を打席途中の窓で押すと、その打席の残りの球に効く（issue #492: 毎球評価） ──

    /// <summary>
    /// 打席の2球目の窓で打撃姿勢（長打狙い）を押すと、その打席の解決が変わるシードが在る。打席頭で凍結して
    /// いた頃は、打席途中で姿勢を変えても現在の打席の実効能力は既に固まっていて効かなかった。
    /// </summary>
    [Fact]
    public void BattingApproach_PushedMidAtBat_AffectsCurrentAtBat()
    {
        var differs = false;
        for (ulong seed = 1; seed <= 80 && !differs; seed++)
        {
            var plain = ApproachAtSecondPitchWindow(seed, push: false);
            var pushed = ApproachAtSecondPitchWindow(seed, push: true);
            if (plain is null || pushed is null) continue;
            if (plain != pushed) differs = true;
        }
        Assert.True(differs, "打席途中に打撃姿勢を押しても、その打席の残りの球に一度も効かない（毎球評価できていない）");
    }

    private static string? ApproachAtSecondPitchWindow(ulong seed, bool push)
    {
        var prog = NewGame(seed);
        for (var guard = 0; guard < 6000; guard++)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) return null;
            if (r != AdvancePitchResult.Pitch) continue;
            var ab = prog.CurrentAtBat;
            if (ab is not { PitchNumberInPa: 2 }) continue;   // 2球目の窓＝打席途中

            if (push) prog.SetBattingApproach(ab.IsTop, BattingApproach.Slug);
            AdvancePitchResult rr;
            do { rr = prog.AdvancePitch(); } while (rr == AdvancePitchResult.Pitch);
            var key = rr == AdvancePitchResult.Finished ? null : CurrentAtBatKey(prog.Current!);
            // 姿勢は「解除するまで続く」ので、次の打席以降に漏れないようこの試合はここで捨てる（隔離）。
            return key;
        }
        return null;
    }

    // ── 陣形/配球/ギアを1球の口へ移し、采配窓ごとに再予約して寿命を維持する（issue #492） ──

    /// <summary>
    /// 前進守備を1球の口（<see cref="MatchProgression.SetPitchDefenseOverride"/>）へ毎窓再予約すると、試合結果が
    /// 変わるシードが在る（＝陣形が1球単位でエンジンへ届いている）。幾何にしか触らないので単一シードでは
    /// 恒等になりうる（<see cref="TacticsCardWindowsTests"/> と同じ理由）＝複数シードで一度でも変われば十分。
    /// </summary>
    [Fact]
    public void PerPitchInfieldIn_ReReservedEachWindow_ReachesEngine()
    {
        var differs = false;
        for (ulong seed = 1; seed <= 12 && !differs; seed++)
            differs = DigestWithReReservedInfieldIn(seed) != BaselineDigest(seed);
        Assert.True(differs, "前進守備を1球の口へ毎窓再予約しても結果が一度も変わらない（陣形が1球単位で届いていない）");
    }

    private static string BaselineDigest(ulong seed)
    {
        var prog = NewGame(seed);
        while (prog.AdvancePitch() != AdvancePitchResult.Finished) { }
        return GameResultDigest.Sha256Of(prog.BuildResult());
    }

    private static string DigestWithReReservedInfieldIn(ulong seed)
    {
        var prog = NewGame(seed);
        for (var guard = 0; guard < 20000; guard++)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;
            if (r != AdvancePitchResult.Pitch) continue;
            // 采配窓（これから投げる1球）で、守備側へ前進守備を再予約する（UI の寿命維持と同じ流儀）。
            var ab = prog.CurrentAtBat;
            if (ab is not null)
                prog.SetPitchDefenseOverride(ab.IsTop, policy: null, gear: null, infield: DefenseDepth.In, outfield: null);
        }
        return GameResultDigest.Sha256Of(prog.BuildResult());
    }

    // ── 決定論・帯: 無指示なら AdvancePitch 一括は Advance 一括＝バッチ Play と完全一致（既存の別テストの補完）──

    /// <summary>
    /// 1球の口を毎窓 null で再予約しても（＝指示解除の連打）、無指示の試合とバイト一致する
    /// （既定恒等・RNG非消費＝不変条件#2）。
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void NullReReservationEachWindow_MatchesUntouchedGame(ulong seed)
    {
        var baseline = BaselineDigest(seed);

        var prog = NewGame(seed);
        for (var guard = 0; guard < 20000; guard++)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;
            if (r != AdvancePitchResult.Pitch) continue;
            var ab = prog.CurrentAtBat;
            if (ab is null) continue;
            prog.SetPitchDefenseOverride(ab.IsTop, null, null, null, null);
            prog.SetBattingApproach(ab.IsTop, BattingApproach.Normal);
            prog.SetOffensiveSign(ab.IsTop, null);
        }
        Assert.Equal(baseline, GameResultDigest.Sha256Of(prog.BuildResult()));
    }

    // ── 中断保存→復元で新しい1球粒度も再現する（NewWindows_ReproduceViaSaveRestore と同型・陣形の1球口版） ──

    /// <summary>
    /// 打席途中の窓で陣形/配球/ギアを1球の口へ予約した試合が、保存→復元でバイト一致する
    /// （PitchIndex 付き <see cref="GameDecision"/> の往復・issue #492）。
    /// </summary>
    [Fact]
    public void PerPitchDefenseOverride_ReproducesViaSaveRestore()
    {
        var prog = NewGame(42UL);
        var pushed = false;
        for (var guard = 0; guard < 6000 && !pushed; guard++)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;
            if (r != AdvancePitchResult.Pitch) continue;
            var ab = prog.CurrentAtBat;
            if (ab is { PitchNumberInPa: 2 })
            {
                prog.SetPitchDefenseOverride(ab.IsTop, PitchPolicy.ControlFirst, PitcherGear.Push,
                    DefenseDepth.In, DefenseDepth.Deep);
                pushed = true;
            }
        }
        Assert.True(pushed, "2球目の窓に到達しなかった（別シードを使う）");
        while (prog.AdvancePitch() != AdvancePitchResult.Finished) { }
        var direct = GameResultDigest.Sha256Of(prog.BuildResult());

        var save = prog.Save();
        var resumed = GameReplay.Restore(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true, CapturePitches = true }, save);
        while (resumed.Steps.MoveNext()) { }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(direct, restored);
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.PitchDefenseOverride
            && d.Infield == DefenseDepth.In && d.Outfield == DefenseDepth.Deep);
    }
}
