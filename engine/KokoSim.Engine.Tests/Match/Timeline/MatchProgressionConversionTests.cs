using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;   // GameResultDigest（テスト共通のダイジェスト）
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 試合中の守備位置操作 seam（issue #235・設計書09 §2/§6）。守備位置の入替（コンバート）と
/// コンバート継投が <see cref="MatchProgression"/> から操作でき、決定列（<see cref="GameDecision"/>）へ載って
/// 「同シード＋同操作 → 同結果」「Save/Load 往復で同結果」であること（不変条件#2 決定論）を保証する。
/// </summary>
public sealed class MatchProgressionConversionTests
{
    private static Player P(FieldPosition pos, string name) => new()
    {
        Position = pos, Name = name,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Rp(string name) => P(FieldPosition.Pitcher, name) with { Pitching = PitcherAttributes.LeagueAverage };

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
        Bullpen = new[] { Rp(n + "二番手"), Rp(n + "三番手") },
        Bench = new[] { P(FieldPosition.FirstBase, n + "控1"), P(FieldPosition.LeftField, n + "控2") },
    };

    private static MatchProgression NewProg(ulong seed)
        => new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed);

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
    public void SubstitutionOptions_ExposesSwapAndConvertWhileFielding()
    {
        var prog = NewProg(11UL);
        prog.Advance();

        var away = prog.SubstitutionOptions(teamIsAway: true);    // 攻撃側
        var home = prog.SubstitutionOptions(teamIsAway: false);   // 守備側

        Assert.False(away.CanSwapDefense);
        Assert.Equal("攻撃中は守備位置を動かせない。", away.BlockedReasonFor(SubstitutionKind.SwapDefense));
        Assert.True(home.CanSwapDefense);
        Assert.True(home.CanConvertRelief);
        // 入替できるのは守備に就いている8枠（非DHでは投手スロットを除く）。
        Assert.Equal(8, home.SwappableSlots.Count);
        Assert.DoesNotContain(home.PitcherSlot, home.SwappableSlots);
        Assert.DoesNotContain(FieldPosition.Pitcher, home.ConvertTargets());
    }

    [Fact]
    public void SwapDefensivePositions_ExhaustedBench_IsStillAllowed()
    {
        // 交代回数を消費しない操作なので、控えが尽きていても守備位置は動かせる（issue #235 方針①）。
        var prog = new MatchProgression(
            Team("A"), Team("H") with { Bench = System.Array.Empty<Player>(), Bullpen = System.Array.Empty<Player>() },
            new GameContext(), 5UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);

        Assert.False(opt!.CanDefensiveSub);
        Assert.True(opt.CanSwapDefense);
        Assert.True(prog.SwapDefensivePositions(false, 0, 4));
    }

    // ===== 2. 操作の単体 =====

    [Fact]
    public void SwapDefensivePositions_MovesPositionsOnly()
    {
        var prog = NewProg(7UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var before = opt!.Lineup.Select(p => p.Name).ToList();

        Assert.True(prog.SwapDefensivePositions(false, 0, 4));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal(before, after.Lineup.Select(p => p.Name).ToList());   // 打順の顔ぶれは不変
        Assert.Equal(FieldPosition.Shortstop, after.Lineup[0].Position);
        Assert.Equal(FieldPosition.Catcher, after.Lineup[4].Position);
    }

    [Fact]
    public void ConvertPitcherAndRelieve_KeepsStarterInTheGameAsFielder()
    {
        var prog = NewProg(7UL);
        var opt = AdvanceUntilDefense(prog, teamIsAway: false);
        Assert.NotNull(opt);
        var starter = opt!.CurrentPitcher.Name;

        Assert.True(prog.ConvertPitcherAndRelieve(false, FieldPosition.LeftField, opt.Bullpen[0]));

        var after = prog.SubstitutionOptions(teamIsAway: false);
        Assert.Equal("H二番手", after.CurrentPitcher.Name);
        var converted = after.Lineup.First(p => p.Name == starter);
        Assert.Equal(FieldPosition.LeftField, converted.Position);   // 元投手は残って左翼を守る
        Assert.DoesNotContain(after.Lineup, p => p.Name == "H左");    // 押し出された左翼手は退場
    }

    // ===== 3. 決定論（同シード＋同操作／Save-Load 往復） =====

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void SameSeedAndSameConversions_ProduceTheSameResult(ulong seed)
    {
        string Run()
        {
            var prog = NewProg(seed);
            var applied = 0;
            while (prog.ConfirmedPlateAppearances < 24 && prog.Advance())
            {
                var def = prog.SubstitutionOptions(teamIsAway: false);
                if (def.IsOffense) continue;
                if (applied == 0 && prog.SwapDefensivePositions(false, 0, 4)) applied++;
                else if (applied == 1 && def.Bullpen.Count > 0
                         && prog.ConvertPitcherAndRelieve(false, FieldPosition.CenterField, def.Bullpen[0])) applied++;
            }
            Assert.Equal(2, applied);
            return GameResultDigest.Sha256Of(prog.FinishRemaining());
        }

        Assert.Equal(Run(), Run());
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void SaveLoadRoundTrip_WithConversions_ProducesTheSameResult(ulong seed)
    {
        var prog = NewProg(seed);
        var applied = 0;
        while (prog.ConfirmedPlateAppearances < 24 && prog.Advance())
        {
            var def = prog.SubstitutionOptions(teamIsAway: false);
            if (def.IsOffense) continue;
            if (applied == 0 && prog.SwapDefensivePositions(false, 0, 4)) applied++;
            else if (applied == 1 && def.Bullpen.Count > 0
                     && prog.ConvertPitcherAndRelieve(false, FieldPosition.CenterField, def.Bullpen[0])) applied++;
        }
        Assert.Equal(2, applied);

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
    public void SwapDefensivePositions_DoesNotConsumeRandomness()
    {
        // 守備位置の入替そのものは乱数を引かない。能力値が全員同じチームで入れ替えれば、
        // 守備解決への入力（守備位置→能力の対応）が変わらないので試合展開は1球も動かない。
        // ※ 記録（ボックススコアの守備位置表記）は変わるので digest ではなく得点経過で見る。
        var baseline = GameEngine.Play(Team("A"), Team("H"), new GameContext { CaptureTimelines = true },
            new Xoshiro256Random(99UL));

        var prog = NewProg(99UL);
        var swapped = false;
        while (prog.Advance())
        {
            if (swapped) continue;
            var def = prog.SubstitutionOptions(teamIsAway: false);
            if (!def.IsOffense) swapped = prog.SwapDefensivePositions(false, 0, 4);
        }
        Assert.True(swapped, "シナリオが守備位置の入替を実際に行使していない（テストの前提が崩れている）");
        var after = prog.BuildResult();

        Assert.Equal(baseline.AwayRuns, after.AwayRuns);
        Assert.Equal(baseline.HomeRuns, after.HomeRuns);
        Assert.Equal(baseline.AwayLineScore, after.AwayLineScore);
        Assert.Equal(baseline.HomeLineScore, after.HomeLineScore);
    }
}
