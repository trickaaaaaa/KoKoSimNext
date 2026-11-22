using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 内野通過の走査（InfielderCanCutOff／FindClosestInfielderMiss）の反復上限（issue #1215）。
/// 打球が内野に止まったまま誰も届かない経路でも、走査が必ず終わる（過去の CameraPlan 無限ループと同型の安全弁）。
/// </summary>
public sealed class InfieldInterceptScanBoundTests
{
    private static BouncePath RestingInInfield(FieldingCoefficients c) =>
        BouncePath.Compute(new Vector3D(0, 0, 10), new Vector3D(0, -2.0, 0.5), 0.3, c, IrregularBounce.None);

    [Fact]
    public void InfielderCanCutOff_BallStoppingInInfield_WithNobodyReaching_TerminatesAsNotCutOff()
    {
        var c = new FieldingCoefficients();
        var path = RestingInInfield(c);
        var stop = path.StopPosition;
        Assert.True(System.Math.Sqrt(stop.X * stop.X + stop.Z * stop.Z) < c.InfieldDepthM);

        Assert.False(FieldingResolver.InfielderCanCutOff(new List<Fielder>(), path, c));
    }

    [Fact]
    public void FindClosestInfielderMiss_BallStoppingInInfield_Terminates()
    {
        var c = new FieldingCoefficients();
        var path = RestingInInfield(c);

        Assert.Null(FieldingResolver.FindClosestInfielderMiss(new List<Fielder>(), path, c));
    }
}
