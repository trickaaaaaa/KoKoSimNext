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

    /// <summary>甲子園の出場校一覧が出す「どこの代表か」。分割県は区画名、他は県名。</summary>
    [Fact]
    public void NameOf_ReturnsTheSummerRegionName()
    {
        var nation = MakeNation();
        var prefs = nation.Prefectures;

        var hokkaido = nation.InPrefecture(0).ToList();
        Assert.Equal("北北海道", SummerRegions.NameOf(prefs, hokkaido.First(s => s.Id % 2 == 0)));
        Assert.Equal("南北海道", SummerRegions.NameOf(prefs, hokkaido.First(s => s.Id % 2 == 1)));

        var tokyo = nation.InPrefecture(12).ToList();
        Assert.Equal("東東京", SummerRegions.NameOf(prefs, tokyo.First(s => s.Id % 2 == 0)));
        Assert.Equal("西東京", SummerRegions.NameOf(prefs, tokyo.First(s => s.Id % 2 == 1)));

        Assert.Equal("県20", SummerRegions.NameOf(prefs, nation.InPrefecture(20).First()));
    }

    /// <summary>
    /// どの区画にも属さない学校（自校＝専用IDで Nation に居ない）は県名へ落ちる
    /// ＝呼び出し側で分岐させない。<c>Id % 2</c> が負になる自校 Id(-1) でも落ちない。
    /// </summary>
    [Fact]
    public void NameOf_SchoolOutsideEveryRegion_FallsBackToPrefectureName()
    {
        var nation = MakeNation();
        // 東京(12)は分割県だが、自校 Id(-1) は偶奇のどちらの区画にも入らない＝県名へ落ちる。
        var manager = new School { Id = -1, Name = "桜丘", PrefectureId = 12, Strength = 50 };
        Assert.Equal("県12", SummerRegions.NameOf(nation.Prefectures, manager));

        var unknown = new School { Id = -1, Name = "無所属", PrefectureId = 999, Strength = 50 };
        Assert.Equal("", SummerRegions.NameOf(nation.Prefectures, unknown));
    }

    /// <summary>
    /// 自校（専用IDで Id 偶奇の区画判定が使えない）が属する区画を、県内地区 DistrictId から決める
    /// （issue 北海道・東京分割）。非分割県は DistrictId に関わらずその1区画。
    /// </summary>
    [Fact]
    public void RegionFor_NonSplitPrefecture_IgnoresDistrictId_ReturnsTheOnlyRegion()
    {
        var prefs = MakeNation().Prefectures;
        var region = SummerRegions.RegionFor(prefs, prefectureId: 20, districtId: 7);
        Assert.Equal(20, region.PrefectureId);
        Assert.Null(region.Split);
    }

    [Theory]
    [InlineData(0, 0, "北北海道")]
    [InlineData(1, 1, "南北海道")]
    [InlineData(2, 0, "北北海道")]
    [InlineData(null, 0, "北北海道")]   // 未設定（null）は0区画目に倒す
    public void RegionFor_SplitPrefecture_UsesDistrictIdParity(int? districtId, int expectedSplit, string expectedName)
    {
        var prefs = MakeNation().Prefectures;
        var region = SummerRegions.RegionFor(prefs, prefectureId: 0, districtId);
        Assert.Equal(0, region.PrefectureId);
        Assert.Equal(expectedSplit, region.Split);
        Assert.Equal(expectedName, region.Name);
    }

    /// <summary>自校の区画（DistrictId基準）と、その区画の Entrants（Id偶奇基準）は独立に決まる。
    /// つまり自校は Entrants に含まれない＝呼び出し側（TournamentEntry.BuildField）が別途足す契約。</summary>
    [Fact]
    public void RegionFor_ManagerRegionEntrants_DoesNotIncludeTheManagerItself()
    {
        var nation = MakeNation();
        var manager = new School { Id = -1, Name = "自校", PrefectureId = 0, DistrictId = 1, Strength = 50 };
        var region = SummerRegions.RegionFor(nation.Prefectures, manager.PrefectureId, manager.DistrictId);
        Assert.Equal("南北海道", region.Name);
        Assert.DoesNotContain(SummerRegions.Entrants(nation, region), s => s.Id == manager.Id);
    }
}
