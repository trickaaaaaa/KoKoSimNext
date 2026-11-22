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

    /// <summary>
    /// 北海道は10支部（DistrictId 0-9）が正で、夏の南北はそこからの多対1写像で決まる
    /// （支部が正・南北はそこから導出、design-05 §2.2）。範囲外・未設定は0番目（北北海道）に倒す。
    /// 出典どおりの内訳: 北北海道＝旭川(1)・空知(5)・名寄(6)・北見(7)・十勝(8)・釧根(9)。
    /// 南北海道＝札幌(0)・函館(2)・室蘭(3)・小樽(4)。
    /// </summary>
    [Theory]
    [InlineData(0, 1, "南北海道")]    // 札幌
    [InlineData(1, 0, "北北海道")]    // 旭川
    [InlineData(2, 1, "南北海道")]    // 函館
    [InlineData(9, 0, "北北海道")]    // 釧根
    [InlineData(99, 0, "北北海道")]   // 範囲外は0番目に倒す
    [InlineData(null, 0, "北北海道")] // 未設定（null）は0番目に倒す
    public void RegionFor_Hokkaido_UsesBranchLookup(int? districtId, int expectedSplit, string expectedName)
    {
        var prefs = MakeNation().Prefectures;
        var region = SummerRegions.RegionFor(prefs, prefectureId: 0, districtId);
        Assert.Equal(0, region.PrefectureId);
        Assert.Equal(expectedSplit, region.Split);
        Assert.Equal(expectedName, region.Name);
    }

    /// <summary>
    /// 支部（DistrictId）を持つ北海道の AI校は、校Id の偶奇でなく支部から南北が決まる
    /// （自校の <see cref="SummerRegions.RegionFor"/> と同じ規則＝札幌支部の AI校が北北海道に出ない・issue #1217）。
    /// </summary>
    [Fact]
    public void Contains_HokkaidoSchoolWithDistrict_FollowsBranchNotIdParity()
    {
        var prefs = MakeNation().Prefectures;
        var regions = SummerRegions.Build(prefs);
        var north = regions.First(r => r.PrefectureId == 0 && r.Split == 0);
        var south = regions.First(r => r.PrefectureId == 0 && r.Split == 1);

        // Id 偶奇なら北だが、札幌支部(0)＝南。
        var sapporoEven = new School { Id = 100, Name = "札幌偶", PrefectureId = 0, DistrictId = 0, Strength = 50 };
        Assert.False(north.Contains(sapporoEven));
        Assert.True(south.Contains(sapporoEven));
        Assert.Equal("南北海道", SummerRegions.NameOf(prefs, sapporoEven));

        // Id 奇数なら南だが、旭川支部(1)＝北。
        var asahikawaOdd = new School { Id = 101, Name = "旭川奇", PrefectureId = 0, DistrictId = 1, Strength = 50 };
        Assert.True(north.Contains(asahikawaOdd));
        Assert.False(south.Contains(asahikawaOdd));
        Assert.Equal("北北海道", SummerRegions.NameOf(prefs, asahikawaOdd));
    }

    /// <summary>北海道の全支部で、AI校の所属区画が RegionFor（自校の規則）と一致する。</summary>
    [Fact]
    public void Contains_HokkaidoAllBranches_AgreesWithRegionFor()
    {
        var prefs = MakeNation().Prefectures;
        var regions = SummerRegions.Build(prefs);
        for (var d = 0; d < SummerRegions.HokkaidoBranchNames.Count; d++)
        foreach (var id in new[] { 200, 201 })
        {
            var school = new School { Id = id, Name = "x", PrefectureId = 0, DistrictId = d, Strength = 50 };
            var expected = SummerRegions.RegionFor(prefs, 0, d);
            Assert.Equal(expected, regions.Single(r => r.Contains(school)));
        }
    }

    /// <summary>支部が付いていない（地区データ無しの組み立て）北海道の AI校は従来どおり Id 偶奇で割る。</summary>
    [Fact]
    public void Contains_HokkaidoSchoolWithoutDistrict_FallsBackToIdParity()
    {
        var prefs = MakeNation().Prefectures;
        var regions = SummerRegions.Build(prefs);
        var even = new School { Id = 100, Name = "a", PrefectureId = 0, Strength = 50 };
        Assert.Equal("北北海道", SummerRegions.NameOf(prefs, even));
        Assert.Contains(even, new[] { even }.Where(regions.First(r => r.PrefectureId == 0 && r.Split == 0).Contains));
    }

    /// <summary>東京は秋季側に地区データを持たないので、従来どおり DistrictId の偶奇で決める。</summary>
    [Theory]
    [InlineData(0, 0, "東東京")]
    [InlineData(1, 1, "西東京")]
    [InlineData(2, 0, "東東京")]
    [InlineData(null, 0, "東東京")]
    public void RegionFor_Tokyo_UsesDistrictIdParity(int? districtId, int expectedSplit, string expectedName)
    {
        var prefs = MakeNation().Prefectures;
        var region = SummerRegions.RegionFor(prefs, prefectureId: 12, districtId);
        Assert.Equal(12, region.PrefectureId);
        Assert.Equal(expectedSplit, region.Split);
        Assert.Equal(expectedName, region.Name);
    }

    /// <summary>
    /// 新設校スタート（issue #585系）の地区選択向け。秋季側に地区データを持たない東京だけが2択を返す。
    /// 北海道は秋季側（10支部・data/pref-formats/hokkaido.yaml）が正になったので空を返す
    /// （<see cref="HokkaidoBranchNames_MatchesHokkaidoYamlOrder"/> 参照）。
    /// </summary>
    [Theory]
    [InlineData(0, new string[0])]
    [InlineData(12, new[] { "東東京", "西東京" })]
    [InlineData(20, new string[0])]
    public void SplitDistrictNames_OnlyTokyoHasTwoNamedDistricts(int prefectureId, string[] expected)
    {
        Assert.Equal(expected, SummerRegions.SplitDistrictNames(prefectureId));
    }

    /// <summary>新規開始画面（issue #1334）：夏が2分割の東京・北海道だけが夏の区画名の2択を返す。</summary>
    [Theory]
    [InlineData(0, new[] { "北北海道", "南北海道" })]
    [InlineData(12, new[] { "東東京", "西東京" })]
    [InlineData(20, new string[0])]
    public void NewGameSplitNames_TokyoAndHokkaido(int prefectureId, string[] expected)
    {
        Assert.Equal(expected, SummerRegions.NewGameSplitNames(prefectureId));
    }

    /// <summary>選んだ南北→自校の DistrictId→RegionFor で、選んだ夏の区画に必ず戻る（北海道は代表支部）。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(12, 0)]
    [InlineData(12, 1)]
    public void NewGameDistrictId_RoundTripsToChosenRegion(int prefectureId, int split)
    {
        var prefs = MakeNation().Prefectures;
        var districtId = SummerRegions.NewGameDistrictId(prefectureId, split);
        Assert.Equal(split, SummerRegions.RegionFor(prefs, prefectureId, districtId).Split);
    }

    /// <summary>北海道の代表支部は北北海道=旭川(1)・南北海道=札幌(0)（秋季の支部番号）。</summary>
    [Fact]
    public void NewGameDistrictId_Hokkaido_RepresentativeBranches()
    {
        Assert.Equal("旭川", SummerRegions.HokkaidoBranchNames[SummerRegions.NewGameDistrictId(0, 0)]);
        Assert.Equal("札幌", SummerRegions.HokkaidoBranchNames[SummerRegions.NewGameDistrictId(0, 1)]);
    }

    /// <summary>北海道の支部名10件（DistrictId順）。data/pref-formats/hokkaido.yaml の districts: と同じ並び。</summary>
    [Fact]
    public void HokkaidoBranchNames_MatchesHokkaidoYamlOrder()
    {
        Assert.Equal(
            new[] { "札幌", "旭川", "函館", "室蘭", "小樽", "空知", "名寄", "北見", "十勝", "釧根" },
            SummerRegions.HokkaidoBranchNames);
    }

    /// <summary>自校の区画（DistrictId基準）と、その区画の Entrants（Id偶奇基準）は独立に決まる。
    /// つまり自校は Entrants に含まれない＝呼び出し側（TournamentEntry.BuildField）が別途足す契約。</summary>
    [Fact]
    public void RegionFor_ManagerRegionEntrants_DoesNotIncludeTheManagerItself()
    {
        var nation = MakeNation();
        var manager = new School { Id = -1, Name = "自校", PrefectureId = 0, DistrictId = 0, Strength = 50 };  // 0=札幌(南)
        var region = SummerRegions.RegionFor(nation.Prefectures, manager.PrefectureId, manager.DistrictId);
        Assert.Equal("南北海道", region.Name);
        Assert.DoesNotContain(SummerRegions.Entrants(nation, region), s => s.Id == manager.Id);
    }
}
