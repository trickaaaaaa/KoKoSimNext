using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;
namespace KokoSim.Engine.Tests.Debugging;
public sealed class TmpLinerProbe
{
    private readonly ITestOutputHelper _o; public TmpLinerProbe(ITestOutputHelper o) { _o = o; }
    [Fact]
    public void Dump()
    {
        var field = new FieldGeometry(); var aero = new Aerodynamics(); var fielders = field.StandardAlignment(); var probe = new FieldingCoefficients() with { ErrorBaseProb = 0.0, IrregularBounceProb = 0.0, FlyErrorBaseProb = 0.0 };
        foreach (var bearing in new[] { -14.0, -6.0, 6.0, 14.0 })
          for (var launch = 0.0; launch <= 24.0; launch += 4.0)
            for (var exit = 46.0; exit >= 30.0; exit -= 4.0)
            {
                var ball = new BattedBall { ExitVelocityMps = exit, LaunchAngleDeg = launch, BearingDeg = bearing };
                var play = FieldingResolver.ResolveDetailed(ball, field, aero, BatterAttributes.LeagueAverage, fielders, probe, new Xoshiro256Random(1));
                if (play.Result != BattedBallResult.Out || !play.IsFly) continue;
                _o.WriteLine($"b={bearing} l={launch} e={exit} role={play.FielderRole} cls={play.Class} hang={play.HangTimeSeconds:F2} apex={play.ApexHeightM:F2} land=({play.LandingX:F1},{play.LandingZ:F1}) range={play.RangeM:F1}");
            }
    }
}
