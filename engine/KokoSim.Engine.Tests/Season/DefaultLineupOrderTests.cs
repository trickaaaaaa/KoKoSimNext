using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>背番号どおりのスタメンの打順を AI と同じ規則で並べる（<see cref="DefaultLineupOrder"/>・issue #955）。</summary>
public sealed class DefaultLineupOrderTests
{
    private static readonly FieldPosition[] Fielding =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
        FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
    };

    // index 0＝投手（背番号1）、1〜8＝野手（背番号2〜9）。打撃は index が大きいほど強い。
    private static List<DevelopingPlayer> Roster(int pitcherContact, int pitcherPower)
    {
        var list = new List<DevelopingPlayer>();
        for (var i = 0; i < 9; i++)
        {
            var p = new DevelopingPlayer { Name = $"P{i}", Id = i + 1, UniformNumber = i + 1 };
            var lv = i == 0 ? pitcherContact : 30 + i * 5;
            p.SetLevel(AbilityKind.Contact, lv);
            p.SetLevel(AbilityKind.Power, i == 0 ? pitcherPower : lv);
            p.SetLevel(AbilityKind.Discipline, 50);
            p.SetLevel(AbilityKind.Speed, 50);
            p.SetLevel(AbilityKind.Bunt, 50);
            list.Add(p);
        }
        return list;
    }

    private static IReadOnlyList<(int RosterIndex, FieldPosition Position)> Run(List<DevelopingPlayer> roster)
        => DefaultLineupOrder.Arrange(roster, Enumerable.Range(1, 8).Select(i => (i, Fielding[i - 1])).ToList(), 0);

    [Fact]
    public void Fielders_KeepTheirUniformNumberPosition_AndFollowRoleOrder()
    {
        var roster = Run(Roster(20, 20));
        Assert.Equal(9, roster.Count);
        Assert.Equal(Enumerable.Range(0, 9), roster.Select(s => s.RosterIndex).OrderBy(x => x));

        foreach (var (idx, pos) in roster)
            Assert.Equal(idx == 0 ? FieldPosition.Pitcher : Fielding[idx - 1], pos);

        // 出塁力（ミート寄与）最上位が1番、小技最上位が2番、残りの最強打者が4番・次点が3番・その次が5番（LineupOrderer.Arrange の役割順）。
        Assert.Equal(new[] { 8, 7, 5, 6, 4 }, roster.Take(5).Select(s => s.RosterIndex));
    }

    [Fact]
    public void WeakHittingPitcher_BatsNinth()
    {
        var order = Run(Roster(20, 20));
        Assert.Equal(0, order[8].RosterIndex);
        Assert.Equal(FieldPosition.Pitcher, order[8].Position);
    }

    [Fact]
    public void StrongHittingPitcher_IsOrderedWithFielders()
    {
        var order = Run(Roster(100, 100));
        Assert.Equal(0, order[0].RosterIndex);   // 打撃が野手平均以上の投手は9番固定でなく野手と並ぶ（LineupOrderer.BatsWithFielders）
        Assert.Equal(FieldPosition.Pitcher, order[0].Position);
    }

    [Fact]
    public void MatchesLineupOrdererArrangeOnSamePlayers()
    {
        var roster = Roster(20, 20);
        var order = Run(roster);
        var players = Enumerable.Range(1, 8).Select(i => RosterTeamBuilder.ToPlayer(roster[i], Fielding[i - 1])).ToList();
        var expected = LineupOrderer.Arrange(players).Select(p => p.SourceId).ToList();
        Assert.Equal(expected, order.Take(8).Select(s => (int?)roster[s.RosterIndex].Id).ToList());
    }
}
