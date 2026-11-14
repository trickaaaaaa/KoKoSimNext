using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Broadcast;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Broadcast;

/// <summary>
/// 選手交代のログ（issue #920 交代テロップ）。<see cref="GameProgress.Substitutions"/> が、交代の経路
/// （采配窓の操作・AI・取り消しの再生）を問わず、起きた交代を1件ずつ・種類つきで積むことを固定する。
/// </summary>
public sealed class SubstitutionLogTests
{
    private static Player P(FieldPosition pos, string name, int number = 0) => new()
    {
        Position = pos, Name = name, UniformNumber = number,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Rp(string name, int number = 0)
        => P(FieldPosition.Pitcher, name, number) with { Pitching = PitcherAttributes.LeagueAverage };

    private static Team Team(string n) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕", 2), P(FieldPosition.FirstBase, n + "一", 3),
            P(FieldPosition.SecondBase, n + "二", 4), P(FieldPosition.ThirdBase, n + "三", 5),
            P(FieldPosition.Shortstop, n + "遊", 6), P(FieldPosition.LeftField, n + "左", 7),
            P(FieldPosition.CenterField, n + "中", 8), P(FieldPosition.RightField, n + "右", 9),
            Rp(n + "先発P", 1),
        },
        PitcherSlot = 8,
        Reserves = TeamReserves.Merge(
            new[] { Rp(n + "二番手", 10), Rp(n + "三番手", 11) },
            new[] { P(FieldPosition.FirstBase, n + "控1", 13), P(FieldPosition.SecondBase, n + "控2", 14) }),
    };

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
        Reserves = TeamReserves.Merge(new[] { Rp(n + "二番手") }, new[] { P(FieldPosition.FirstBase, n + "控1") }),
    };

    private static GameContext Ctx() => new() { CaptureTimelines = true, CapturePitches = true };

    /// <summary>表の打席の頭の窓まで進める。</summary>
    private static LiveAtBat ToTopHead(MatchProgression prog)
    {
        while (true)
        {
            var r = prog.AdvancePitch();
            Assert.NotEqual(AdvancePitchResult.Finished, r);
            if (r == AdvancePitchResult.Pitch && prog.CurrentAtBat is { PitchNumberInPa: 1, IsTop: true } at) return at;
        }
    }

    [Fact]
    public void PinchHit_IsLogged_WithOrderAndTheBatterWhoLeft()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), Ctx(), 7UL);
        var head = ToTopHead(prog);
        Assert.Empty(prog.Substitutions);

        var sub = prog.SubstitutionOptions(teamIsAway: true).Reserves.First(x => x.Name == "A控1");
        Assert.True(prog.PinchHit(teamIsAway: true, sub));

        var e = Assert.Single(prog.Substitutions);
        Assert.True(e.TeamIsAway);
        Assert.True(e.IsTopHalf);
        Assert.Equal(1, e.Inning);
        var entry = Assert.Single(e.Entries);
        Assert.Equal(SubstitutionEntryKind.PinchHit, entry.Kind);
        Assert.Equal(head.BatterOrder, entry.Order);
        Assert.Equal("A控1", entry.In.Name);
        Assert.Equal(13, entry.In.Number);
        Assert.Equal(head.BatterName, entry.Out!.Name);
    }

    [Fact]
    public void ChangePitcher_IsLoggedAsRelief_InThePitchersSlot()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), Ctx(), 7UL);
        ToTopHead(prog);
        var reliever = prog.SubstitutionOptions(teamIsAway: false).Reserves.First(x => x.Name == "H二番手");
        Assert.True(prog.ChangePitcher(teamIsAway: false, reliever));

        var e = Assert.Single(prog.Substitutions);
        Assert.False(e.TeamIsAway);
        var entry = Assert.Single(e.Entries);
        Assert.Equal(SubstitutionEntryKind.Relief, entry.Kind);
        Assert.Equal(9, entry.Order);
        Assert.Equal(FieldPosition.Pitcher, entry.Position);
        Assert.Equal("H二番手", entry.In.Name);
        Assert.Equal("H先発P", entry.Out!.Name);
    }

    [Fact]
    public void DefensiveSub_AndPositionSwap_AreLogged()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), Ctx(), 7UL);
        ToTopHead(prog);
        var opt = prog.SubstitutionOptions(teamIsAway: false);
        var outgoing = opt.Lineup.First(x => x.Name == "H一");
        var sub = opt.Reserves.First(x => x.Name == "H控2");
        Assert.True(prog.DefensiveSub(teamIsAway: false, outgoing, sub));
        Assert.True(prog.SwapDefensivePositions(teamIsAway: false, 1, 2));   // 一塁 ⇔ 二塁

        Assert.Equal(2, prog.Substitutions.Count);
        var def = Assert.Single(prog.Substitutions[0].Entries);
        Assert.Equal(SubstitutionEntryKind.DefensiveSub, def.Kind);
        Assert.Equal(FieldPosition.FirstBase, def.Position);
        Assert.Equal("H一", def.Out!.Name);

        var swap = prog.Substitutions[1].Entries;
        Assert.Equal(2, swap.Count);
        Assert.All(swap, x => Assert.Equal(SubstitutionEntryKind.PositionChange, x.Kind));
        Assert.All(swap, x => Assert.Null(x.Out));

        // 同じ間合いの2件は1枚にまとまり、控えから入った選手は最後の位置（二塁）で出る
        var card = Assert.Single(SubstitutionTelop.Build(prog.Substitutions));
        Assert.Equal(2, card.Rows.Count);
        var subRow = card.Rows.Single(r => r.Name == "H控2");
        Assert.Equal("守備交代", subRow.Kind);
        Assert.Equal("セカンド", subRow.Position);
        Assert.Equal("H一", subRow.OutName);
        var moved = card.Rows.Single(r => r.Name == "H二");
        Assert.Equal("守備位置変更", moved.Kind);
        Assert.Equal("ファースト", moved.Position);
    }

    [Fact]
    public void DhTeam_ReliefIsOutOfTheOrder_AndReleaseDhPutsThePitcherIntoTheOrder()
    {
        var prog = new MatchProgression(DhTeam("A"), DhTeam("H"), Ctx(), 7UL);
        ToTopHead(prog);
        var reliever = prog.SubstitutionOptions(teamIsAway: false).Reserves.First(x => x.Name == "H二番手");
        Assert.True(prog.ChangePitcher(teamIsAway: false, reliever));
        var relief = Assert.Single(prog.Substitutions[0].Entries);
        Assert.Equal(SubstitutionEntryKind.Relief, relief.Kind);
        Assert.Equal(0, relief.Order);
        Assert.Equal("H先発P", relief.Out!.Name);

        // DHが左翼へ就き（元の左翼手が退く）、投手がDHの打順へ入る
        Assert.True(prog.ReleaseDh(teamIsAway: false, FieldPosition.LeftField));
        var entries = prog.Substitutions[1].Entries;
        var dhRelease = entries.Single(x => x.Kind == SubstitutionEntryKind.DhRelease);
        Assert.Equal(9, dhRelease.Order);
        Assert.Equal("H二番手", dhRelease.In.Name);
        Assert.Equal(FieldPosition.Pitcher, dhRelease.Position);
        Assert.Null(dhRelease.Out);   // 元DHは守備に就いて残っている
        var moved = entries.Single(x => x.In.Name == "HDH");
        Assert.Equal(SubstitutionEntryKind.PositionChange, moved.Kind);
        Assert.Equal(FieldPosition.LeftField, moved.Position);
        Assert.Equal("H左", moved.Out!.Name);
    }

    [Fact]
    public void CancelledDraft_RemovesItsEntries()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), Ctx(), 7UL);
        ToTopHead(prog);
        prog.BeginSubstitutionDraft();
        var sub = prog.SubstitutionOptions(teamIsAway: true).Reserves.First(x => x.Name == "A控1");
        Assert.True(prog.PinchHit(teamIsAway: true, sub));
        Assert.Single(prog.Substitutions);
        prog.CancelSubstitutionDraft();
        Assert.Empty(prog.Substitutions);
    }

    [Fact]
    public void Replay_RebuildsTheSameLog()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), Ctx(), 11UL);
        ToTopHead(prog);
        var sub = prog.SubstitutionOptions(teamIsAway: true).Reserves.First(x => x.Name == "A控1");
        Assert.True(prog.PinchHit(teamIsAway: true, sub));
        for (var i = 0; i < 4; i++) prog.Advance();
        var before = prog.Substitutions.Select(SubstitutionTelop.HistoryLine).ToList();

        var save = prog.Save();
        prog.SeekTo(save.ConfirmedPlateAppearances, save.ConfirmedPitchesInCurrentPa);
        Assert.Equal(before, prog.Substitutions.Select(SubstitutionTelop.HistoryLine).ToList());
    }

    [Fact]
    public void AiSubstitutions_AreLogged()
    {
        // 委任AIは疲れた先発を代える＝どれかのシードで AI の継投がログに載る
        var found = false;
        for (ulong seed = 1; seed <= 10 && !found; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"), Ctx(), seed);
            prog.SkipDelegateToAi(managerIsAway: false, managerTacticalSense: 70);
            found = prog.Substitutions.Any(e => !e.TeamIsAway
                && e.Entries.Any(x => x.Kind == SubstitutionEntryKind.Relief && x.Out?.Name == "H先発P"));
        }
        Assert.True(found, "委任AIの継投が交代ログに一度も載らない");
    }

    [Fact]
    public void WithoutPitchCapture_NothingIsLogged()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, 7UL);
        prog.Advance();
        var sub = prog.SubstitutionOptions(teamIsAway: true).Reserves.First(x => x.Name == "A控1");
        Assert.True(prog.PinchHit(teamIsAway: true, sub));
        Assert.Empty(prog.Substitutions);
    }

    [Fact]
    public void Logging_DoesNotChangeTheGame()
    {
        // 記録は観測専用＝采配なしの試合は記録の有無で同じ結果になる
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var a = new MatchProgression(Team("A"), Team("H"), Ctx(), seed).FinishRemaining();
            var b = new MatchProgression(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed).FinishRemaining();
            Assert.Equal(a.AwayRuns, b.AwayRuns);
            Assert.Equal(a.HomeRuns, b.HomeRuns);
            Assert.Equal(a.Log.Select(l => l.Result), b.Log.Select(l => l.Result));
        }
    }
}
