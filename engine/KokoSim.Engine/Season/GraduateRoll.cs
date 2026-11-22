using System;
using System.Collections.Generic;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 卒業生1人ぶんの名前の表の行（issue #1414）。成績の帰属キー（選手ID）から名前を引くための最小限だけを持つ。
/// 年度は YearIndex（1始まり）。0＝不明（本体ごと持っていた旧セーブから変換した行は、いつ入学・卒業したか辿れない）。
/// </summary>
public readonly record struct GraduateEntry(int Id, string Name, int EnrolledYearIndex, int GraduatedYearIndex);

/// <summary>
/// 卒業生の名前の表（issue #1414）。卒業生を選手データ丸ごとで持ち続けるとセーブが年10〜15人ずつ際限なく重くなるので、
/// 「選手ID・名前・入学年度・卒業年度」だけを残して本体は捨てる。使い道は記録画面などの ID→名前だけ。
/// 同じ選手IDは1行（後から入れた方が勝つ）。純ロジック・乱数不使用（不変条件#2/#3）。
/// </summary>
public sealed class GraduateRoll
{
    private readonly List<GraduateEntry> _entries = new();
    private readonly Dictionary<int, int> _indexById = new();

    public IReadOnlyList<GraduateEntry> Entries => _entries;
    public int Count => _entries.Count;

    /// <summary>名前の表の最大ID（採番の再開で、卒業生のIDを再利用しないために見る）。空なら 0。</summary>
    public int MaxId
    {
        get
        {
            var max = 0;
            foreach (var e in _entries) max = Math.Max(max, e.Id);
            return max;
        }
    }

    public bool Contains(int id) => _indexById.ContainsKey(id);

    public bool TryGetName(int id, out string name)
    {
        if (_indexById.TryGetValue(id, out var i)) { name = _entries[i].Name; return true; }
        name = "";
        return false;
    }

    public void Add(GraduateEntry entry)
    {
        if (_indexById.TryGetValue(entry.Id, out var i)) { _entries[i] = entry; return; }
        _indexById[entry.Id] = _entries.Count;
        _entries.Add(entry);
    }

    /// <summary>
    /// 年度替わり（<see cref="RosterLifecycle.AdvanceYear"/> の <c>year</c>＝新年度）で卒業した選手を名前の表へ移す。
    /// 卒業は前年度の末なので卒業年度は <c>newYearIndex-1</c>、3年在籍なので入学年度は <c>newYearIndex-3</c>。
    /// </summary>
    public void AddGraduated(IEnumerable<DevelopingPlayer> graduated, int newYearIndex)
    {
        foreach (var p in graduated)
            Add(new GraduateEntry(p.Id, p.Name, newYearIndex - 3, newYearIndex - 1));
    }

    /// <summary>旧セーブ（卒業生を本体ごと持っていた版）の卒業生を名前の表へ移す。年度は辿れないので 0。</summary>
    public void AddLegacy(IEnumerable<DevelopingPlayer> graduated)
    {
        foreach (var p in graduated) Add(new GraduateEntry(p.Id, p.Name, 0, 0));
    }
}
