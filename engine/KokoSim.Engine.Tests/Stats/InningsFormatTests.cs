using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Stats;

/// <summary>投球回の表記と、UI が端数だけ小さく組むための整数部／端数の分割。</summary>
public sealed class InningsFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "0 1/3")]
    [InlineData(2, "0 2/3")]
    [InlineData(3, "1")]
    [InlineData(16, "5 1/3")]
    [InlineData(26, "8 2/3")]
    public void Text_FormatsOutsAsInnings(int outs, string expected)
        => Assert.Equal(expected, InningsFormat.Text(outs));

    [Fact]
    public void PitchingLine_And_StatLine_UseTheSameText()
    {
        var line = new PitchingLine("P", 16, 0, 0, 0, 0, 0, 0);
        Assert.Equal("5 1/3", line.InningsText);
    }

    [Theory]
    [InlineData("5 1/3", "5", "1/3")]
    [InlineData("12 2/3", "12", "2/3")]
    [InlineData("0 1/3", "0", "1/3")]
    [InlineData("7", "7", "")]
    [InlineData("—", "—", "")]
    [InlineData("", "", "")]
    [InlineData("0.45", "0.45", "")]
    [InlineData("5 1/4", "5 1/4", "")]
    [InlineData("1/3", "1/3", "")]
    public void Split_SeparatesWholeFromFraction(string text, string whole, string fraction)
        => Assert.Equal((whole, fraction), InningsFormat.Split(text));

    [Fact]
    public void Split_NullIsEmpty() => Assert.Equal(("", ""), InningsFormat.Split(null));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(27)]
    public void Split_RoundTripsText(int outs)
    {
        var (whole, frac) = InningsFormat.Split(InningsFormat.Text(outs));
        Assert.Equal((outs / 3).ToString(), whole);
        Assert.Equal(outs % 3 == 0 ? "" : (outs % 3) + "/3", frac);
    }
}
