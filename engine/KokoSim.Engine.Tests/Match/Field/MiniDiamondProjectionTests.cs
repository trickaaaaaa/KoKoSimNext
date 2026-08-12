using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

/// <summary>
/// 守備フォーカス中の小ダイヤモンド（issue #595）の座標写像の検算。
/// 走者ドットも塁マスも同じ写像を通るので、ここが狂うと「塁の上に居るのに塁からずれた点」が出る。
/// </summary>
public sealed class MiniDiamondProjectionTests
{
    private const int Digits = 4;

    [Fact]
    public void FourBases_MapToTheMidpointsOfTheFourEdges()
    {
        // 本塁＝下中央・二塁＝上中央・一塁＝右中央・三塁＝左中央（＝画面で見た菱形）。
        var home = MiniDiamondProjection.ToLocal01(FieldDiagramGeometry.Home);
        var second = MiniDiamondProjection.ToLocal01(FieldDiagramGeometry.Second);
        var first = MiniDiamondProjection.ToLocal01(FieldDiagramGeometry.First);
        var third = MiniDiamondProjection.ToLocal01(FieldDiagramGeometry.Third);

        Assert.Equal(0.5, home.U, Digits);
        Assert.Equal(0.5, second.U, Digits);
        Assert.Equal(0.5, first.V, Digits);
        Assert.Equal(0.5, third.V, Digits);

        // 上下・左右が中心から等距離＝箱の中で菱形が傾かない。
        Assert.Equal(0.5 - second.V, home.V - 0.5, Digits);
        Assert.Equal(0.5 - third.U, first.U - 0.5, Digits);
        // 本塁-二塁の間隔と一塁-三塁の間隔が等しい＝縦横で潰れない。
        Assert.Equal(home.V - second.V, first.U - third.U, Digits);
    }

    [Fact]
    public void EveryBase_SitsInsideTheBoxWithMargin()
    {
        foreach (var b in new[]
                 {
                     FieldDiagramGeometry.Home, FieldDiagramGeometry.First,
                     FieldDiagramGeometry.Second, FieldDiagramGeometry.Third,
                 })
        {
            var (u, v) = MiniDiamondProjection.ToLocal01(b);
            Assert.InRange(u, 0.02, 0.98);
            Assert.InRange(v, 0.02, 0.98);
        }
    }

    [Fact]
    public void FirstBaseOverrun_StaysInsideTheBox()
    {
        // 打者走者は一塁の 3.5m 先まで駆け抜ける（issue #443）。塁ちょうどで箱を切ると枠から出る。
        const double overrunM = 3.5;
        var dir = System.Math.Sqrt(0.5);   // 本塁→一塁の単位ベクトル（45°）
        var x = FieldDiagramGeometry.First.X + overrunM * dir;
        var y = FieldDiagramGeometry.First.Y + overrunM * dir;

        var (u, v) = MiniDiamondProjection.ToLocal01(x, y);
        Assert.True(u < 1.0, "一塁駆け抜けが箱の右端を越えている");
        Assert.True(v > 0.0, "一塁駆け抜けが箱の上端を越えている");
    }

    [Fact]
    public void MidwayBetweenBases_MapsToMidwayBetweenTheirDots()
    {
        // 写像は一次＝レッグの途中の走者は必ず塁と塁を結ぶ線上に乗る（走塁が図の上で折れない）。
        var second = MiniDiamondProjection.ToLocal01(FieldDiagramGeometry.Second);
        var third = MiniDiamondProjection.ToLocal01(FieldDiagramGeometry.Third);
        var mid = MiniDiamondProjection.ToLocal01(
            (FieldDiagramGeometry.Second.X + FieldDiagramGeometry.Third.X) / 2.0,
            (FieldDiagramGeometry.Second.Y + FieldDiagramGeometry.Third.Y) / 2.0);

        Assert.Equal((second.U + third.U) / 2.0, mid.U, Digits);
        Assert.Equal((second.V + third.V) / 2.0, mid.V, Digits);
    }

    [Fact]
    public void FarOutsidePoints_AreClampedToTheEdge()
    {
        // 想定外の座標（外野など）でも枠の外へは描かない。
        var (u, v) = MiniDiamondProjection.ToLocal01(-200.0, 300.0);
        Assert.Equal(0.0, u, Digits);
        Assert.Equal(0.0, v, Digits);
    }
}
