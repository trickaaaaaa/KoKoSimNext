namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 県内地区の割当計画（設計書05 §2.2 / CHANGELOG 28）。地理固定割（district_assignment=geographic）の県について
/// 「県Id → 地区数」を県フォーマットから導く。地区数＝地区予選 group_split ステージの Groups
/// （例: 神奈川=4 / 兵庫=5。districts 一覧の名称数ではなく運用ブロック数に合わせる）。
/// NationGenerator に渡すと学校へ DistrictId が付与され、group_split の地理割が実際に効く。
/// </summary>
public static class DistrictPlan
{
    public static IReadOnlyDictionary<int, int> Build(
        PrefectureTable prefTable, IReadOnlyDictionary<string, PrefFormat> prefFormats)
    {
        var map = new Dictionary<int, int>();
        foreach (var fmt in prefFormats.Values)
        {
            if (fmt.DistrictAssignment != DistrictAssignment.Geographic) continue;
            var count = GeographicGroupCount(fmt);
            if (count <= 0) continue;
            var info = prefTable.Prefectures.FirstOrDefault(p => p.Name == fmt.Pref);
            if (info is not null) map[info.Id] = count;
        }
        return map;
    }

    /// <summary>地区予選（geographic な group_split）のブロック数。無ければ0。</summary>
    private static int GeographicGroupCount(PrefFormat fmt)
    {
        foreach (var st in fmt.Stages)
        {
            if (st.Type == StageType.GroupSplit && st.Grouping == GroupingMode.Geographic && st.Groups is { } g)
                return g;
        }
        return 0;
    }
}
