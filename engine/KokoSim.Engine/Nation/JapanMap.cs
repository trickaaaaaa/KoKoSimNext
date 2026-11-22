using System;
using System.Collections.Generic;
using System.Globalization;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 県選択画面（issue #1410）の日本地図の1区画。全国図では1県（<see cref="Split"/>＝null）、
/// 北海道・東京の拡大図では夏の区画（0=北北海道/東東京, 1=南北海道/西東京。<see cref="SummerRegions"/> の Split と同じ並び）。
/// 輪郭は外周と穴をまとめた頂点列のリストで、塗りも点内判定も偶奇規則。
/// </summary>
public sealed class MapFeature
{
    public int PrefectureId { get; }
    public int? Split { get; }
    public string Name { get; }
    public IReadOnlyList<int[]> Rings { get; }

    public MapFeature(int prefectureId, int? split, string name, IReadOnlyList<int[]> rings)
    {
        PrefectureId = prefectureId;
        Split = split;
        Name = name;
        Rings = rings;
    }

    /// <summary>点（view の座標）がこの区画の中か。偶奇規則（穴は外）。</summary>
    public bool Contains(double x, double y)
    {
        var inside = false;
        foreach (var r in Rings)
        {
            var n = r.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = r[i * 2], yi = r[i * 2 + 1], xj = r[j * 2], yj = r[j * 2 + 1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
        }
        return inside;
    }
}

/// <summary>地図の1枚（全国図／北海道の拡大図／東京の拡大図）。</summary>
public sealed class MapView
{
    public string Id { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>沖縄を寄せた枠（x, y, w, h）。全国図だけが持つ。枠の中はすべて沖縄＝小さな島を狙わなくても押せる。</summary>
    public int[]? Frame { get; }

    public IReadOnlyList<MapFeature> Features { get; }

    public MapView(string id, int width, int height, int[]? frame, IReadOnlyList<MapFeature> features)
    {
        Id = id;
        Width = width;
        Height = height;
        Frame = frame;
        Features = features;
    }

    /// <summary>
    /// 点（view の座標）の下にある区画。無ければ null。輪郭の中に入っている区画だけを返す
    /// （隣へ丸めない）。全国図で沖縄の枠の中は沖縄にする。
    /// </summary>
    public MapFeature? HitTest(double x, double y)
    {
        foreach (var f in Features)
            if (f.Contains(x, y)) return f;
        if (Frame is { } fr && x >= fr[0] && x <= fr[0] + fr[2] && y >= fr[1] && y <= fr[1] + fr[3])
        {
            foreach (var f in Features)
                if (f.PrefectureId == OkinawaId) return f;
        }
        return null;
    }

    internal const int OkinawaId = 46;
}

/// <summary>
/// 日本地図データ（data/japan-map.yaml・tools/gen-japan-map.py の生成物）。
/// 全国図の1県が 県Id（JIS順の添字・<see cref="Prefecture.Id"/>）、拡大図の区画が県Id＋Split。
/// 新規ゲームでは地図で選んだ区画を、既存の地区欄と同じ <c>NewGameSplitNames</c> の添字としてそのまま使う
/// （対応表を別に持たない）。
/// </summary>
public sealed class JapanMap
{
    public MapView National { get; }
    public MapView Hokkaido { get; }
    public MapView Tokyo { get; }

    public JapanMap(MapView national, MapView hokkaido, MapView tokyo)
    {
        National = national;
        Hokkaido = hokkaido;
        Tokyo = tokyo;
    }

    /// <summary>県Id に拡大図があればそれ（北海道・東京）。無ければ null。</summary>
    public MapView? ZoomOf(int prefectureId) => prefectureId switch
    {
        SummerRegions.HokkaidoId => Hokkaido,
        SummerRegions.TokyoId => Tokyo,
        _ => null,
    };
}

/// <summary>data/japan-map.yaml を <see cref="JapanMap"/> へ変換する純パーサ（IO なし）。生成物の行形式だけを読む。</summary>
public static class JapanMapParser
{
    /// <summary>読めない（view 欠け・県が47そろわない等）ときは null。</summary>
    public static JapanMap? Parse(string yaml)
    {
        var views = new Dictionary<string, ViewBuilder>(StringComparer.Ordinal);
        ViewBuilder? view = null;
        FeatureBuilder? feature = null;

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            var indent = line.Length - trimmed.Length;

            if (indent == 2 && trimmed.StartsWith("- id:", StringComparison.Ordinal))
            {
                view = new ViewBuilder(trimmed.Substring(5).Trim());
                views[view.Id] = view;
                feature = null;
            }
            else if (view == null) continue;
            else if (indent == 4 && trimmed.StartsWith("size:", StringComparison.Ordinal))
            {
                var n = Numbers(trimmed.Substring(5));
                if (n.Length == 2) { view.Width = n[0]; view.Height = n[1]; }
            }
            else if (indent == 4 && trimmed.StartsWith("frame:", StringComparison.Ordinal))
            {
                var n = Numbers(trimmed.Substring(6));
                if (n.Length == 4) view.Frame = n;
            }
            else if (indent == 6 && trimmed.StartsWith("- id:", StringComparison.Ordinal))
            {
                if (!int.TryParse(trimmed.Substring(5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
                feature = new FeatureBuilder(id);
                view.Features.Add(feature);
            }
            else if (feature != null && indent == 8 && trimmed.StartsWith("split:", StringComparison.Ordinal))
            {
                if (int.TryParse(trimmed.Substring(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) feature.Split = s;
            }
            else if (feature != null && indent == 8 && trimmed.StartsWith("name:", StringComparison.Ordinal))
            {
                feature.Name = trimmed.Substring(5).Trim();
            }
            else if (feature != null && indent == 10 && trimmed.StartsWith("- \"", StringComparison.Ordinal))
            {
                var body = trimmed.Substring(3).TrimEnd('"');
                var n = Numbers(body);
                if (n.Length >= 6 && n.Length % 2 == 0) feature.Rings.Add(n);
            }
        }

        if (!views.TryGetValue("national", out var nat) || !views.TryGetValue("hokkaido", out var hk)
            || !views.TryGetValue("tokyo", out var tk)) return null;
        if (nat.Features.Count != 47) return null;

        return new JapanMap(nat.Build(), hk.Build(), tk.Build());
    }

    private static int[] Numbers(string s)
    {
        var parts = s.Split(new[] { ' ', ',', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        var res = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            res[i] = int.Parse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture);
        return res;
    }

    private sealed class ViewBuilder
    {
        public readonly string Id;
        public int Width, Height;
        public int[]? Frame;
        public readonly List<FeatureBuilder> Features = new();
        public ViewBuilder(string id) { Id = id; }

        public MapView Build()
        {
            var list = new List<MapFeature>(Features.Count);
            foreach (var f in Features) list.Add(new MapFeature(f.Id, f.Split, f.Name, f.Rings));
            return new MapView(Id, Width, Height, Frame, list);
        }
    }

    private sealed class FeatureBuilder
    {
        public readonly int Id;
        public int? Split;
        public string Name = "";
        public readonly List<int[]> Rings = new();
        public FeatureBuilder(int id) { Id = id; }
    }
}
