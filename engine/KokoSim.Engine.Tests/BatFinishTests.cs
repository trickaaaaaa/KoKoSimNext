using System.Collections.Generic;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests;

public class BatFinishTests
{
    [Fact]
    public void SamePlayerGetsSameFinish()
    {
        for (int id = 0; id < 200; id++)
            Assert.Equal(BatFinishes.For(id, "x"), BatFinishes.For(id, "y"));
        Assert.Equal(BatFinishes.For(null, "山田太郎"), BatFinishes.For(null, "山田太郎"));
    }

    [Fact]
    public void PlayerIdsSpreadOverAllThreeFinishes()
    {
        var counts = new Dictionary<BatFinish, int>();
        for (int id = 1; id <= 3000; id++)
        {
            var f = BatFinishes.For(id, null);
            counts[f] = counts.GetValueOrDefault(f) + 1;
        }
        Assert.Equal(BatFinishes.Count, counts.Count);
        foreach (var kv in counts) Assert.InRange(kv.Value, 800, 1200);
    }

    [Fact]
    public void NamesSpreadWhenIdIsMissing()
    {
        var seen = new HashSet<BatFinish>();
        for (int i = 0; i < 100; i++) seen.Add(BatFinishes.For(null, "選手" + i));
        Assert.Equal(BatFinishes.Count, seen.Count);
    }

    [Fact]
    public void ConsecutiveIdsAreNotAllSameNeighbours()
    {
        int same = 0;
        for (int id = 1; id < 1000; id++)
            if (BatFinishes.For(id, null) == BatFinishes.For(id + 1, null)) same++;
        Assert.InRange(same, 200, 450);   // 独立なら約1/3
    }
}
