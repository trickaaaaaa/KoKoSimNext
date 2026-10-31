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
/// 交代の下書き（issue #718）。交代パネルで予約した交代は「確定して試合へ戻る」まで取り消せる。
/// ・取り消すと、交代を一度もしなかった試合と1ビットも違わない（決定論・不変条件#2）。
/// ・大会の試合（投手回復度・前週の球数つき・注入乱数）でも同じ。
/// ・伝令は決定列に積まれ、再生し直しても消えない。
/// ・確定すれば交代は残る。
/// </summary>
public sealed class SubstitutionDraftTests
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
        Reserves = TeamReserves.Merge(new[] { Rp(n + "二番手"), Rp(n + "三番手") }, new[]
        {
            P(FieldPosition.FirstBase, n + "控1"), P(FieldPosition.LeftField, n + "控2"),
        }),
    };

    private const bool HomeSide = false;   // 自校＝後攻（1回表は守備）

    private static string PlayOut(MatchProgression prog)
    {
        while (prog.Advance()) { }
        return GameResultDigest.Sha256Of(prog.BuildResult());
    }

    /// <summary>守備中の打席境界まで進める。</summary>
    private static void AdvanceToDefense(MatchProgression prog, int minPa)
    {
        for (var i = 0; i < minPa; i++) prog.Advance();
        while (prog.IsTeamOnOffense(HomeSide)) prog.Advance();
    }

    private static void DoSomeSubstitutions(MatchProgression prog)
    {
        var o = prog.SubstitutionOptions(HomeSide);
        Assert.True(prog.SwapDefensivePositions(HomeSide, 1, 2));
        Assert.True(prog.DefenseAssign(HomeSide, 5, o.Reserves.First(r => r.Name.EndsWith("控2"))));
        Assert.True(prog.Relieve(HomeSide, prog.SubstitutionOptions(HomeSide).Reserves.First(r => r.Name.EndsWith("二番手"))));
    }

    [Fact]
    public void Cancel_MakesTheGameIdentical_ToNeverSubstituting()
    {
        var plain = new MatchProgression(Team("A"), Team("H"), new GameContext(), 7);
        AdvanceToDefense(plain, 12);
        var expected = PlayOut(plain);

        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), 7);
        AdvanceToDefense(prog, 12);
        var o0 = prog.SubstitutionOptions(HomeSide);
        var beforeLineup = o0.Lineup.Select(p => p.Name).ToList();
        var beforePitcher = o0.CurrentPitcher.Name;
        var beforeReserves = o0.Reserves.Select(p => p.Name).ToList();
        prog.BeginSubstitutionDraft();
        DoSomeSubstitutions(prog);
        Assert.Equal(3, prog.SubstitutionDraftCount);
        prog.CancelSubstitutionDraft();

        Assert.False(prog.InSubstitutionDraft);
        var after = prog.SubstitutionOptions(HomeSide);
        Assert.Equal(beforeLineup, after.Lineup.Select(p => p.Name));
        Assert.Equal(beforePitcher, after.CurrentPitcher.Name);
        Assert.Equal(beforeReserves, after.Reserves.Select(p => p.Name));
        Assert.Equal(expected, PlayOut(prog));
    }

    [Fact]
    public void Cancel_InTournamentMatch_KeepsRecoveryAndCarryOver()
    {
        // 大会の試合＝注入乱数＋投手回復度＋前週の球数。再生に渡し忘れると取り消し後の試合が変わる。
        MatchProgression Make(out Team home)
        {
            var away = Team("A");
            home = Team("H");
            var rec = new Dictionary<Player, double> { [home.BattingOrder[8]] = 0.55 };
            var carry = new Dictionary<Player, int> { [home.BattingOrder[8]] = 300 };
            return new MatchProgression(away, home, new GameContext { WeeklyPitchLimit = 500 }, new Xoshiro256Random(11),
                homePitchRecovery: rec, homePriorWeekPitches: carry);
        }

        var plain = Make(out _);
        AdvanceToDefense(plain, 8);
        var expected = PlayOut(plain);

        var prog = Make(out _);
        AdvanceToDefense(prog, 8);
        prog.BeginSubstitutionDraft();
        DoSomeSubstitutions(prog);
        prog.CancelSubstitutionDraft();
        Assert.Equal(expected, PlayOut(prog));
    }

    [Fact]
    public void Commit_KeepsTheSubstitutions()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), 7);
        AdvanceToDefense(prog, 12);
        prog.BeginSubstitutionDraft();
        DoSomeSubstitutions(prog);
        prog.CommitSubstitutionDraft();
        prog.CancelSubstitutionDraft();   // 確定後の取り消しは何もしない
        Assert.EndsWith("二番手", prog.SubstitutionOptions(HomeSide).CurrentPitcher.Name);
    }

    [Fact]
    public void Timeout_SurvivesReplay_AndCancelBeforeItKeepsIt()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), 7);
        AdvanceToDefense(prog, 12);
        Assert.Equal(MatchProgression.PlayerTimeoutResult.Defense, prog.CallTimeout(HomeSide, 0));
        var left = prog.DefenseTimeoutsLeft(HomeSide);

        // 伝令の後に交代を予約して取り消す＝再生し直しても伝令は残る（決定列に積まれている）。
        prog.BeginSubstitutionDraft();
        DoSomeSubstitutions(prog);
        prog.CancelSubstitutionDraft();
        Assert.Equal(left, prog.DefenseTimeoutsLeft(HomeSide));

        // 伝令ありの試合を最後まで＝同じ操作をした別個体と一致（再生の伝令が live と同じ効果）。
        var twin = new MatchProgression(Team("A"), Team("H"), new GameContext(), 7);
        AdvanceToDefense(twin, 12);
        twin.CallTimeout(HomeSide, 0);
        Assert.Equal(PlayOut(twin), PlayOut(prog));
    }

    [Fact]
    public void Cancel_WithoutChanges_DoesNotReplay()
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), 7);
        AdvanceToDefense(prog, 5);
        var current = prog.Current;
        prog.BeginSubstitutionDraft();
        prog.CancelSubstitutionDraft();
        Assert.Same(current, prog.Current);
    }
}
