using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation;
using Xunit;
using NationModel = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>夏の地方大会49区画（北海道2・東京2, 設計書05 §1.1 / issue #65）のテスト。</summary>
public sealed class SummerRegionsTests
{
    private static NationModel MakeNation()
    {
        var prefs = Enumerable.Range(0, 47)
            .Select(i => new Prefecture(i, $"県{i}", 0))
            .ToList();
        var schools = new List<School>();
        var id = 0;
        // 北海道(0)=9校・東京(12)=11校（奇数で±1丸めも検証）、他は2校ずつ。
        foreach (var pref in prefs)
        {
            var count = pref.Id == 0 ? 9 : pref.Id == 12 ? 11 : 2;
            for (var s = 0; s < count; s++)
            {
                schools.Add(new School { Id = id++, Name = $"{pref.Id}-{s}", PrefectureId = pref.Id, Strength = 50 });
            }
        }
        return new NationModel(prefs, schools);
    }

    [Fact]
    public void Build_Produces49Regions()
    {
        var regions = SummerRegions.Build(MakeNation().Prefectures);
        Assert.Equal(49, regions.Count);
    }

    [Fact]
    public void Build_SplitsHokkaidoAndTokyoWithRealNames()
    {
        var regions = SummerRegions.Build(MakeNation().Prefectures);

        var hokkaido = regions.Where(r => r.PrefectureId == 0).ToList();
        Assert.Equal(2, hokkaido.Count);
        Assert.Equal(new[] { "北北海道", "南北海道" }, hokkaido.Select(r => r.Name));
        Assert.Equal(new int?[] { 0, 1 }, hokkaido.Select(r => r.Split));

        var tokyo = regions.Where(r => r.PrefectureId == 12).ToList();
        Assert.Equal(2, tokyo.Count);
        Assert.Equal(new[] { "東東京", "西東京" }, tokyo.Select(r => r.Name));
        Assert.Equal(new int?[] { 0, 1 }, tokyo.Select(r => r.Split));

        // それ以外の45県は分割なし・元の県名のまま（秋季・センバツ等ほかの大会フローは47県のまま不変）。
        var others = regions.Where(r => r.PrefectureId != 0 && r.PrefectureId != 12).ToList();
        Assert.Equal(45, others.Count);
        Assert.All(others, r => Assert.Null(r.Split));
        Assert.Equal(others.Select(r => r.PrefectureId), others.Select(r => r.PrefectureId).Distinct());
    }

    [Fact]
    public void Entrants_SplitIsDisjointAndCoversAllSchools()
    {
        var nation = MakeNation();
        var regions = SummerRegions.Build(nation.Prefectures);

        foreach (var prefId in new[] { 0, 12 })
        {
            var whole = nation.InPrefecture(prefId).Select(s => s.Id).OrderBy(x => x).ToList();
            var half0 = SummerRegions.Entrants(nation, regions.First(r => r.PrefectureId == prefId && r.Split == 0))
                .Select(s => s.Id).ToList();
            var half1 = SummerRegions.Entrants(nation, regions.First(r => r.PrefectureId == prefId && r.Split == 1))
                .Select(s => s.Id).ToList();

            Assert.Empty(half0.Intersect(half1));
            Assert.Equal(whole, half0.Concat(half1).OrderBy(x => x).ToList());
            Assert.True(System.Math.Abs(half0.Count - half1.Count) <= 1, "50/50split should be within ±1");
        }
    }

    [Fact]
    public void Entrants_NonSplitRegion_MatchesWholePrefecture()
    {
        var nation = MakeNation();
        var region = SummerRegions.Build(nation.Prefectures).First(r => r.PrefectureId == 20);
        var entrants = SummerRegions.Entrants(nation, region).Select(s => s.Id).ToList();
        var whole = nation.InPrefecture(20).Select(s => s.Id).ToList();
        Assert.Equal(whole, entrants);
    }
}
