using System.Collections.Generic;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 勢力図マップ（issue #1494）の材料。県ごとに「その県の最強校」を選び、ランク色5段（S/A/B/C/D以下）へ丸める。
/// 表示専用＝乱数を引かず、全国の状態にも触れない。
/// </summary>
public static class PrefecturePowerMap
{
    /// <summary>塗り分けの5段（強い順）。D より下は D にまとめる。</summary>
    public static readonly IReadOnlyList<Tier> Steps = new[] { Tier.S, Tier.A, Tier.B, Tier.C, Tier.D };

    public static Tier StepOf(Tier tier) => tier < Tier.D ? Tier.D : tier;

    /// <summary>県Id→その県で <see cref="School.Strength"/> が最大の学校（同点は校Idの小さい方）。</summary>
    public static IReadOnlyDictionary<int, School> StrongestByPrefecture(IEnumerable<School> schools)
    {
        var best = new Dictionary<int, School>();
        foreach (var s in schools)
        {
            if (!best.TryGetValue(s.PrefectureId, out var cur)
                || s.Strength > cur.Strength
                || (s.Strength == cur.Strength && s.Id < cur.Id))
                best[s.PrefectureId] = s;
        }
        return best;
    }
}
