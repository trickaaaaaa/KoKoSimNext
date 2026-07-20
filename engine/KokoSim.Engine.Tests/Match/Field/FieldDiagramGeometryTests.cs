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
}
