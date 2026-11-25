using System;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

/// <summary>
/// フェンス距離の左右非対称補間の検証（設計書13-stadiums §2-1）。
/// φ=0→中堅、φ=±45°→該当ポール、対称時は従来式と一致。
/// </summary>
public sealed class FieldGeometryTests
{
    private const double Deg45 = Math.PI / 4.0;

    [Fact]
    public void FenceDistance_AtCenterAndPoles_ReturnsExactDimensions()
    {
        var field = new FieldGeometry { LeftFenceM = 91, RightFenceM = 100, CenterFenceM = 122 };

        Assert.Equal(122, field.FenceDistance(0), 6);        // 中堅
        Assert.Equal(91, field.FenceDistance(-Deg45), 6);    // 左翼ポール（−X）
        Assert.Equal(100, field.FenceDistance(Deg45), 6);    // 右翼ポール（+X）
    }

    [Fact]
    public void WallDistance_FairSide_EqualsFenceDistance()
    {
        var field = new FieldGeometry { LeftFenceM = 91, RightFenceM = 100, CenterFenceM = 122 };
        foreach (var deg in new[] { -45.0, -20.0, 0.0, 30.0, 45.0 })
            Assert.Equal(field.FenceDistance(deg * Math.PI / 180), field.WallDistance(deg * Math.PI / 180), 9);
    }

    /// <summary>ファウル側の壁はラインから垂直に FoulSideM。ライン沿い（ポール方向）はポールの距離、
    /// ラインから離れるほど壁が近くなる。FenceDistance を ±45° の外へ延長した値（cos(2φ) 補間）は使わない。</summary>
    [Fact]
    public void WallDistance_FoulSide_IsPerpendicularWallNotExtendedFence()
    {
        var field = new FieldGeometry { LeftFenceM = 95, RightFenceM = 95, CenterFenceM = 122, FoulSideM = 13 };
        // ラインのすぐ外（φ=46°）: ポールより手前の壁は遠い＝ポールの距離で頭打ち。
        Assert.Equal(95.0 / Math.Cos(1 * Math.PI / 180), field.WallDistance(46 * Math.PI / 180), 6);
        // ライン＋30°（φ=75°）: 壁までは 13 / sin(30°) = 26m。
        Assert.Equal(26.0, field.WallDistance(75 * Math.PI / 180), 6);
        Assert.Equal(26.0, field.WallDistance(-75 * Math.PI / 180), 6);
        // 延長した FenceDistance は 75° で 95+27·cos150°＝71.6m と全く別の値になる（ライン沿い60m等の誤判定の元）。
        Assert.NotEqual(26.0, field.FenceDistance(75 * Math.PI / 180), 1);
        // 壁はラインに近い方向ほど遠い＝単調に減る。
        Assert.True(field.WallDistance(60 * Math.PI / 180) > field.WallDistance(80 * Math.PI / 180));
    }

    [Fact]
    public void FenceDistance_AsymmetricStadium_LeftAndRightDiffer()
    {
        // 広島市民球場（左101/右100）相当
        var field = new FieldGeometry { LeftFenceM = 101, RightFenceM = 100, CenterFenceM = 122 };

        var left = field.FenceDistance(-Deg45 / 2);  // 左中間
        var right = field.FenceDistance(Deg45 / 2);  // 右中間
        Assert.True(left > right, $"左中間({left}) が右中間({right}) より深いはず");
    }

    [Fact]
    public void FenceDistance_SymmetricStadium_MatchesLegacyFormula()
    {
        // 左右同値なら旧式 R = Line + (Center-Line)·cos(2φ) と一致（後方互換）
        var field = new FieldGeometry { LeftFenceM = 95, RightFenceM = 95, CenterFenceM = 118 };
        foreach (var phi in new[] { -Deg45, -0.3, 0.0, 0.2, Deg45 })
        {
            var expected = 95 + (118 - 95) * Math.Cos(2 * phi);
            Assert.Equal(expected, field.FenceDistance(phi), 6);
        }
    }

    [Fact]
    public void LineFenceM_IsAverageOfLeftAndRight()
    {
        var field = new FieldGeometry { LeftFenceM = 101, RightFenceM = 100 };
        Assert.Equal(100.5, field.LineFenceM, 6);
    }

    [Fact]
    public void DefaultGeometry_UnchangedFromBaseline()
    {
        // 既定球場は 95/95/118/4.0m を維持（既存 balance-targets の帯を壊さない）
        var field = new FieldGeometry();
        Assert.Equal(95, field.LeftFenceM, 6);
        Assert.Equal(95, field.RightFenceM, 6);
        Assert.Equal(118, field.CenterFenceM, 6);
        Assert.Equal(4.0, field.FenceHeightM, 6);
    }

    /// <summary>
    /// 一塁手・三塁手の標準守備位置を固定する（issue #1640）。座標を動かしたら気づけるように、
    /// 値そのものに加え「ファウルラインへ旧値より寄っている」ことも確認する。
    /// 実際の野球の標準守備位置の目安（10〜15ft≒3.0〜4.6m）までは寄せ切れていない＝
    /// Heavy実測で夏場の得点/チーム帯の上限に当たったため、帯に収まる範囲で寄せた値
    /// （旧値 16.2ft→15.8ft・隙間0.75m→0.61m。残りは docs/design/OPEN-QUESTIONS.md 参照）。
    /// 本塁からの距離は左右対称・ほぼ不変＝29.4m前後。
    /// </summary>
    [Fact]
    public void StandardPosition_CornerInfielders_AreNearTheFoulLine()
    {
        var (firstX, firstZ) = FieldGeometry.StandardPosition(FieldPosition.FirstBase);
        var (thirdX, thirdZ) = FieldGeometry.StandardPosition(FieldPosition.ThirdBase);

        Assert.Equal(17.2, firstX, 6);
        Assert.Equal(24.0, firstZ, 6);
        Assert.Equal(-17.2, thirdX, 6);
        Assert.Equal(24.0, thirdZ, 6);

        // 左右対称（三塁手は一塁手のX反転）。
        Assert.Equal(firstX, -thirdX, 6);
        Assert.Equal(firstZ, thirdZ, 6);

        // ファウルライン（原点を通る45°線 x=z）からの垂直距離。旧値(4.95m=16.2ft)より線寄りで、
        // 実際の野球の目安（10〜15ft≒3.0〜4.6m）に近づいた側にあること。
        var perpFromLine = Math.Abs(firstX - firstZ) / Math.Sqrt(2.0);
        Assert.InRange(perpFromLine, 4.6, 4.95);

        // 本塁からの距離は塁間(27.43m)よりやや深い程度で、従来の29.4m付近を維持。
        var radius = Math.Sqrt(firstX * firstX + firstZ * firstZ);
        Assert.InRange(radius, 28.0, 31.0);
    }
}
