using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 弾道（LaunchTendency 1〜100）→タイプ軸5段の境界値検証（issue #219）。
/// </summary>
public sealed class LaunchTendencyTypesTests
{
    [Theory]
    [InlineData(1, LaunchTendencyType.GroundBall)]
    [InlineData(20, LaunchTendencyType.GroundBall)]
    [InlineData(21, LaunchTendencyType.LeanGround)]
    [InlineData(40, LaunchTendencyType.LeanGround)]
    [InlineData(41, LaunchTendencyType.Liner)]
    [InlineData(60, LaunchTendencyType.Liner)]
    [InlineData(61, LaunchTendencyType.LeanFly)]
    [InlineData(80, LaunchTendencyType.LeanFly)]
    [InlineData(81, LaunchTendencyType.FlyBall)]
    [InlineData(100, LaunchTendencyType.FlyBall)]
    public void FromValue_MapsBoundariesToExpectedType(int lt, LaunchTendencyType expected)
    {
        Assert.Equal(expected, LaunchTendencyTypes.FromValue(lt));
    }
}
