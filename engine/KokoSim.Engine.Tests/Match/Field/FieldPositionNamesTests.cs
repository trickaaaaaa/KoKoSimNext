using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

public class FieldPositionNamesTests
{
    [Fact]
    public void AllPositions_HaveDistinctNonEmptyNamesInEveryStyle()
    {
        var all = System.Enum.GetValues<FieldPosition>();
        foreach (var style in new System.Func<FieldPosition, string>[]
                 { FieldPositionNames.Katakana, FieldPositionNames.Kanji2, FieldPositionNames.Abbr })
        {
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (var p in all)
            {
                var s = style(p);
                Assert.False(string.IsNullOrEmpty(s));
                Assert.True(names.Add(s), $"duplicate name for {p}: {s}");
            }
        }
    }

    [Fact]
    public void KnownValues()
    {
        Assert.Equal("ショート", FieldPositionNames.Katakana(FieldPosition.Shortstop));
        Assert.Equal("遊撃", FieldPositionNames.Kanji2(FieldPosition.Shortstop));
        Assert.Equal("遊", FieldPositionNames.Abbr(FieldPosition.Shortstop));
        Assert.Equal("指名打者", FieldPositionNames.Katakana(FieldPosition.DesignatedHitter));
        Assert.Equal("指", FieldPositionNames.Abbr(FieldPosition.DesignatedHitter));
    }

    [Fact]
    public void UnknownValue_FallsBack()
    {
        var bad = (FieldPosition)99;
        Assert.Equal("", FieldPositionNames.Katakana(bad));
        Assert.Equal("", FieldPositionNames.Kanji2(bad));
        Assert.Equal("—", FieldPositionNames.Abbr(bad));
    }
}
