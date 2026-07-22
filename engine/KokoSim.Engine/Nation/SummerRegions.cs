using System.Collections.Generic;
using System.Linq;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 夏の地方大会の1区画（設計書05 §1.1: 49地方=北海道2・東京2, OPEN-QUESTIONS Q5 / issue #65 決定A-b）。
/// 都道府県モデル（47, <see cref="Prefecture"/>）自体は変えず、夏の予選単位だけ北海道・東京を2つに割る
/// 仮想区分。秋季・センバツ選考など他の大会フローは従来どおり47都道府県のまま
/// （<c>single_prefecture</c>, data/pref-formats/regional-tournaments.yaml）で、この型とは無関係。
/// </summary>
public sealed record SummerRegion(int PrefectureId, string Name, int? Split)
{
    /// <summary>学校がこの区画に属するか。Split未設定の区画は県内全校、設定時は Id 偶奇で2分割
    /// （GroupSplit.cs の地理未設定フォールバックと同じ規約。地理的な意味は持たない機械的な等分）。</summary>
    public bool Contains(School school)
        => school.PrefectureId == PrefectureId && (Split is not { } s || school.Id % 2 == s);
}

/// <summary>49の夏の地方大会区画を組み立てる（設計書05 §1.1）。</summary>
public static class SummerRegions
{
    // JIS順Id（data/prefectures.yaml）。
    private const int HokkaidoId = 0;
    private const int TokyoId = 12;

    public static IReadOnlyList<SummerRegion> Build(IReadOnlyList<Prefecture> prefectures)
    {
        var regions = new List<SummerRegion>(49);
        foreach (var pref in prefectures)
        {
            if (pref.Id == HokkaidoId)
            {
                regions.Add(new SummerRegion(pref.Id, "北北海道", 0));
                regions.Add(new SummerRegion(pref.Id, "南北海道", 1));
            }
            else if (pref.Id == TokyoId)
            {
                regions.Add(new SummerRegion(pref.Id, "東東京", 0));
                regions.Add(new SummerRegion(pref.Id, "西東京", 1));
            }
            else
            {
                regions.Add(new SummerRegion(pref.Id, pref.Name, null));
            }
        }
        return regions;
    }

    /// <summary>この区画の参加校（Nation.InPrefecture を区画の Contains でさらに絞る）。</summary>
    public static IEnumerable<School> Entrants(Nation nation, SummerRegion region)
        => nation.InPrefecture(region.PrefectureId).Where(region.Contains);
}
