using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 強制発動 <see cref="ForcedOutcome.ThroughInfieldLeft"/> ほか（issue #1591 の撮影用）:
/// 初球が内野を抜ける単打になり、両側の内野手が打球を追う動きがタイムラインに出る。
/// </summary>
public sealed class ForcedThroughInfieldTests
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
    [InlineData(ForcedOutcome.ThroughInfieldLeft)]
    [InlineData(ForcedOutcome.ThroughInfieldMiddle)]
    [InlineData(ForcedOutcome.ThroughInfieldRight)]
    public void BallIsFound_ForEveryDirection(ForcedOutcome kind)
    {
        var field = new FieldGeometry();
        var ball = ForcedThroughInfieldBall.For(
            kind, field, new KokoSim.Engine.Match.Pitching.Aerodynamics(), field.StandardAlignment(), new FieldingCoefficients());
        Assert.NotNull(ball);
    }

    [Theory]
    [InlineData(ForcedOutcome.ThroughInfieldLeft, 1UL)]
    [InlineData(ForcedOutcome.ThroughInfieldMiddle, 1UL)]
    [InlineData(ForcedOutcome.ThroughInfieldRight, 1UL)]
    [InlineData(ForcedOutcome.ThroughInfieldLeft, 42UL)]
    public void FirstPitch_IsAThroughInfieldHit_WithTwoInfieldersChasing(ForcedOutcome force, ulong seed)
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed,
            new ScenarioStart { Inning = 1, IsTop = true, Outs = 0 });
        prog.ForceNext(force);
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());
        Assert.Equal(AdvancePitchResult.PlateAppearance, prog.AdvancePitch());

        var play = prog.Current?.Play;
        Assert.NotNull(play);
        var infield = new[] { FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop };
        var chasers = play!.Moves
            .Where(kv => infield.Contains(kv.Key) && kv.Value.Any(m => m.T0 < TimelineBuilder.ContactAt + 0.6))
            .Select(kv => kv.Key).ToList();
        Assert.True(chasers.Count >= 2, $"打球を追う内野手が2人未満: {string.Join(",", chasers)}");
    }
}
