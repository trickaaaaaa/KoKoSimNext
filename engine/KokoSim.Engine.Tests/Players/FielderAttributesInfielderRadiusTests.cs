using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// 内野手の横の守備範囲（issue #1661）。守備50で基準値と恒等、守備が高いほど広がり低いほど狭まる、
/// 下限・上限で頭打ちになることを固定する。
/// </summary>
public sealed class FielderAttributesInfielderRadiusTests
{
    private const double BaseM = 4.20;
    private const double SlopePerPoint = 0.027;
    private const double FloorM = 3.4;
    private const double CapM = 5.0;

    [Fact]
    public void Fielding50_EqualsBase()
    {
        var attrs = new FielderAttributes { Fielding = 50 };
        Assert.Equal(BaseM, attrs.InfielderFieldingRadiusM(BaseM, SlopePerPoint, FloorM, CapM), 9);
    }

    [Fact]
    public void HigherFielding_WidensRadius()
    {
        var low = new FielderAttributes { Fielding = 30 };
        var mid = new FielderAttributes { Fielding = 50 };
        var high = new FielderAttributes { Fielding = 70 };

        var rLow = low.InfielderFieldingRadiusM(BaseM, SlopePerPoint, FloorM, CapM);
        var rMid = mid.InfielderFieldingRadiusM(BaseM, SlopePerPoint, FloorM, CapM);
        var rHigh = high.InfielderFieldingRadiusM(BaseM, SlopePerPoint, FloorM, CapM);

        Assert.True(rLow < rMid, $"rLow={rLow} rMid={rMid}");
        Assert.True(rMid < rHigh, $"rMid={rMid} rHigh={rHigh}");
    }

    [Fact]
    public void VeryLowFielding_ClampsAtFloor()
    {
        var attrs = new FielderAttributes { Fielding = 1 };
        var r = attrs.InfielderFieldingRadiusM(BaseM, SlopePerPoint, FloorM, CapM);
        Assert.Equal(FloorM, r, 9);
    }

    [Fact]
    public void VeryHighFielding_ClampsAtCap()
    {
        var attrs = new FielderAttributes { Fielding = 99 };
        var r = attrs.InfielderFieldingRadiusM(BaseM, SlopePerPoint, FloorM, CapM);
        Assert.Equal(CapM, r, 9);
    }

    [Fact]
    public void DefaultCoefficients_Draft20And80_MatchIssueRoughOrder()
    {
        // issue #1661 の叩き台: 守備20で約3.4m・守備80で約5.0m（±0.8m・slope≈0.027）。
        // 既定の FieldingCoefficients と同じ値で計算し、同じ桁に収まることを固定する。
        var c = new FieldingCoefficients();
        var r20 = new FielderAttributes { Fielding = 20 }.InfielderFieldingRadiusM(
            c.InfielderFieldingRadiusM, c.InfielderFieldingRadiusSlopeMPerPoint,
            c.InfielderFieldingRadiusFloorM, c.InfielderFieldingRadiusCapM);
        var r80 = new FielderAttributes { Fielding = 80 }.InfielderFieldingRadiusM(
            c.InfielderFieldingRadiusM, c.InfielderFieldingRadiusSlopeMPerPoint,
            c.InfielderFieldingRadiusFloorM, c.InfielderFieldingRadiusCapM);

        Assert.InRange(r20, 3.3, 3.5);
        Assert.InRange(r80, 4.9, 5.1);
    }
}
