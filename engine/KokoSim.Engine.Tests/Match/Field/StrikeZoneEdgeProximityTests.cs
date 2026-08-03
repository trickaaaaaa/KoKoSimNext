using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

/// <summary>
/// ゾーン内の「縁への近さ」（issue #403）の幾何テスト。中心=0・縁上=1・チェビシェフ距離
/// （横縦それぞれ半幅で正規化した大きい方）であることを既知の座標で固定する。
/// </summary>
public sealed class StrikeZoneEdgeProximityTests
{
    private static readonly StrikeZone Zone = new();

    [Fact]
    public void EdgeProximity_AtCenter_IsZero()
        => Assert.Equal(0.0, Zone.EdgeProximity(0.0, Zone.CenterY), 9);

    [Fact]
    public void EdgeProximity_OnEdges_IsOne()
    {
        // 横縁（x=±HalfWidth・高さは中心）
        Assert.Equal(1.0, Zone.EdgeProximity(Zone.HalfWidthM, Zone.CenterY), 9);
        Assert.Equal(1.0, Zone.EdgeProximity(-Zone.HalfWidthM, Zone.CenterY), 9);
        // 縦縁（上端・下端、x=0）
        Assert.Equal(1.0, Zone.EdgeProximity(0.0, Zone.TopM), 9);
        Assert.Equal(1.0, Zone.EdgeProximity(0.0, Zone.BottomM), 9);
        // 四隅も1（1を超えない）
        Assert.Equal(1.0, Zone.EdgeProximity(Zone.HalfWidthM, Zone.TopM), 9);
    }

    [Fact]
    public void EdgeProximity_Midway_IsHalf_AndTakesLargerAxis()
    {
        // 横だけ半分寄り＝0.5
        Assert.Equal(0.5, Zone.EdgeProximity(Zone.HalfWidthM * 0.5, Zone.CenterY), 9);
        // 縦だけ半分寄り＝0.5
        Assert.Equal(0.5, Zone.EdgeProximity(0.0, Zone.CenterY + Zone.HalfHeightM * 0.5), 9);
        // 横0.5・縦0.8 → 大きい方の0.8（ユークリッド合成ではない）
        Assert.Equal(0.8, Zone.EdgeProximity(Zone.HalfWidthM * 0.5, Zone.CenterY + Zone.HalfHeightM * 0.8), 9);
    }

    [Fact]
    public void EdgeProximity_OutsideZone_ClampsToOne()
        => Assert.Equal(1.0, Zone.EdgeProximity(Zone.HalfWidthM * 3.0, Zone.CenterY), 9);
}
