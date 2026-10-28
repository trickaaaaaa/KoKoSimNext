using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 成長イベントの年間発生回数（issue #694）。1校（SeasonEngine の自校ロスター）を40年回して、
/// 上昇イベントが「たまに起きる福音」の頻度に収まっていることを固定する。
/// 2026-10-28 実測（seed 7）: 覚醒 1.7／大覚醒 0.13／伸びしろアップ 0.97／新球種 1.6／開眼 4.5（回/年）。
/// </summary>
public sealed class GrowthEventRateTests
{
    [Fact]
    public void UpwardEvents_FireAtTheIntendedYearlyRates()
    {
        const int years = 40;
        var counts = new Dictionary<GrowthEventType, int>();
        var ctx = new SeasonContext
        {
            GrowthObserver = n => counts[n.Type] = counts.TryGetValue(n.Type, out var v) ? v + 1 : 1,
        };
        SeasonEngine.Run(years, ctx, new Xoshiro256Random(7));

        double PerYear(params GrowthEventType[] types)
        {
            var sum = 0;
            foreach (var t in types) sum += counts.TryGetValue(t, out var v) ? v : 0;
            return sum / (double)years;
        }

        Assert.InRange(PerYear(GrowthEventType.Awakening), 1.0, 3.0);
        Assert.InRange(PerYear(GrowthEventType.GrandAwakening), 0.05, 0.3);
        Assert.InRange(PerYear(GrowthEventType.GrowthSurge), 0.5, 1.5);
        Assert.InRange(PerYear(GrowthEventType.NewPitch, GrowthEventType.PitchRefined), 0.8, 2.5);
    }
}
