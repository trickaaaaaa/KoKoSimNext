using System.Collections.Generic;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 強制発動 <see cref="ForcedOutcome.ChasedFoulFly"/> / <see cref="ForcedOutcome.BigFoul"/>（issue #1074 の撮影用）:
/// 初球が守備ビューで見せるファウルになり、打席は続く。
/// </summary>
public sealed class ForcedFoulTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50 - AbilityScale.Delta, Power = 50 - AbilityScale.Delta, LaunchTendency = 50, Discipline = 50 - AbilityScale.Delta,
        Speed = 50 - AbilityScale.Delta, ArmStrength = 50 - AbilityScale.Delta, Fielding = 50 - AbilityScale.Delta, Catching = 50 - AbilityScale.Delta, Bunt = 50 - AbilityScale.Delta,
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
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    [Theory]
    [InlineData(ForcedOutcome.ChasedFoulFly, FoulViewKind.ChasedFly, 1UL)]
    [InlineData(ForcedOutcome.ChasedFoulFly, FoulViewKind.ChasedFly, 42UL)]
    [InlineData(ForcedOutcome.BigFoul, FoulViewKind.BigFoul, 1UL)]
    [InlineData(ForcedOutcome.BigFoul, FoulViewKind.BigFoul, 42UL)]
    public void ForcedFoul_FirstPitchIsShownInTheFieldView(ForcedOutcome force, FoulViewKind expected, ulong seed)
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext { CaptureTimelines = true, CapturePitches = true }, seed,
            new ScenarioStart { Inning = 1, IsTop = true, Outs = 0 });
        prog.ForceNext(force);
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());   // 1球目の手前の采配窓
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());   // ファウル＝打席は続く

        var pitch = prog.LastPitch!.Pitch;
        Assert.Equal(PitchKind.Foul, pitch.Kind);
        Assert.Equal(1, pitch.StrikesAfter);
        Assert.NotNull(pitch.FoulFlight);
        Assert.Equal(expected, pitch.FoulFlight!.FoulView);
        if (expected == FoulViewKind.ChasedFly) Assert.NotEmpty(pitch.FoulFlight.Moves);
    }
}
