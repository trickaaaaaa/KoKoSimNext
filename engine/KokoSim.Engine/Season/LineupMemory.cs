using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Season;

/// <summary>
/// 前の試合で確定したスタメンの記憶（issue lineup-carry-over-previous-game）。
/// <see cref="LineupSpec"/> は選手の参照を直接持つのでセーブに載らず、ロード後は別インスタンスになる。
/// ここでは<b>選手ID</b>で持つ＝セーブ可能・引退／背番号変更を跨いでも照合できる純データ。
/// 9枠の打順（選手ID＋守備位置。DH枠は <see cref="FieldPosition.DesignatedHitter"/>）と、DH制の先発投手ID。
/// </summary>
public sealed record LineupMemory(
    IReadOnlyList<int> PlayerIds,
    IReadOnlyList<FieldPosition> Positions,
    int? StartingPitcherId)
{
    public const int Slots = 9;

    /// <summary>DH制か（「指」の枠があるか）。</summary>
    public bool UsesDh => DhSlot >= 0;

    /// <summary>「指」の枠（0基点）。無ければ -1。</summary>
    public int DhSlot => DefenseAssignment.SlotOf(Positions, FieldPosition.DesignatedHitter);

    /// <summary>形が正しいか（9枠・IDが正・DH制なら先発IDあり）。セーブが壊れていたら復元せず自動シードへ落とす。</summary>
    public bool IsWellFormed
    {
        get
        {
            if (PlayerIds.Count != Slots || Positions.Count != Slots) return false;
            foreach (var id in PlayerIds) if (id <= 0) return false;
            if (UsesDh && (StartingPitcherId is null || StartingPitcherId <= 0)) return false;
            return true;
        }
    }

    /// <summary>
    /// 確定した <see cref="LineupSpec"/> から記憶を作る。spec の DH枠は守備位置に投手を入れて
    /// <see cref="LineupSpec.DhSlot"/> で示す規約（UI の ToLineupSpec）なので、その枠を「指」に読み替える。
    /// </summary>
    public static LineupMemory FromSpec(LineupSpec spec)
    {
        if (spec.BattingOrder.Count != Slots)
            throw new ArgumentException($"打順は{Slots}枠でなければならない（{spec.BattingOrder.Count}）", nameof(spec));
        var ids = new int[Slots];
        var pos = new FieldPosition[Slots];
        for (var i = 0; i < Slots; i++)
        {
            ids[i] = spec.BattingOrder[i].Player.Id;
            pos[i] = spec.UsesDh && i == spec.DhSlot ? FieldPosition.DesignatedHitter : spec.BattingOrder[i].Position;
        }
        return new LineupMemory(ids, pos, spec.UsesDh ? spec.StartingPitcher?.Id : null);
    }
}

/// <summary>前回スタメンを現ロスターへ当てはめた結果。<see cref="Restored"/>=false なら呼び手は自動シードへ落とす。</summary>
public sealed record LineupRestoreResult(
    bool Restored,
    int[] RosterIndices,
    FieldPosition[] Positions,
    bool UsesDh,
    int StartingPitcherIndex)
{
    public static readonly LineupRestoreResult NotRestored =
        new(false, Array.Empty<int>(), Array.Empty<FieldPosition>(), false, -1);
}

