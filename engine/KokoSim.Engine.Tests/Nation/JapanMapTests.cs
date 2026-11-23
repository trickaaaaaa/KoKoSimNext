using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 県選択画面（issue #1410）の日本地図データ（data/japan-map.yaml）と点内判定の検証。
/// 画面に出る47県すべてが「乗せたらその県」と引ける・北海道/東京の区画名が既存の地区欄と一致する、を固定する。
/// 表示専用のデータなので試合結果には触れない＝統計帯には影響しない。
/// </summary>
public sealed class JapanMapTests
{
    private static JapanMap LoadReal()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("japan-map.yaml");
        var map = JapanMapParser.Parse(File.ReadAllText(path));
        Assert.NotNull(map);
        return map!;
    }

    /// <summary>区画の中の点を、輪郭の高さの中ほどで水平線を引いた交点の組から取る（各輪郭の最も広い区間の中点）。</summary>
    private static IEnumerable<(double X, double Y)> InteriorPoints(MapFeature f)
    {
        foreach (var ring in f.Rings)
        {
            var n = ring.Length / 2;
            var minY = double.MaxValue; var maxY = double.MinValue;
            for (var i = 0; i < n; i++) { minY = System.Math.Min(minY, ring[i * 2 + 1]); maxY = System.Math.Max(maxY, ring[i * 2 + 1]); }
            foreach (var t in new[] { 0.35, 0.5, 0.65 })
            {
                var y = minY + (maxY - minY) * t + 0.37;   // 頂点の高さと重ならないようずらす
                var xs = new List<double>();
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = ring[i * 2], yi = ring[i * 2 + 1], xj = ring[j * 2], yj = ring[j * 2 + 1];
                    if ((yi > y) != (yj > y)) xs.Add((xj - xi) * (y - yi) / (yj - yi) + xi);
                }
                xs.Sort();
                var best = -1.0; double bx = 0;
                for (var k = 0; k + 1 < xs.Count; k++)
                {
                    var mid = (xs[k] + xs[k + 1]) / 2;
                    if (!f.Contains(mid, y)) continue;
                    var w = xs[k + 1] - xs[k];
                    if (w > best) { best = w; bx = mid; }
                }
                if (best > 0) yield return (bx, y);
            }
        }
    }

    [Fact]
    public void National_HasEvery47Prefectures_ExactlyOnce()
    {
        var map = LoadReal();
        var ids = map.National.Features.Select(f => f.PrefectureId).OrderBy(i => i).ToList();
        Assert.Equal(Enumerable.Range(0, 47), ids);
        Assert.All(map.National.Features, f => Assert.NotEmpty(f.Rings));
    }

    [Fact]
    public void National_EveryPrefecture_IsPickedByAPointInsideIt()
    {
        var map = LoadReal();
        foreach (var f in map.National.Features)
        {
            var pts = InteriorPoints(f).ToList();
            Assert.True(pts.Count > 0, $"県Id {f.PrefectureId} に内部の点を取れない");
            // 沖縄は縮小して離島ばかりなので、内部の点は枠の外でもよい（枠の中は別テスト）。他県の輪郭と重なって奪われないこと
            foreach (var (x, y) in pts)
            {
                var hit = map.National.HitTest(x, y);
                Assert.True(hit != null && hit.PrefectureId == f.PrefectureId,
                    $"県Id {f.PrefectureId}（{PrefectureNames.JapaneseOf(f.PrefectureId)}）の中の点 ({x:F1},{y:F1}) が {(hit == null ? "どこでもない" : PrefectureNames.JapaneseOf(hit.PrefectureId))} に当たる");
            }
        }
    }

    [Theory]
    [InlineData(36, "香川県")]
    [InlineData(26, "大阪府")]
    [InlineData(12, "東京都")]
    [InlineData(25, "京都府")]
    [InlineData(28, "奈良県")]
    public void National_SmallPrefectures_AreStillPickable(int id, string name)
    {
        var map = LoadReal();
        Assert.Equal(name, PrefectureNames.JapaneseOf(id));
        var f = map.National.Features.Single(x => x.PrefectureId == id);
        var hits = InteriorPoints(f).Select(p => map.National.HitTest(p.X, p.Y)?.PrefectureId).ToList();
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(id, h));
    }

    [Fact]
    public void National_OutsideEverything_HitsNothing()
    {
        var map = LoadReal();
        Assert.Null(map.National.HitTest(-5, -5));
        Assert.Null(map.National.HitTest(map.National.Width + 5, 3));
    }

    [Fact]
    public void National_OkinawaFrame_PicksOkinawaEvenOnWater()
    {
        var map = LoadReal();
        var fr = map.National.Frame;
        Assert.NotNull(fr);
        // 枠の左上の隅（島の無い海）でも沖縄
        var hit = map.National.HitTest(fr![0] + 2, fr[1] + 2);
        Assert.Equal(46, hit?.PrefectureId);
        // 枠は本土の県の輪郭とは重ならない
        foreach (var f in map.National.Features.Where(x => x.PrefectureId != 46))
            foreach (var ring in f.Rings)
                for (var i = 0; i < ring.Length; i += 2)
                    Assert.False(ring[i] >= fr[0] && ring[i] <= fr[0] + fr[2] && ring[i + 1] >= fr[1] && ring[i + 1] <= fr[1] + fr[3],
                        $"県Id {f.PrefectureId} が沖縄の枠に食い込む");
    }

    [Theory]
    [InlineData(SummerRegionsIds.Hokkaido)]
    [InlineData(SummerRegionsIds.Tokyo)]
    public void ZoomViews_AreTwoRegions_NamedLikeTheDistrictField(int prefectureId)
    {
        var map = LoadReal();
        var view = map.ZoomOf(prefectureId);
        Assert.NotNull(view);
        var names = SummerRegions.NewGameSplitNames(prefectureId);
        Assert.Equal(2, names.Count);
        Assert.Equal(2, view!.Features.Count);
        foreach (var f in view.Features)
        {
            Assert.Equal(prefectureId, f.PrefectureId);
            Assert.NotNull(f.Split);
            // 地図の区画名＝既存の地区欄の選択肢名（同じ添字で同じ名前）
            Assert.Equal(names[f.Split!.Value], f.Name);
            // 地区欄の添字→自校の DistrictId の写像は既存のまま（新しい対応表を作らない）
            Assert.True(SummerRegions.NewGameDistrictId(prefectureId, f.Split.Value) >= 0);
        }
        Assert.Equal(new[] { 0, 1 }, view.Features.Select(f => f.Split!.Value).OrderBy(s => s));
    }

    [Theory]
    [InlineData(SummerRegionsIds.Hokkaido)]
    [InlineData(SummerRegionsIds.Tokyo)]
    public void ZoomViews_EveryRegion_IsPickedByAPointInsideIt(int prefectureId)
    {
        var view = LoadReal().ZoomOf(prefectureId)!;
        foreach (var f in view.Features)
        {
            var pts = InteriorPoints(f).ToList();
            Assert.NotEmpty(pts);
            foreach (var (x, y) in pts)
                Assert.Equal(f.Split, view.HitTest(x, y)?.Split);
        }
    }

    [Fact]
    public void ZoomOf_OtherPrefectures_HaveNoZoom()
    {
        var map = LoadReal();
        for (var id = 0; id < 47; id++)
        {
            var expected = id == SummerRegionsIds.Hokkaido || id == SummerRegionsIds.Tokyo;
            Assert.Equal(expected, map.ZoomOf(id) != null);
            // 拡大図を持つ県は、夏の2分割（NewGameSplitNames が2択）の県と一致する
            Assert.Equal(expected, SummerRegions.NewGameSplitNames(id).Count == 2);
        }
    }

    [Fact]
    public void Tokyo_WestIsWestOfEast()
    {
        // 東東京は湾岸（右）・西東京は多摩（左）。区割りの取り違え（東西が逆）を見張る
        var tokyo = LoadReal().Tokyo;
        double Cx(MapFeature f) => InteriorPoints(f).Average(p => p.X);
        var east = tokyo.Features.Single(f => f.Split == 0);
        var west = tokyo.Features.Single(f => f.Split == 1);
        Assert.True(Cx(east) > Cx(west));
    }

    [Fact]
    public void Hokkaido_NorthIsNorthOfSouth()
    {
        var hk = LoadReal().Hokkaido;
        double Cy(MapFeature f) => InteriorPoints(f).Average(p => p.Y);
        var north = hk.Features.Single(f => f.Split == 0);
        var south = hk.Features.Single(f => f.Split == 1);
        Assert.True(Cy(north) < Cy(south));   // y は下向き＝小さいほど北
    }

    [Fact]
    public void Parser_RejectsIncompleteData()
    {
        Assert.Null(JapanMapParser.Parse(""));
        Assert.Null(JapanMapParser.Parse("views:\n  - id: national\n    size: [10, 10]\n"));
    }

    [Fact]
    public void Contains_UsesEvenOdd_HolesAreOutside()
    {
        var f = new MapFeature(0, null, "",
            new List<int[]> { new[] { 0, 0, 100, 0, 100, 100, 0, 100 }, new[] { 40, 40, 60, 40, 60, 60, 40, 60 } });
        Assert.True(f.Contains(10, 10));
        Assert.False(f.Contains(50, 50));
        Assert.False(f.Contains(150, 50));
    }

    /// <summary>
    /// どの輪郭も自分自身と交差しない（単純閉曲線）。偶奇規則の塗りで、自己交差は白い欠けになる
    /// （issue #1499: 東京の拡大図を 89×50 の粗い格子へ丸めて自己交差し、23区側が抜けていた）。
    /// </summary>
    [Fact]
    public void Rings_DoNotSelfIntersect()
    {
        var map = LoadReal();
        foreach (var view in new[] { map.National, map.Hokkaido, map.Tokyo })
            foreach (var f in view.Features)
                foreach (var ring in f.Rings)
                {
                    var n = ring.Length / 2;
                    for (var i = 0; i < n; i++)
                        for (var j = i + 2; j < n; j++)
                        {
                            if (i == 0 && j == n - 1) continue;   // 閉じる辺と最初の辺は隣り合う
                            Assert.False(SegmentsCross(ring, i, (i + 1) % n, j, (j + 1) % n),
                                $"{view.Id} の {f.Name}{f.PrefectureId} で辺 {i} と {j} が交差している");
                        }
                }
    }

    /// <summary>拡大図は全国図より細かい格子で持つ（長い辺がおよそ1500目盛り。issue #1499）。</summary>
    [Fact]
    public void ZoomViews_UseFineGrid()
    {
        var map = LoadReal();
        foreach (var view in new[] { map.Hokkaido, map.Tokyo })
            Assert.True(System.Math.Max(view.Width, view.Height) >= 1000, $"{view.Id} の格子が粗い（{view.Width}×{view.Height}）");
    }

    private static bool SegmentsCross(int[] r, int a0, int a1, int b0, int b1)
    {
        long ax = r[a0 * 2], ay = r[a0 * 2 + 1], bx = r[a1 * 2], by = r[a1 * 2 + 1];
        long cx = r[b0 * 2], cy = r[b0 * 2 + 1], dx = r[b1 * 2], dy = r[b1 * 2 + 1];
        static long Cross(long ox, long oy, long px, long py, long qx, long qy) => (px - ox) * (qy - oy) - (py - oy) * (qx - ox);
        static bool On(long ox, long oy, long px, long py, long qx, long qy)
            => System.Math.Min(ox, px) <= qx && qx <= System.Math.Max(ox, px) && System.Math.Min(oy, py) <= qy && qy <= System.Math.Max(oy, py);
        long d1 = Cross(cx, cy, dx, dy, ax, ay), d2 = Cross(cx, cy, dx, dy, bx, by);
        long d3 = Cross(ax, ay, bx, by, cx, cy), d4 = Cross(ax, ay, bx, by, dx, dy);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        // 接する・重なる（共線）も欠けの元なので交差として扱う
        if (d1 == 0 && On(cx, cy, dx, dy, ax, ay)) return true;
        if (d2 == 0 && On(cx, cy, dx, dy, bx, by)) return true;
        if (d3 == 0 && On(ax, ay, bx, by, cx, cy)) return true;
        if (d4 == 0 && On(ax, ay, bx, by, dx, dy)) return true;
        return false;
    }

    private static class SummerRegionsIds
    {
        public const int Hokkaido = 0;
        public const int Tokyo = 12;
    }
}
