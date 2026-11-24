using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

/// <summary>投球チャート（issue #1541）の写像。向きは投手の後ろから見た側（対決ビューの絵と同じ）。</summary>
public sealed class PitchChartProjectionTests
{
    private static readonly StrikeZone Zone = new();

    [Fact]
    public void ZoneCenter_IsInTheMiddleOfTheZoneBox()
    {
        var (u, v) = PitchChartProjection.ToLocal01(0.0, Zone.CenterY, Zone);
        var (u0, v0, u1, v1) = PitchChartProjection.ZoneLocal01(Zone);
        Assert.Equal((u0 + u1) / 2.0, u, 6);
        Assert.Equal((v0 + v1) / 2.0, v, 6);
        Assert.Equal(0.5, u, 6);
    }

    /// <summary>
    /// 一塁側（+X）は投手の後ろから見て<b>左</b>。右打者は三塁側（−X）に立つ＝内角は図の右寄り。
    /// ここが逆になると、対決ビューで内角に来た球が図では外角に打たれる。
    /// </summary>
    [Fact]
    public void FirstBaseSide_IsLeft_AndRightHandedBattersInsideIsRight()
    {
        var (uFirstSide, _) = PitchChartProjection.ToLocal01(+0.15, Zone.CenterY, Zone);
        var (uRhbInside, _) = PitchChartProjection.ToLocal01(-0.15, Zone.CenterY, Zone);
        Assert.True(uFirstSide < 0.5, "一塁側の球が図の右に出ている");
        Assert.True(uRhbInside > 0.5, "右打者の内角（三塁側）が図の左に出ている");
    }

    [Fact]
    public void HighPitch_IsAbove_LowPitch()
    {
        var (_, vHigh) = PitchChartProjection.ToLocal01(0.0, Zone.TopM, Zone);
        var (_, vLow) = PitchChartProjection.ToLocal01(0.0, Zone.BottomM, Zone);
        Assert.True(vHigh < vLow);
    }

    [Fact]
    public void ZoneBox_LeavesMarginOnEverySide()
    {
        var (u0, v0, u1, v1) = PitchChartProjection.ZoneLocal01(Zone);
        Assert.True(u0 > 0.0 && u1 < 1.0 && v0 > 0.0 && v1 < 1.0);
        Assert.True(u0 < u1 && v0 < v1);
    }

    [Fact]
    public void FarOutsidePitch_IsClampedToTheEdge()
    {
        var (u, v) = PitchChartProjection.ToLocal01(-3.0, -1.0, Zone);
        Assert.Equal(1.0, u, 6);
        Assert.Equal(1.0, v, 6);
    }
}
