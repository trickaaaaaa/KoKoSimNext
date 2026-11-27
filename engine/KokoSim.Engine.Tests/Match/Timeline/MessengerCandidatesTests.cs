using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 伝令役の候補（issue #1736）＝未出場の控え＋交代で退いた選手。出場中の選手と、この試合で怪我をして退いた選手は入らない。
/// 控えが先頭＝控えの添字だけで記録された従来の決定列は同じ人を指す。取り消しの再生でも同じ人が伝令役になる。
/// </summary>
public sealed class MessengerCandidatesTests
{
    private static Player P(FieldPosition pos, string name, int leadership = 50, int grade = 0) => new()
    {
        Position = pos, Name = name, Leadership = leadership, Grade = grade,
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
        Reserves = TeamReserves.Merge(new[] { Rp(n + "二番手") }, new[]
        {
            P(FieldPosition.FirstBase, n + "控1"), P(FieldPosition.LeftField, n + "控2"),
        }),
    };

    private static MatchInjuryEvent Hurt(string team, string name) => new(
        Inning: 3, IsTop: true, Scene: InjuryScene.Sliding, TeamName: team, PlayerName: name,
        PlayerSourceId: null, PlayerNumber: 0, Draw: default);

    private static Player Reserve(TeamState ts, string suffix) => ts.Reserves.First(r => r.Name.EndsWith(suffix));

    private static IEnumerable<string> Names(IEnumerable<Player> ps) => ps.Select(p => p.Name);

    [Fact]
    public void NoOneRetired_CandidatesAreTheReserves()
    {
        var ts = new TeamState(Team("H"));
        Assert.Equal(Names(ts.AvailableReserves), Names(ts.MessengerCandidates));
    }

    [Fact]
    public void RetiredPlayer_FollowsTheReserves_InRetirementOrder()
    {
        var ts = new TeamState(Team("H"));
        Assert.True(ts.DefensiveSub(ts.CurrentLineup[5], Reserve(ts, "控2")));   // 左が退く
        Assert.True(ts.DefensiveSub(ts.CurrentLineup[1], Reserve(ts, "控1")));   // 一が退く

        var names = Names(ts.MessengerCandidates).ToList();
        Assert.Equal(Names(ts.AvailableReserves).Concat(new[] { "H左", "H一" }), names);
    }

    [Fact]
    public void PlayersInTheGame_AreNeverCandidates()
    {
        var ts = new TeamState(Team("H"));
        Assert.True(ts.DefensiveSub(ts.CurrentLineup[5], Reserve(ts, "控2")));
        var names = Names(ts.MessengerCandidates).ToList();
        foreach (var p in ts.CurrentLineup) Assert.DoesNotContain(p.Name, names);
        Assert.DoesNotContain(ts.CurrentPitcher.Name, names);
    }

    [Fact]
    public void PitcherRelievedFromTheMound_IsACandidate()
    {
        var ts = new TeamState(Team("H"));
        Assert.True(ts.ChangePitcherTo(Reserve(ts, "二番手")));
        Assert.Contains("H先発P", Names(ts.MessengerCandidates));
        Assert.DoesNotContain("H二番手", Names(ts.MessengerCandidates));
    }

    [Fact]
    public void PlayerInjuredInThisMatch_IsNotACandidate_ButOtherRetiredAre()
    {
        var ts = new TeamState(Team("H"));
        var injuries = new List<MatchInjuryEvent>();
        ts.BindMatchInjuries(injuries);
        Assert.True(ts.DefensiveSub(ts.CurrentLineup[5], Reserve(ts, "控2")));   // 左は怪我で退く
        Assert.True(ts.DefensiveSub(ts.CurrentLineup[1], Reserve(ts, "控1")));   // 一は怪我なしで退く
        injuries.Add(Hurt("H", "H左"));

        var names = Names(ts.MessengerCandidates).ToList();
        Assert.DoesNotContain("H左", names);
        Assert.Contains("H一", names);
    }

    [Fact]
    public void InjuryOnTheOtherTeam_DoesNotExclude()
    {
        var ts = new TeamState(Team("H"));
        var injuries = new List<MatchInjuryEvent> { Hurt("A", "H左") };
        ts.BindMatchInjuries(injuries);
        Assert.True(ts.DefensiveSub(ts.CurrentLineup[5], Reserve(ts, "控2")));
        Assert.Contains("H左", Names(ts.MessengerCandidates));
    }

    [Fact]
    public void CallTimeout_WithARetiredPlayer_UsesThatPlayerAsMessenger()
    {
        var home = Team("H");
        var progress = GameEngine.NewProgress(Team("A"), home, new GameContext(), new Xoshiro256Random(5));
        var ts = progress.Home;
        var leader = ts.CurrentLineup[5];
        Assert.True(ts.DefensiveSub(leader, Reserve(ts, "控2")));
        var idx = ts.MessengerCandidates.ToList().FindIndex(p => p.Name == "H左");
        Assert.True(idx >= ts.AvailableReserves.Count);

        Assert.Equal(2, SubstitutionCommands.CallTimeout(progress, teamIsAway: false, idx, asOffense: false));
        Assert.Equal("H左", ts.DefenseMessenger?.Name);
    }

    [Fact]
    public void RetiredMessenger_SurvivesCancelReplay()
    {
        const bool homeSide = false;
        void ToDefense(MatchProgression prog)
        {
            for (var i = 0; i < 12; i++) prog.Advance();
            while (prog.IsTeamOnOffense(homeSide)) prog.Advance();
        }
        void SubAndSendRetired(MatchProgression prog)
        {
            prog.BeginSubstitutionDraft();
            Assert.True(prog.DefenseAssign(homeSide, 5, prog.SubstitutionOptions(homeSide).Reserves.First(r => r.Name.EndsWith("控2"))));
            prog.CommitSubstitutionDraft();
            var cands = prog.TimeoutMessengerCandidates(homeSide);
            var idx = cands.ToList().FindIndex(p => p.Name == "H左");
            Assert.True(idx >= 0);
            Assert.Equal(MatchProgression.PlayerTimeoutResult.Defense, prog.CallTimeout(homeSide, idx));
        }
        string PlayOut(MatchProgression prog)
        {
            while (prog.Advance()) { }
            return GameResultDigest.Sha256Of(prog.BuildResult());
        }

        var twin = new MatchProgression(Team("A"), Team("H"), new GameContext(), 7);
        ToDefense(twin);
        SubAndSendRetired(twin);

        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), 7);
        ToDefense(prog);
        SubAndSendRetired(prog);
        prog.BeginSubstitutionDraft();
        Assert.True(prog.Relieve(homeSide, prog.SubstitutionOptions(homeSide).Reserves.First(r => r.Name.EndsWith("二番手"))));
        prog.CancelSubstitutionDraft();   // 再生し直し＝決定列から組み直す

        Assert.Equal(PlayOut(twin), PlayOut(prog));
    }
}
