using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// この試合のベンチ入り（<see cref="Team.BenchEntry"/>）。サヨナラの場面で喜ぶ選手の数（issue #1176）と
/// 結束の顔ぶれ（設計書23 §3）の単一ソース。打順＋控え＋DH制の先発投手で、試合中の交代では変わらない。
/// </summary>
public sealed class BenchEntryTests
{
    private static Player P(string name) => new() { Name = name };

    private static Team TeamOf(int reserves, bool dh = false)
    {
        var order = Enumerable.Range(0, 9).Select(i => P("o" + i)).ToList();
        return new Team
        {
            Name = "A",
            BattingOrder = order,
            PitcherSlot = dh ? 0 : 8,
            Reserves = Enumerable.Range(0, reserves).Select(i => P("r" + i)).ToList(),
            DhSlot = dh ? 8 : -1,
            StartingPitcher = dh ? P("sp") : null,
        };
    }

    [Theory]
    [InlineData(11, 20)]   // 上限いっぱい（9＋11）
    [InlineData(3, 12)]    // 部員が少ない学校
    [InlineData(0, 9)]
    public void BenchEntry_IsBattingOrderPlusReserves(int reserves, int expected)
    {
        var team = TeamOf(reserves);
        Assert.Equal(expected, team.BenchEntry.Count);
        Assert.Equal(expected, new TeamState(team).BenchEntryCount);
    }

    [Fact]
    public void BenchEntry_WithDh_CountsStartingPitcher()
    {
        var team = TeamOf(5, dh: true);
        Assert.Equal(9 + 5 + 1, team.BenchEntry.Count);
        Assert.Contains(team.StartingPitcher, team.BenchEntry);
    }

    [Fact]
    public void BenchEntryCount_DoesNotChangeWithSubstitution()
    {
        var team = TeamOf(4);
        var state = new TeamState(team);
        var before = state.BenchEntryCount;
        // 代打で控えを1人使っても、退いた選手はベンチに居る＝人数は変わらない
        Assert.True(state.PinchHitNext(team.Reserves[0]));
        Assert.Equal(before, state.BenchEntryCount);
    }

    [Fact]
    public void Cohesion_UsesBenchEntryMembers()
    {
        var c = new CohesionCoefficients();
        var team = TeamOf(4, dh: true);
        Assert.Equal(Cohesion.Value(Cohesion.BaseValue(new List<Player>(team.BenchEntry), team.Captain, c), team.CohesionDelta),
            Cohesion.ForTeam(team, c), 12);
    }
}
