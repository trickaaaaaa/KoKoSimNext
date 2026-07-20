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
}
