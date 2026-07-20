using KokoSim.Engine.Core;
using Xunit;

namespace KokoSim.Engine.Tests.Core;

/// <summary>
/// 決定論RNGの回帰テスト（不変条件#2の常設検証）。
/// 同シード同結果・独立ストリーム・分布の健全性を保証する。
/// </summary>
public sealed class Xoshiro256RandomTests
{
    [Fact]
    public void SameSeed_ProducesIdenticalSequence()
    {
        var a = new Xoshiro256Random(12345);
        var b = new Xoshiro256Random(12345);

        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
        }
    }

    [Fact]
    public void DifferentSeeds_DivergeQuickly()
    {
        var a = new Xoshiro256Random(1);
        var b = new Xoshiro256Random(2);

        var identical = 0;
        for (var i = 0; i < 100; i++)
        {
            if (a.NextUInt64() == b.NextUInt64())
            {
                identical++;
            }
        }

        // 異なるシードで100連続一致はまず起きない。
        Assert.True(identical < 3, $"想定外に一致が多い: {identical}");
    }

    [Fact]
    public void NextDouble_StaysInUnitInterval()
    {
        var rng = new Xoshiro256Random(7);
        for (var i = 0; i < 100_000; i++)
        {
            var d = rng.NextDouble();
            Assert.InRange(d, 0.0, 0.9999999999);
        }
    }

    [Fact]
    public void NextDouble_MeanApproachesHalf()
    {
        var rng = new Xoshiro256Random(99);
        double sum = 0;
        const int n = 200_000;
        for (var i = 0; i < n; i++)
        {
            sum += rng.NextDouble();
        }

        var mean = sum / n;
        Assert.InRange(mean, 0.49, 0.51);
    }

    [Fact]
    public void NextInt_StaysWithinRange()
    {
        var rng = new Xoshiro256Random(3);
        for (var i = 0; i < 100_000; i++)
        {
            var v = rng.NextInt(-5, 10);
            Assert.InRange(v, -5, 9);
        }
    }

    [Fact]
    public void NextInt_InvalidRange_Throws()
    {
        var rng = new Xoshiro256Random(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
    }

    [Fact]
    public void NextGaussian_HasExpectedMoments()
    {
        var rng = new Xoshiro256Random(2024);
        const int n = 500_000;
        double sum = 0;
        double sumSq = 0;
        for (var i = 0; i < n; i++)
        {
            var g = rng.NextGaussian(10.0, 2.0);
            sum += g;
            sumSq += g * g;
        }

        var mean = sum / n;
        var variance = (sumSq / n) - (mean * mean);
        var std = Math.Sqrt(variance);

        Assert.InRange(mean, 9.95, 10.05);
        Assert.InRange(std, 1.95, 2.05);
    }

    [Fact]
    public void Fork_IsDeterministic_AndIndependentFromParent()
    {
        var parentA = new Xoshiro256Random(555);
        var parentB = new Xoshiro256Random(555);

        var forkA = parentA.Fork(7);
        var forkB = parentB.Fork(7);

        // 同じ親状態・同じ streamId の Fork は同一列。
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(forkA.NextUInt64(), forkB.NextUInt64());
        }

        // 異なる streamId は別の列。
        var parentC = new Xoshiro256Random(555);
        var forkDifferent = parentC.Fork(8);
        var identical = 0;
        var forkSame = new Xoshiro256Random(555).Fork(7);
        for (var i = 0; i < 100; i++)
        {
            if (forkDifferent.NextUInt64() == forkSame.NextUInt64())
            {
                identical++;
            }
        }
        Assert.True(identical < 3, $"streamId 違いなのに一致が多い: {identical}");
    }
}