/// <summary>
/// 前回スタメン（<see cref="LineupMemory"/>）を現在のロスターへ当てはめる純関数（issue lineup-carry-over-previous-game）。
/// 乱数不使用・UnityEngine 非依存。規則:
/// <list type="bullet">
/// <item>各枠の選手が今もロスターに居て<b>出場登録できる</b>（<paramref name="isEligible"/>＝ベンチ入り）なら、
/// 打順・守備位置ともそのまま</item>
/// <item>居ない／ベンチ外／引退・転校で消えた枠は<b>穴</b>。投手枠（守備位置が投手）は <paramref name="fillByPitching"/>、
/// それ以外は <paramref name="fillByBatting"/> の先頭から未使用の者で埋める（呼び手が UI の穴埋めと同じ順位で渡す）。
/// 守備位置は枠のものを維持＝他の枠は動かさない</item>
/// <item>DH制: <paramref name="dhAllowed"/> なら先発投手も同じ規則で復元／穴埋め。DH不可なら非DHへ畳む
/// （「指」の枠に先発投手を戻し、DHの打者は控えへ＝UI の ToggleDh OFF と同じ形）</item>
/// <item>穴が埋め切れない・守備網羅（<see cref="DefenseAssignment.CoversAllPositions"/>）が崩れる・記憶の形が
/// 壊れている場合は <see cref="LineupRestoreResult.Restored"/>=false（不正な LineupSpec は engine が例外で弾くので画面側で先に塞ぐ）</item>
/// </list>
/// </summary>
public static class LineupRestorer
{
    public static LineupRestoreResult Restore(
        LineupMemory? memory,
        IReadOnlyList<DevelopingPlayer> roster,
        Func<int, bool> isEligible,
        IReadOnlyList<int> fillByBatting,
        IReadOnlyList<int> fillByPitching,
        bool dhAllowed = true)
    {
        if (memory is null || !memory.IsWellFormed) return LineupRestoreResult.NotRestored;

        var idx = new int[LineupMemory.Slots];
        var pos = new FieldPosition[LineupMemory.Slots];
        var used = new HashSet<int>();

        // ① ID照合（ベンチ入りの者だけ。同じ選手が2枠に来る壊れた記憶は2枠目を穴にする）。
        for (var i = 0; i < LineupMemory.Slots; i++)
        {
            pos[i] = memory.Positions[i];
            var r = IndexOfId(roster, memory.PlayerIds[i]);
            idx[i] = r >= 0 && isEligible(r) && used.Add(r) ? r : -1;
        }

        // ② DH の扱い。
        var usesDh = memory.UsesDh && dhAllowed;
        var sp = -1;
        if (memory.UsesDh)
        {
            var spr = memory.StartingPitcherId is int spid ? IndexOfId(roster, spid) : -1;
            var spOk = spr >= 0 && isEligible(spr) && !used.Contains(spr);
            if (dhAllowed)
            {
                if (spOk) { sp = spr; used.Add(spr); }
            }
            else
            {
                // 畳む: 「指」の枠へ先発投手を戻す（居なければ投手枠の穴として下で埋める）。DHの打者は外れる。
                var dh = memory.DhSlot;
                if (idx[dh] >= 0) used.Remove(idx[dh]);
                pos[dh] = FieldPosition.Pitcher;
                idx[dh] = -1;
                if (spOk) { idx[dh] = spr; used.Add(spr); }
            }
        }

        // ③ 穴埋め（投手枠＝投手力順、他＝打撃力順）。
        for (var i = 0; i < LineupMemory.Slots; i++)
        {
            if (idx[i] >= 0) continue;
            var fill = pos[i] == FieldPosition.Pitcher ? fillByPitching : fillByBatting;
            var pick = FirstUnused(fill, used, isEligible);
            if (pick < 0) return LineupRestoreResult.NotRestored;
            idx[i] = pick;
            used.Add(pick);
        }
        if (usesDh && sp < 0)
        {
            sp = FirstUnused(fillByPitching, used, isEligible);
            if (sp < 0) return LineupRestoreResult.NotRestored;
            used.Add(sp);
        }

        // ④ 守備網羅（DH制なら「指」の枠を除く8守備＋投手枠が打順外＝投手は9守備位置の要求から外れる）。
        if (!DefenseAssignment.CoversAllPositions(pos, usesDh ? memory.DhSlot : -1))
            return LineupRestoreResult.NotRestored;

        return new LineupRestoreResult(true, idx, pos, usesDh, sp);
    }

    private static int IndexOfId(IReadOnlyList<DevelopingPlayer> roster, int id)
    {
        for (var i = 0; i < roster.Count; i++) if (roster[i].Id == id) return i;
        return -1;
    }

    private static int FirstUnused(IReadOnlyList<int> ranked, HashSet<int> used, Func<int, bool> isEligible)
    {
        foreach (var i in ranked) if (!used.Contains(i) && isEligible(i)) return i;
        return -1;
    }
}
