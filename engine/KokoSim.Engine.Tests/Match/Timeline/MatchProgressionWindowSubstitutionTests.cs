using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 1球進行（<see cref="MatchProgression.AdvancePitch"/>）の采配窓で行う選手交代（2026-08-15）。
///
/// <para>旧実装は打席の頭の窓を「投手・打者を束縛した後」に開いていたため、その窓で行った継投・代打は
/// 次の打席から効いた（監督報告「投手交代したけど、一人目の打者の間、前の投手が投げている」）。
/// いまは打席の頭の窓を束縛の前に開き、打席途中の窓では束縛し直す（<c>RebindMatchup</c>）＝
/// 交代は「押した次の球」から効く。GameReplay も同じ位置で適用するので Save/Load 往復とも一致する。</para>
/// </summary>
public sealed class MatchProgressionWindowSubstitutionTests
{
    private static Player P(FieldPosition pos, string name) => new()
    {
        Position = pos, Name = name,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Rp(string name, Handedness throws) =>
        P(FieldPosition.Pitcher, name) with { Pitching = PitcherAttributes.LeagueAverage, Throws = throws };

    private static Team Team(string n) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕"), P(FieldPosition.FirstBase, n + "一"),
            P(FieldPosition.SecondBase, n + "二"), P(FieldPosition.ThirdBase, n + "三"),
            P(FieldPosition.Shortstop, n + "遊"), P(FieldPosition.LeftField, n + "左"),
            P(FieldPosition.CenterField, n + "中"), P(FieldPosition.RightField, n + "右"),
            Rp(n + "先発P", Handedness.Right),
        },
        PitcherSlot = 8,
        Reserves = TeamReserves.Merge(
            new[] { Rp(n + "二番手", Handedness.Left), Rp(n + "三番手", Handedness.Right) },
            new[] { P(FieldPosition.FirstBase, n + "控1"), P(FieldPosition.SecondBase, n + "控2") }),
    };

    private static GameContext Ctx() => new() { CaptureTimelines = true, CapturePitches = true };

    private static MatchProgression NewProg(ulong seed) => new(Team("A"), Team("H"), Ctx(), seed);

    /// <summary>表（先攻Aの攻撃・Hの守備）の打席の頭の窓まで進める（<paramref name="skip"/> 打席ぶん飛ばす）。</summary>
    private static LiveAtBat AdvanceToTopHeadWindow(MatchProgression prog, int skip = 1)
    {
        var heads = 0;
        while (true)
        {
            var r = prog.AdvancePitch();
            Assert.NotEqual(AdvancePitchResult.Finished, r);
            if (r == AdvancePitchResult.Pitch && prog.CurrentAtBat is { PitchNumberInPa: 1, IsTop: true } && heads++ >= skip)
                return prog.CurrentAtBat;
        }
    }

    /// <summary>表の打席で「打席途中の窓」（2球目以降）まで進める。</summary>
    private static LiveAtBat AdvanceToTopMidWindow(MatchProgression prog)
    {
        while (true)
        {
            var r = prog.AdvancePitch();
            Assert.NotEqual(AdvancePitchResult.Finished, r);
            if (r == AdvancePitchResult.Pitch && prog.CurrentAtBat is { IsTop: true } at && at.PitchNumberInPa >= 2)
                return at;
        }
    }

    /// <summary>いまの打席を確定まで進め、途中の窓の LiveAtBat を集めて返す。</summary>
    private static List<LiveAtBat> FinishPlateAppearance(MatchProgression prog)
    {
        var windows = new List<LiveAtBat>();
        AdvancePitchResult r;
        while ((r = prog.AdvancePitch()) == AdvancePitchResult.Pitch) windows.Add(prog.CurrentAtBat!);
        Assert.Equal(AdvancePitchResult.PlateAppearance, r);
        return windows;
    }

    [Fact]
    public void ChangePitcher_AtHeadWindow_PitchesThatPlateAppearance()
    {
        var prog = NewProg(7UL);
        var head = AdvanceToTopHeadWindow(prog);
        Assert.Equal("H先発P", head.PitcherName);

        var opt = prog.SubstitutionOptions(teamIsAway: false);
        var reliever = opt.Reserves.First(x => x.Name == "H二番手");
        Assert.True(prog.ChangePitcher(teamIsAway: false, reliever));

        // 交代の直後から窓の対戦が新投手（UIはこれで HUD・利き腕を引き直す）
        Assert.Equal("H二番手", prog.CurrentAtBat!.PitcherName);
        Assert.Equal(Handedness.Left, prog.CurrentAtBat.PitcherThrows);
        Assert.Equal(1, prog.CurrentAtBat.PitchNumberInPa);

        var windows = FinishPlateAppearance(prog);
        Assert.All(windows, w => Assert.Equal("H二番手", w.PitcherName));
        Assert.Equal("H二番手", prog.Current!.PitcherName);   // この打席は新投手が投げた
        Assert.Equal(Handedness.Left, prog.Current.PitcherThrows);

        var result = prog.FinishRemaining();
        var starter = result.HomePitching.First(l => l.Name == "H先発P");
        Assert.Equal(1, starter.BattersFaced);   // 先発は交代前の1打席だけ
    }

    [Fact]
    public void PinchHit_AtHeadWindow_BatsThatPlateAppearance()
    {
        var prog = NewProg(7UL);
        var head = AdvanceToTopHeadWindow(prog);
        var opt = prog.SubstitutionOptions(teamIsAway: true);
        var ph = opt.Reserves.First(x => x.Name == "A控1");
        var order = head.BatterOrder;
        Assert.True(prog.PinchHit(teamIsAway: true, ph));

        Assert.Equal("A控1", prog.CurrentAtBat!.BatterName);
        Assert.Equal(order, prog.CurrentAtBat.BatterOrder);

        FinishPlateAppearance(prog);
        Assert.Equal("A控1", prog.Current!.BatterName);   // 代打がこの打席に立った
        Assert.Equal(order, prog.Current.BatterOrder);
    }

    [Fact]
    public void ChangePitcher_MidPlateAppearance_TakesOverKeepingTheCount()
    {
        var prog = NewProg(7UL);
        var mid = AdvanceToTopMidWindow(prog);
        Assert.Equal("H先発P", mid.PitcherName);

        var opt = prog.SubstitutionOptions(teamIsAway: false);
        var reliever = opt.Reserves.First(x => x.Name == "H二番手");
        Assert.True(prog.ChangePitcher(teamIsAway: false, reliever));

        // カウント・球数はそのまま、投手だけ差し替わる
        var after = prog.CurrentAtBat!;
        Assert.Equal("H二番手", after.PitcherName);
        Assert.Equal(mid.Balls, after.Balls);
        Assert.Equal(mid.Strikes, after.Strikes);
        Assert.Equal(mid.PitchNumberInPa, after.PitchNumberInPa);
        Assert.Equal(mid.BatterName, after.BatterName);

        var windows = FinishPlateAppearance(prog);
        Assert.All(windows, w => Assert.Equal("H二番手", w.PitcherName));
        Assert.Equal("H二番手", prog.Current!.PitcherName);
        // 打席途中の窓は「投げ終えた球の続き」＝球数の連番が途切れない
        if (windows.Count > 0) Assert.Equal(mid.PitchNumberInPa + 1, windows[0].PitchNumberInPa);
    }

    [Theory]
    [InlineData(7UL)]
    [InlineData(31UL)]
    public void SaveLoadRoundTrip_WithWindowSubstitutions_ProducesTheSameResult(ulong seed)
    {
        // 打席の頭の窓で継投、打席途中の窓で代打（別の打席）→ その後の打席の途中で保存。
        var prog = NewProg(seed);
        AdvanceToTopHeadWindow(prog);
        var def = prog.SubstitutionOptions(teamIsAway: false);
        Assert.True(prog.ChangePitcher(false, def.Reserves.First(x => x.Name == "H二番手")));
        FinishPlateAppearance(prog);
        AdvanceToTopMidWindow(prog);
        var off = prog.SubstitutionOptions(teamIsAway: true);
        Assert.True(prog.PinchHit(true, off.Reserves.First(x => x.Name == "A控1")));
        // さらに数窓進めてから保存（打席途中の保存＝ConfirmedPitchesInCurrentPa の経路も通す）
        for (var i = 0; i < 5; i++) prog.AdvancePitch();

        var save = prog.Save();
        var straight = GameResultDigest.Sha256Of(prog.FinishRemaining());

        var json = JsonSerializer.Serialize(save);
        var back = JsonSerializer.Deserialize<GameSaveState>(json)!;
        var resumed = GameReplay.Restore(Team("A"), Team("H"), Ctx(), back);
        while (resumed.Steps.MoveNext()) { /* 続行 */ }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(straight, restored);
    }

    [Fact]
    public void HeadWindow_IsOpenedBeforeBinding_SoTheAtBatSeesTheSubstitute_EvenInReplay()
    {
        // 決定列の PitchIndex: 打席の頭の窓の交代は 0、途中の窓の交代はその球数で記録される。
        var prog = NewProg(7UL);
        AdvanceToTopHeadWindow(prog);
        var def = prog.SubstitutionOptions(teamIsAway: false);
        Assert.True(prog.ChangePitcher(false, def.Reserves.First(x => x.Name == "H二番手")));
        FinishPlateAppearance(prog);
        var mid = AdvanceToTopMidWindow(prog);
        var off = prog.SubstitutionOptions(teamIsAway: true);
        Assert.True(prog.PinchHit(true, off.Reserves.First(x => x.Name == "A控1")));

        var save = prog.Save();
        var subs = save.Decisions.Where(d => d.Kind is GameDecisionKind.ChangePitcher or GameDecisionKind.PinchHit).ToList();
        Assert.Equal(2, subs.Count);
        Assert.Equal(0, subs[0].PitchIndex);
        Assert.Equal(mid.PitchNumberInPa - 1, subs[1].PitchIndex);
    }
}
