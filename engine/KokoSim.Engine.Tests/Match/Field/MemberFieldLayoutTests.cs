using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

/// <summary>
/// メンバー画面の球場図の固定幾何・守備位置の検算（表示専用ジオメトリ）。
/// 内野の5点は塁間27.4mの正方形、マウンドは本塁—二塁線の約47%地点、
/// 一・三塁手はファウルラインから2m以上内側に立つ、を保証する。
/// </summary>
public sealed class MemberFieldLayoutTests
{
    [Fact]
    public void BasePaths_AreAll27_4mWithin0_1()
    {
        Assert.Equal(27.4, MemberFieldLayout.Distance(MemberFieldLayout.Home, MemberFieldLayout.First), 1);
        Assert.Equal(27.4, MemberFieldLayout.Distance(MemberFieldLayout.First, MemberFieldLayout.Second), 1);
        Assert.Equal(27.4, MemberFieldLayout.Distance(MemberFieldLayout.Second, MemberFieldLayout.Third), 1);
        Assert.Equal(27.4, MemberFieldLayout.Distance(MemberFieldLayout.Third, MemberFieldLayout.Home), 1);
    }

    [Fact]
    public void SecondBase_IsHomeToSecondDiagonal_38_8m()
    {
        // 本塁—二塁は塁間の√2倍（対角）。
        Assert.Equal(38.8, MemberFieldLayout.Distance(MemberFieldLayout.Home, MemberFieldLayout.Second), 1);
    }

    [Fact]
    public void Mound_IsAbout47PercentAlongHomeToSecond()
    {
        var ratio = MemberFieldLayout.Mound.Y / MemberFieldLayout.Second.Y;
        Assert.InRange(ratio, 0.45, 0.49); // 18.4 / 38.8 ≈ 0.474
    }

    [Fact]
    public void CornerInfielders_AreAtLeast2mInsideFoulLine()
    {
        var first = MemberFieldLayout.DefensivePosition(3);
        var third = MemberFieldLayout.DefensivePosition(5);
        Assert.True(MemberFieldLayout.InsideFoulLineM(first, firstBaseSide: true) >= 2.0,
            "一塁手はファウルラインから2m以上内側に立つこと");
        Assert.True(MemberFieldLayout.InsideFoulLineM(third, firstBaseSide: false) >= 2.0,
            "三塁手はファウルラインから2m以上内側に立つこと");
    }

    [Fact]
    public void Catcher_IsJustBehindHome()
    {
        var catcher = MemberFieldLayout.DefensivePosition(2);
        Assert.True(catcher.Y < 0, "捕手は本塁後方（Y<0）");
        Assert.True(MemberFieldLayout.Distance(catcher, MemberFieldLayout.Home) < 3.0, "捕手は本塁直後（3m以内）");
    }

    [Fact]
    public void MiddleInfielders_AreDeeperThanSecondBase()
    {
        var second = MemberFieldLayout.DefensivePosition(4);
        var shortstop = MemberFieldLayout.DefensivePosition(6);
        Assert.True(second.Y > MemberFieldLayout.Second.Y, "二塁手は二塁ベースより深い（Y大）");
        Assert.True(shortstop.Y > MemberFieldLayout.Second.Y, "遊撃手は二塁ベースより深い（Y大）");
    }

    [Theory]
    [InlineData(7)] // 左
    [InlineData(8)] // 中
    [InlineData(9)] // 右
    public void Outfielders_AreBeyondTwoThirdsToFence(int uniform)
    {
        var p = MemberFieldLayout.DefensivePosition(uniform);
        var radial = MemberFieldLayout.Distance(MemberFieldLayout.Home, p);
        var fence = MemberFieldLayout.FenceRadiusM(MemberFieldLayout.BearingRad(p));
        Assert.True(radial / fence >= 2.0 / 3.0, $"外野手はフェンスまでの2/3より奥（実測 {radial / fence:P0}）");
    }
}
