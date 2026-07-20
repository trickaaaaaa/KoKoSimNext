using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// スタメン仕様（LineupSpec）→試合用 Team 組立（RosterTeamBuilder.BuildFromLineup）の検証。
/// 打順・守備位置・DH・先発・主将の反映と、不正編成の弾きを確認する。
/// </summary>
public sealed class LineupSpecTests
{
    // 既定でベンチ入り（背番号あり）。ベンチ外を作るときだけ number: 0 を渡す。
    private static DevelopingPlayer Dp(int id, string name, bool pitcher = false, bool captain = false, int number = -1)
        => new()
        {
            Id = id, Name = name, IsPitcher = pitcher, IsCaptain = captain,
            UniformNumber = number >= 0 ? number : Math.Clamp(id, 1, UniformNumberAssigner.BenchSize),
        };

    // 非DH: 8守備＋投手9番。守備位置を一意網羅。
    private static readonly FieldPosition[] EightFieldPos =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
        FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
    };

    private static List<LineupSlot> StandardOrder(out DevelopingPlayer pitcher, bool captainAtLeadoff = false)
    {
        var slots = new List<LineupSlot>(9);
        for (var i = 0; i < 8; i++)
            slots.Add(new LineupSlot(Dp(i + 1, $"野手{i + 1}", captain: captainAtLeadoff && i == 0), EightFieldPos[i]));
        pitcher = Dp(9, "投手", pitcher: true);
        slots.Add(new LineupSlot(pitcher, FieldPosition.Pitcher)); // 9番＝投手（PitcherSlot=8）
        return slots;
    }

    [Fact]
    public void BuildFromLineup_NonDh_HonorsOrderPositionsAndSourceId()
    {
        var order = StandardOrder(out _);
        var team = RosterTeamBuilder.BuildFromLineup(new LineupSpec(order));

        Assert.Equal(9, team.BattingOrder.Count);
        Assert.False(team.UsesDh);
        Assert.Equal(8, team.PitcherSlot);
        Assert.Equal(FieldPosition.Pitcher, team.BattingOrder[8].Position);
        Assert.Equal(FieldPosition.Catcher, team.BattingOrder[0].Position);
        // SourceId が投影で伝播（成績帰属の前提）。
        Assert.Equal(1, team.BattingOrder[0].SourceId);
        Assert.Equal(9, team.BattingOrder[8].SourceId);
        // 守備網羅が満たされているので TeamState 構築は例外を出さない。
        _ = new TeamState(team);
    }

    [Fact]
    public void BuildFromLineup_Captain_ProjectedToSameReference()
    {
        var order = StandardOrder(out _, captainAtLeadoff: true);
        var team = RosterTeamBuilder.BuildFromLineup(new LineupSpec(order));
        Assert.NotNull(team.Captain);
        Assert.Same(team.BattingOrder[0], team.Captain); // 在場の主将は打順の同一 Player 参照
    }

    [Fact]
    public void BuildFromLineup_Dh_HonorsDhSlotAndStartingPitcher()
    {
        // 8守備＋DH（3番）。投手は打順外。
        var slots = new List<LineupSlot>(9);
        var posCursor = 0;
        for (var i = 0; i < 9; i++)
        {
            if (i == 3) { slots.Add(new LineupSlot(Dp(100, "DH打者"), FieldPosition.FirstBase)); continue; } // DHの守備位置は表示用
            slots.Add(new LineupSlot(Dp(i + 1, $"野手{i + 1}"), EightFieldPos[posCursor++]));
        }
        var sp = Dp(200, "先発", pitcher: true);
        var team = RosterTeamBuilder.BuildFromLineup(new LineupSpec(slots, DhSlot: 3, StartingPitcher: sp));

        Assert.True(team.UsesDh);
        Assert.Equal(3, team.DhSlot);
        Assert.NotNull(team.StartingPitcher);
        Assert.Equal(200, team.StartingPitcher!.SourceId);
        Assert.Equal(100, team.BattingOrder[3].SourceId); // DHが打順に居る
        _ = new TeamState(team); // 守備網羅（8野手＋先発）で構築成立
    }

    [Fact]
    public void BuildFromLineup_DhWithoutStartingPitcher_Throws()
    {
        var slots = Enumerable.Range(0, 9)
            .Select(i => new LineupSlot(Dp(i + 1, $"P{i}"), i < 8 ? EightFieldPos[i] : FieldPosition.FirstBase))
            .ToList();
        Assert.Throws<ArgumentException>(() => RosterTeamBuilder.BuildFromLineup(new LineupSpec(slots, DhSlot: 0)));
    }

    [Fact]
    public void BuildFromLineup_WrongCount_Throws()
    {
        var slots = new List<LineupSlot> { new(Dp(1, "A"), FieldPosition.Catcher) };
        Assert.Throws<ArgumentException>(() => RosterTeamBuilder.BuildFromLineup(new LineupSpec(slots)));
    }

    [Fact]
    public void BuildFromLineup_BenchOutInBattingOrder_Throws()
    {
        var order = StandardOrder(out _);
        // 3番をベンチ外（背番号0）の選手に差し替える。
        order[2] = order[2] with { Player = Dp(30, "ベンチ外", number: 0) };
        var ex = Assert.Throws<ArgumentException>(() => RosterTeamBuilder.BuildFromLineup(new LineupSpec(order)));
        Assert.Contains("ベンチ外", ex.Message);
    }

    [Fact]
    public void BuildFromLineup_BenchOutOnBenchOrBullpen_Throws()
    {
        var order = StandardOrder(out _);
        Assert.Throws<ArgumentException>(() => RosterTeamBuilder.BuildFromLineup(
            new LineupSpec(order, Bench: new List<DevelopingPlayer> { Dp(31, "控え外", number: 0) })));
        Assert.Throws<ArgumentException>(() => RosterTeamBuilder.BuildFromLineup(
            new LineupSpec(order, Bullpen: new List<DevelopingPlayer> { Dp(32, "投手外", pitcher: true, number: 0) })));
    }

    [Fact]
    public void BuildFromLineup_BenchOutStartingPitcher_Throws()
    {
        // DH制：先発投手だけベンチ外。
        var slots = new List<LineupSlot>(9);
        var posCursor = 0;
        for (var i = 0; i < 9; i++)
        {
            if (i == 3) { slots.Add(new LineupSlot(Dp(10, "DH打者"), FieldPosition.FirstBase)); continue; }
            slots.Add(new LineupSlot(Dp(i + 1, $"野手{i + 1}"), EightFieldPos[posCursor++]));
        }
        var sp = Dp(33, "先発", pitcher: true, number: 0);
        Assert.Throws<ArgumentException>(() => RosterTeamBuilder.BuildFromLineup(
            new LineupSpec(slots, DhSlot: 3, StartingPitcher: sp)));
    }

    [Fact]
    public void BuildFromLineup_AllBenchEntered_Succeeds()
    {
        var order = StandardOrder(out _);
        var team = RosterTeamBuilder.BuildFromLineup(new LineupSpec(
            order,
            Bullpen: new List<DevelopingPlayer> { Dp(11, "控え投手", pitcher: true) },
            Bench: new List<DevelopingPlayer> { Dp(12, "控え野手") }));
        Assert.Single(team.Bullpen);
        Assert.Single(team.Bench);
    }

    [Fact]
    public void BuildFromLineup_DuplicatePosition_Throws()
    {
        var order = StandardOrder(out _);
        // 2番の守備位置を捕手に変えて重複させる（捕手が2人・守備網羅が崩れる）。
        order[1] = order[1] with { Position = FieldPosition.Catcher };
        Assert.Throws<ArgumentException>(() => RosterTeamBuilder.BuildFromLineup(new LineupSpec(order)));
    }
}
