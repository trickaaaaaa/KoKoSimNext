using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

/// <summary>
/// 球場俯瞰図の共通地理（FieldDiagramGeometry）の検算。
/// 芝土境界弧は「マウンド中心・半径29m（95ft相当）」——本塁中心ではない——であり、
/// 二塁ベース（本塁から38.8m）が必ずダートの内側に入ることを保証する。
/// </summary>
public sealed class FieldDiagramGeometryTests
{
    private const double Deg45 = System.Math.PI / 4.0;

    [Fact]
    public void InfieldDirtRadius_AtCenterBearing_IsMoundPlusArcRadius()
    {
        // 中堅方向（θ=0）: 本塁→マウンド18.4m ＋ 弧半径29m ＝ 47.4m（二塁の約9m後方）。
        Assert.Equal(47.4, FieldDiagramGeometry.InfieldDirtRadius(0.0), 1);
    }

    [Fact]
    public void InfieldDirtRadius_AlongFoulLines_IsAbout38_9m()
    {
        // ±45°（ファウルライン沿い）: 一・三塁（27.4m）の約11m先まで土。
        Assert.Equal(38.9, FieldDiagramGeometry.InfieldDirtRadius(Deg45), 1);
        Assert.Equal(38.9, FieldDiagramGeometry.InfieldDirtRadius(-Deg45), 1);
    }

    [Fact]
    public void InfieldDirtRadius_CoversSecondBase()
    {
        // 二塁は本塁から38.8m（θ=0）。境界弧の頂点47.4mの内側＝ダート上に載る。
        var secondBearing = FieldDiagramGeometry.BearingRad(FieldDiagramGeometry.Second);
        var secondDistance = System.Math.Sqrt(
            FieldDiagramGeometry.Second.X * FieldDiagramGeometry.Second.X +
            FieldDiagramGeometry.Second.Y * FieldDiagramGeometry.Second.Y);
        Assert.True(FieldDiagramGeometry.InfieldDirtRadius(secondBearing) > secondDistance,
            "二塁ベースは芝土境界弧の内側（ダート上）にあること");
    }

    [Fact]
    public void InfieldDirtRadius_IsSymmetric()
    {
        for (var deg = 0; deg <= 45; deg += 5)
        {
            var th = deg * System.Math.PI / 180.0;
            Assert.Equal(
                FieldDiagramGeometry.InfieldDirtRadius(th),
                FieldDiagramGeometry.InfieldDirtRadius(-th), 6);
        }
    }

    [Fact]
    public void InfieldDirtRadius_IsMonotonicallyDecreasingFromCenterToWing()
    {
        // 弧の中心（マウンド）が本塁より前にあるため、中堅方向が最遠・両翼方向へ単調減少。
        var prev = double.MaxValue;
        for (var deg = 0; deg <= 45; deg += 5)
        {
            var r = FieldDiagramGeometry.InfieldDirtRadius(deg * System.Math.PI / 180.0);
            Assert.True(r < prev, $"θ={deg}° で単調減少が崩れた（{r} >= {prev}）");
            prev = r;
        }
    }

    /// <summary>左右別の FenceRadius（issue #959）は engine の判定式 FenceDistance と全方位で一致し、左右同値なら従来式と一致する。</summary>
    [Theory]
    [InlineData(97.0, 99.0, 121.0)]
    [InlineData(95.0, 95.0, 118.0)]
    [InlineData(101.0, 100.0, 122.0)]
    public void FenceRadius_LeftRight_MatchesFieldGeometryFenceDistance(double left, double right, double center)
    {
        var geom = new FieldGeometry { LeftFenceM = left, RightFenceM = right, CenterFenceM = center };
        for (var deg = -45; deg <= 45; deg += 5)
        {
            var rad = deg * System.Math.PI / 180.0;
            Assert.Equal(geom.FenceDistance(rad), FieldDiagramGeometry.FenceRadius(rad, left, right, center), 9);
        }
        Assert.Equal(left, FieldDiagramGeometry.FenceRadius(-Deg45, left, right, center), 9);
        Assert.Equal(right, FieldDiagramGeometry.FenceRadius(Deg45, left, right, center), 9);
        Assert.Equal(center, FieldDiagramGeometry.FenceRadius(0, left, right, center), 9);
        if (left == right)
            Assert.Equal(FieldDiagramGeometry.FenceRadius(0.3, left, center), FieldDiagramGeometry.FenceRadius(0.3, left, right, center), 12);
    }
}
