using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// チームの結束の単一ソース <see cref="Cohesion.ForTeam(Team, CohesionCoefficients)"/>（issue #935）。
/// 試合（TeamState）と画面（チーム総合力・主将指名）が同じ値を出すこと、主将の差し替えが効くこと。
/// </summary>
public sealed class CohesionForTeamTests
{
    private static readonly CohesionCoefficients C = new();

    private static Player M(string name, int leadership = 50, Personality type = Personality.HonorStudent, int mental = 50)
        => new Player { Name = name, Leadership = leadership, Mental = mental }.WithAxes(type);

    private static Team TeamOf(Player? captain, double delta, List<Player> reserves)
    {
        var order = Enumerable.Range(0, 9).Select(i => M("o" + i, type: i % 3 == 0 ? Personality.Genius : Personality.HonorStudent)).ToList();
        return new Team { Name = "A", BattingOrder = order, PitcherSlot = 8, Reserves = reserves, CohesionDelta = delta, Captain = captain };
    }

    [Fact]
    public void ForTeam_MatchesTeamStateValue()
    {
        var reserves = Enumerable.Range(0, 5).Select(i => M("r" + i, leadership: 70, type: Personality.LoneWolf)).ToList();
        var team = TeamOf(M("cap", leadership: 80, mental: 70), delta: 6.0, reserves);
        Assert.Equal(new TeamState(team).CohesionValue(C), Cohesion.ForTeam(team, C), 12);
    }

    [Fact]
    public void ForTeam_CountsReservesAndDelta()
    {
        var team = TeamOf(null, 0.0, new List<Player>());
        var members = new List<Player>(team.BattingOrder);
        Assert.Equal(Cohesion.BaseValue(members, null, C), Cohesion.ForTeam(team, C), 12);
        var lonely = TeamOf(null, 0.0, Enumerable.Range(0, 4).Select(i => M("r" + i, type: Personality.LoneWolf)).ToList());
        Assert.True(Cohesion.ForTeam(lonely, C) < Cohesion.ForTeam(team, C));   // 一匹狼の控えは顔ぶれの減点
        var withDelta = TeamOf(null, 10.0, new List<Player>());
        Assert.Equal(Cohesion.ForTeam(team, C) + 10.0, Cohesion.ForTeam(withDelta, C), 12);
    }

    [Fact]
    public void ForTeam_CaptainOverride_StrongerLeaderRaisesCohesion()
    {
        var team = TeamOf(M("weak", leadership: 20, mental: 30), 0.0, new List<Player>());
        var strong = M("strong", leadership: 90, mental: 90);
        Assert.True(Cohesion.ForTeam(team, strong, C) > Cohesion.ForTeam(team, C));
        Assert.Equal(Cohesion.ForTeam(team, team.Captain, C), Cohesion.ForTeam(team, C), 12);
    }
}
