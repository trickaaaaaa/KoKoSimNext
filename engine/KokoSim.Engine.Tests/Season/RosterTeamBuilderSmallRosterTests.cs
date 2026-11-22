using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>部員が少ない自動編成で同じ選手が打順と控え投手の2枠に入らない（issue #1244）。</summary>
public sealed class RosterTeamBuilderSmallRosterTests
{
    private static DevelopingPlayer Make(int id, bool pitcher, int level)
    {
        var p = new DevelopingPlayer { Id = id, Name = "選手" + id, Grade = 1 };
        PitcherRoleResolver.SeedAptitudeForRole(p, pitcher);
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, level); p.SetCap(k, 99); }
        return p;
    }

    private static void AssertNoDuplicates(Team team)
    {
        var ids = team.BattingOrder.Concat(team.Reserves).Select(p => p.SourceId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Build_PitchersPulledIntoLineup_AreNotAlsoInBullpen()
    {
        // 野手5＋投手5＝10人。野手が8に満たず、投手が打順の穴埋めに回る。
        var roster = new List<DevelopingPlayer>();
        for (var i = 0; i < 5; i++) roster.Add(Make(i, false, 40));
        for (var i = 5; i < 10; i++) roster.Add(Make(i, true, 50 - i));

        AssertNoDuplicates(RosterTeamBuilder.Build(roster, "新設校"));
    }

    [Fact]
    public void Build_BullpenFilledWhenEnoughPlayers()
    {
        var roster = new List<DevelopingPlayer>();
        for (var i = 0; i < 8; i++) roster.Add(Make(i, false, 40));
        for (var i = 8; i < 12; i++) roster.Add(Make(i, true, 50));

        var team = RosterTeamBuilder.Build(roster, "校");
        AssertNoDuplicates(team);
        Assert.Equal(2, team.Reserves.Count);
    }

    [Fact]
    public void Build_NineOrFewerPlayers_LineupHasPriority()
    {
        var roster = new List<DevelopingPlayer>();
        for (var i = 0; i < 4; i++) roster.Add(Make(i, false, 40));
        for (var i = 4; i < 9; i++) roster.Add(Make(i, true, 50));

        var team = RosterTeamBuilder.Build(roster, "校");
        AssertNoDuplicates(team);
        Assert.Empty(team.Reserves);
    }
}
