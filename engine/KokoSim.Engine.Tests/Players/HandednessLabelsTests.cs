using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>投打の日本語表記フォーマッタ（Issue #140: 7箇所に散在していた重複の集約先）を検証する。</summary>
public sealed class HandednessLabelsTests
{
    [Theory]
    [InlineData(Handedness.Right, "右投")]
    [InlineData(Handedness.Left, "左投")]
    public void Throws_Label(Handedness throws, string expected)
        => Assert.Equal(expected, HandednessLabels.Throws(throws));

    [Theory]
    [InlineData(Handedness.Right, "右打")]
    [InlineData(Handedness.Left, "左打")]
    [InlineData(Handedness.Switch, "両打")]
    public void Bats_Label(Handedness bats, string expected)
        => Assert.Equal(expected, HandednessLabels.Bats(bats));

    [Theory]
    [InlineData(Handedness.Right, Handedness.Left, "右投左打")]
    [InlineData(Handedness.Left, Handedness.Right, "左投右打")]
    [InlineData(Handedness.Right, Handedness.Switch, "右投両打")]
    public void Combined_Label(Handedness throws, Handedness bats, string expected)
        => Assert.Equal(expected, HandednessLabels.Combined(throws, bats));
}
