using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Season;

/// <summary>
/// 打順9枠の守備位置割当（純データ・表示層なし）。操作は「2枠の守備位置を交換する」の一手だけで表現する。
/// 交換は守備位置の多重集合を変えないので、9ポジションの一意性は自動で保たれる（issue #233）。
/// 「投」「指」も他と同じ1つの位置として動かせる＝内部の投/野区分でゲートしない（issue #93）。
/// 投手枠・DH枠は別フィールドで持たず<b>割当配列から引き直す</b>（<see cref="SlotOf"/>）。
/// 「投を別の打順へ動かしたのに PitcherSlot が古いまま」というズレが原理的に起きない形にするため。
/// スタメン設定（試合前）と守備交代（試合中）が共有する規則の単一ソース。UnityEngine 非依存（不変条件#3）。
/// </summary>
public static class DefenseAssignment
{
    /// <summary>守備に就く9ポジション（DHは含まない）。網羅の検算に使う。</summary>
    public static readonly IReadOnlyList<FieldPosition> Fielding = new[]
    {
        FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField,
        FieldPosition.RightField,
    };

    /// <summary>
    /// 2枠の守備位置を交換した新しい配列を返す（引数は変更しない）。同一枠・範囲外なら複製をそのまま返す。
    /// </summary>
    public static FieldPosition[] Swap(IReadOnlyList<FieldPosition> positions, int a, int b)
    {
        var next = new FieldPosition[positions.Count];
        for (var i = 0; i < positions.Count; i++) next[i] = positions[i];
        if (a == b || a < 0 || b < 0 || a >= next.Length || b >= next.Length) return next;
        (next[a], next[b]) = (next[b], next[a]);
        return next;
    }

    /// <summary>その守備位置に就いている打順枠（0基点）。居なければ -1。投手枠・DH枠の追従に使う。</summary>
    public static int SlotOf(IReadOnlyList<FieldPosition> positions, FieldPosition position)
    {
        for (var i = 0; i < positions.Count; i++)
            if (positions[i] == position) return i;
        return -1;
    }

    /// <summary>
    /// 9ポジションを一意に網羅しているか。<see cref="RosterTeamBuilder.BuildFromLineup"/> の受け入れ条件と同じ判定
    /// （DH枠は守備から除き、代わりに打順外の先発投手を投手として数える）。
    /// </summary>
    public static bool CoversAllPositions(IReadOnlyList<FieldPosition> positions, int dhSlot = -1)
    {
        var covered = new HashSet<FieldPosition>();
        for (var i = 0; i < positions.Count; i++)
        {
            if (i == dhSlot) continue;
            covered.Add(positions[i]);
        }
        if (dhSlot >= 0) covered.Add(FieldPosition.Pitcher);
        // 「指」が守備側に混ざっていたら網羅ではない（数だけ合っても実ポジションが1つ欠ける）。
        return covered.Count == 9 && !covered.Contains(FieldPosition.DesignatedHitter);
    }
}
