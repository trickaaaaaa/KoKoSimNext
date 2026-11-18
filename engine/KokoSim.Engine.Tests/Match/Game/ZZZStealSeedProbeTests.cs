using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

public sealed class ZZZStealSeedProbeTests
{
    private readonly ITestOutputHelper _out;
    public ZZZStealSeedProbeTests(ITestOutputHelper output) => _out = output;

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 70, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(
                new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
                System.Array.Empty<Player>()),
        };
    }

    private static (GameResult Result, List<LivePitch> Pitches) PlayStealingEveryPitch(ulong seed, StealTarget target)
    {
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true, CapturePitches = true }, seed);
        var pitches = new List<LivePitch>();
        LivePitch? seen = null;
        while (true)
        {
            var r = prog.AdvancePitch();
            var lp = prog.LastPitch;
            if (lp is not null && !ReferenceEquals(lp, seen)) { seen = lp; pitches.Add(lp); }
            if (r == AdvancePitchResult.Finished) break;
            if (r == AdvancePitchResult.Pitch)
            {
                prog.SetStealAttempt(true, StartType.Gamble, target);
                prog.SetStealAttempt(false, StartType.Gamble, target);
            }
        }
        return (prog.BuildResult(), pitches);
    }

    [Fact]
    public void FindSeedWithStealToThird()
    {
        for (ulong seed = 1; seed <= 300; seed++)
        {
            var (_, pitches) = PlayStealingEveryPitch(seed, StealTarget.Third);
            var hasSteal = pitches.Exists(p => p.StealPlay is not null);
            var hasPickoff = pitches.Exists(p => p.PickoffPlay is not null);
            if (hasSteal && !hasPickoff)
            {
                _out.WriteLine($"FOUND seed={seed}");
                return;
            }
        }
        _out.WriteLine("NOT FOUND");
    }
}
