using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 守備位置割当（DefenseAssignment）の検証。「2枠を交換する」一手だけで任意の割当に到達でき、
/// そのあいだ9ポジションの一意性が保たれること＝スタメン設定の自由割当（issue #233）の土台を確認する。
/// </summary>
public sealed class DefenseAssignmentTests
{
    // 既定の並び（非DH）: 捕一二三遊左中右＋9番に投手。スタメン設定の初期状態と同型。
    private static FieldPosition[] Standard() => new[]
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
        FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        FieldPosition.Pitcher,
    };

    [Fact]
    public void Swap_MovesPitcherToAnyOrderSlot()
    {
        // 「三」を守る3番打者（＝背番号5のような野手）に投手位置を渡す。
        var next = DefenseAssignment.Swap(Standard(), 3, 8);

        Assert.Equal(FieldPosition.Pitcher, next[3]);
        Assert.Equal(FieldPosition.ThirdBase, next[8]);   // 元の投手が三へ入れ替わる
        Assert.Equal(3, DefenseAssignment.SlotOf(next, FieldPosition.Pitcher));
        Assert.True(DefenseAssignment.CoversAllPositions(next));
    }

    [Fact]
    public void Swap_DoesNotMutateInput()
    {
        var src = Standard();
        var next = DefenseAssignment.Swap(src, 0, 8);

        Assert.Equal(FieldPosition.Catcher, src[0]);      // 元配列は不変
        Assert.Equal(FieldPosition.Pitcher, src[8]);
        Assert.NotSame(src, next);
    }

    [Theory]
    [InlineData(2, 2)]    // 同一枠
    [InlineData(-1, 3)]   // 範囲外（選択なし）
    [InlineData(3, 9)]
    public void Swap_SameSlotOrOutOfRange_ReturnsUnchangedCopy(int a, int b)
    {
        var src = Standard();
        Assert.Equal(src, DefenseAssignment.Swap(src, a, b));
    }

    [Fact]
    public void Swap_AnySequence_KeepsNinePositionsUnique()
    {
        // 交換は多重集合を変えない＝どれだけ入れ替えても一意性は壊れない（自由割当の安全性）。
        var pos = Standard();
        var pairs = new[] { (0, 8), (8, 4), (1, 6), (4, 2), (7, 0), (3, 8), (5, 1) };
        foreach (var (a, b) in pairs)
        {
            pos = DefenseAssignment.Swap(pos, a, b);
            Assert.True(DefenseAssignment.CoversAllPositions(pos), $"交換 ({a},{b}) で網羅が崩れた");
        }
        Assert.Equal(9, pos.Distinct().Count());
    }

    [Fact]
    public void Swap_Dh_MovesDhSlotAndKeepsCoverage()
    {
        // DH制: 打順に「指」が1枠、投手は打順外。5番の「指」を6番の「遊」と交換する。
        var pos = Standard();
        pos[4] = FieldPosition.DesignatedHitter;   // 5番=DH（元の「遊」は9番へ）
        pos[8] = FieldPosition.Shortstop;          // 投手は打順外なので9番は野手
        var dhSlot = DefenseAssignment.SlotOf(pos, FieldPosition.DesignatedHitter);
        Assert.Equal(4, dhSlot);
        Assert.True(DefenseAssignment.CoversAllPositions(pos, dhSlot));

        var next = DefenseAssignment.Swap(pos, 4, 8);
        var nextDh = DefenseAssignment.SlotOf(next, FieldPosition.DesignatedHitter);

        Assert.Equal(8, nextDh);                                   // DH枠が追従して動く
        Assert.Equal(FieldPosition.Shortstop, next[4]);            // 元DHの選手が遊へ就く
        Assert.True(DefenseAssignment.CoversAllPositions(next, nextDh));
    }

    [Fact]
    public void CoversAllPositions_MissingPosition_IsRejected()
    {
        var pos = Standard();
        pos[0] = FieldPosition.FirstBase;   // 捕手が居ない（一塁が2人）
        Assert.False(DefenseAssignment.CoversAllPositions(pos));
    }

    [Fact]
    public void BuildFromLineup_AcceptsPitcherMovedOffSlotNine()
    {
        // #233 の核心: 打順9番以外（ここでは4番）の選手を投手にしたスタメンが、そのまま試合用 Team に組める。
        var pos = DefenseAssignment.Swap(Standard(), 3, 8);
        var slots = new List<LineupSlot>(9);
        for (var i = 0; i < 9; i++)
            slots.Add(new LineupSlot(Dp(i + 1, $"選手{i + 1}"), pos[i]));

        var team = RosterTeamBuilder.BuildFromLineup(
            new LineupSpec(slots, PitcherSlot: DefenseAssignment.SlotOf(pos, FieldPosition.Pitcher)));

        Assert.Equal(3, team.PitcherSlot);
        Assert.Equal(FieldPosition.Pitcher, team.BattingOrder[3].Position);
        Assert.Equal(FieldPosition.ThirdBase, team.BattingOrder[8].Position);
        _ = new TeamState(team);   // 守備網羅を満たすので TeamState 構築は例外を出さない
    }

    private static DevelopingPlayer Dp(int id, string name)
        => new() { Id = id, Name = name, UniformNumber = Math.Clamp(id, 1, UniformNumberAssigner.BenchSize) };
}
