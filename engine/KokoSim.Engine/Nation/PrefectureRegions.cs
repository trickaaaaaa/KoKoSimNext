using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Nation;

/// <summary>8地方区分（JIS順の県Idをまとめる）。勧誘の「距離」を測る唯一の物差し（issue #588）。</summary>
public enum JapanRegion
{
    HokkaidoTohoku = 0,
    Kanto = 1,
    Chubu = 2,
    Kinki = 3,
    Chugoku = 4,
    Shikoku = 5,
    Kyushu = 6,
}

/// <summary>
/// 勧誘先までの遠さ（設計書04 §2.2「成功率: 名声 × 距離（寮でカバー） × 競合校の格」）。
/// 実距離[km]は持たない＝県に座標が無いので、地方区分の粗い4段で表す。
/// </summary>
public enum RecruitDistance
{
    /// <summary>自校と同じ県。</summary>
    SamePrefecture = 0,
    /// <summary>同じ地方の別の県。</summary>
    SameRegion = 1,
    /// <summary>隣り合う地方。</summary>
    NeighborRegion = 2,
    /// <summary>それ以外（遠方）。</summary>
    Far = 3,
}

/// <summary>
/// 県Id → 8地方の対応と、地方どうしの隣接（issue #588）。
///
/// <para><b>座標や実距離は持たない。</b> 県に緯度経度を持たせるとデータの正典が増え、
/// 「どこまで正確なら正しいのか」が決まらない。勧誘の距離は成功率の段差にしか使わないので、
/// 地方区分＋隣接という粗い粒度で足りる（設計書04 §2.2）。</para>
///
/// <para>バランス係数ではなく<b>変わらない事実</b>なので、<see cref="PrefectureNames"/> と同じく
/// YAML でなくここが正典でよい。</para>
/// </summary>
public static class PrefectureRegions
{
    // JIS順（0基点）で各地方の最後の県Id。北海道東北 0-6 / 関東 7-13 / 中部 14-22 /
    // 近畿 23-29 / 中国 30-34 / 四国 35-38 / 九州 39-46。
    private static readonly (int LastId, JapanRegion Region)[] Bands =
    {
        (6, JapanRegion.HokkaidoTohoku),
        (13, JapanRegion.Kanto),
        (22, JapanRegion.Chubu),
        (29, JapanRegion.Kinki),
        (34, JapanRegion.Chugoku),
        (38, JapanRegion.Shikoku),
        (46, JapanRegion.Kyushu),
    };

    private static readonly IReadOnlyDictionary<JapanRegion, JapanRegion[]> Neighbors =
        new Dictionary<JapanRegion, JapanRegion[]>
        {
            [JapanRegion.HokkaidoTohoku] = new[] { JapanRegion.Kanto },
            [JapanRegion.Kanto] = new[] { JapanRegion.HokkaidoTohoku, JapanRegion.Chubu },
            [JapanRegion.Chubu] = new[] { JapanRegion.Kanto, JapanRegion.Kinki },
            [JapanRegion.Kinki] = new[] { JapanRegion.Chubu, JapanRegion.Chugoku, JapanRegion.Shikoku },
            [JapanRegion.Chugoku] = new[] { JapanRegion.Kinki, JapanRegion.Shikoku, JapanRegion.Kyushu },
            [JapanRegion.Shikoku] = new[] { JapanRegion.Kinki, JapanRegion.Chugoku, JapanRegion.Kyushu },
            [JapanRegion.Kyushu] = new[] { JapanRegion.Chugoku, JapanRegion.Shikoku },
        };

    /// <summary>県Id（JIS順0基点）の地方。範囲外は近い端へ丸める（落とさない）。</summary>
    public static JapanRegion RegionOf(int prefectureId)
    {
        if (prefectureId < 0) return JapanRegion.HokkaidoTohoku;
        foreach (var b in Bands)
            if (prefectureId <= b.LastId) return b.Region;
        return JapanRegion.Kyushu;
    }

    /// <summary>2つの地方が隣り合うか。</summary>
    public static bool AreNeighbors(JapanRegion a, JapanRegion b)
    {
        if (a == b) return false;
        return Neighbors.TryGetValue(a, out var list) && Array.IndexOf(list, b) >= 0;
    }

    /// <summary>自校の県から見た相手の県の遠さ（設計書04 §2.2）。</summary>
    public static RecruitDistance DistanceBetween(int fromPrefectureId, int toPrefectureId)
    {
        if (fromPrefectureId == toPrefectureId) return RecruitDistance.SamePrefecture;
        var a = RegionOf(fromPrefectureId);
        var b = RegionOf(toPrefectureId);
        if (a == b) return RecruitDistance.SameRegion;
        return AreNeighbors(a, b) ? RecruitDistance.NeighborRegion : RecruitDistance.Far;
    }

    /// <summary>表示名（画面に出す一言）。</summary>
    public static string DisplayName(RecruitDistance d) => d switch
    {
        RecruitDistance.SamePrefecture => "同県",
        RecruitDistance.SameRegion => "同地方",
        RecruitDistance.NeighborRegion => "隣接地方",
        _ => "遠方",
    };
}
