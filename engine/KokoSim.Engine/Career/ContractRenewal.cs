using System;
using System.Collections.Generic;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Career;

/// <summary>
/// 年契約の更新イベント（設計書04 §1.4a・issue #1034）。年額が引かれる年度の総括（第46週）の<b>前週</b>に、
/// 契約中の年契約をまとめて見せ、段を選び直させる。何も触らなければ今の段のまま（＝従来どおり自動更新）。
/// 出すかどうかと週の判定は乱数も時刻も使わない純関数で、ここ1箇所に置く。
/// </summary>
public static class ContractRenewal
{
    public const string EventId = "contract_renewal";

    /// <summary>
    /// 更新を聞く週＝総括（<see cref="SeasonCalendar.ProspectConfirmWeek"/>）の前週。
    /// 総括の回答待ちを挟まずに済み（週送りの順序・セーブの切れ目が増えない）、
    /// スカウトの念押し（<see cref="ScoutMilestones.DeadlineWeek"/>＝確定週の2週前）とも重ならない。
    /// </summary>
    public static int RenewalWeek(SeasonCalendar calendar) => calendar.ProspectConfirmWeek - 1;

    /// <summary>いま契約中（段≧1）の年契約。更新で見せる対象で、カタログの並び順。</summary>
    public static IReadOnlyList<ContractDef> ActiveContracts(ContractCatalog catalog, ContractSet set)
    {
        var list = new List<ContractDef>();
        foreach (var d in catalog.Contracts)
            if (d.MaxLevel > 0 && Math.Clamp(set.LevelOf(d.Id), 0, d.MaxLevel) >= 1) list.Add(d);
        return list;
    }

    /// <summary>更新の週で、契約中の年契約が1つ以上ある（全部未契約の年は出さない）。</summary>
    public static bool ShouldRaise(SeasonCalendar calendar, int week, ContractCatalog catalog, ContractSet set)
        => week == RenewalWeek(calendar) && ActiveContracts(catalog, set).Count > 0;
}

/// <summary>
/// 更新の下書き（契約ID→選んだ段）。決めるまで <see cref="ContractSet"/> には触れず、
/// <see cref="ApplyTo"/> で初めて書き戻す＝途中でやめても契約は変わらない。
/// 対象は作った時点で契約中の系統だけ（未契約の系統をここで新しく契約させない）。
/// </summary>
public sealed class ContractRenewalDraft
{
    private readonly ContractCatalog _catalog;
    private readonly List<ContractDef> _defs;
    private readonly Dictionary<string, int> _levels = new();

    public ContractRenewalDraft(ContractCatalog catalog, ContractSet current)
    {
        _catalog = catalog;
        _defs = new List<ContractDef>(ContractRenewal.ActiveContracts(catalog, current));
        foreach (var d in _defs) _levels[d.Id] = Math.Clamp(current.LevelOf(d.Id), 0, d.MaxLevel);
    }

    /// <summary>更新で見せる系統（作った時点で契約中だったもの）。</summary>
    public IReadOnlyList<ContractDef> Defs => _defs;

    /// <summary>選んでいる段。対象外の系統は 0。</summary>
    public int LevelOf(string id) => _levels.TryGetValue(id, out var lv) ? lv : 0;

    /// <summary>段を選ぶ（0＝やめる〜最上段にクランプ）。対象外の系統は無視。</summary>
    public void SetLevel(string id, int level)
    {
        if (!_levels.ContainsKey(id)) return;
        var def = _catalog.Find(id);
        if (def == null) return;
        _levels[id] = Math.Clamp(level, 0, def.MaxLevel);
    }

    /// <summary>選んだ段の年額合計[万円]。総括で引かれる額と同じ <see cref="ContractCatalog.Aggregate"/> の数え方。</summary>
    public double AnnualCost(ContractSet current) => _catalog.Aggregate(Preview(current)).AnnualCost;

    /// <summary>下書きを反映した契約状態の複製（対象外の系統は現状のまま）。元の <paramref name="current"/> は変えない。</summary>
    public ContractSet Preview(ContractSet current)
    {
        var set = new ContractSet();
        foreach (var kv in current.Levels) set.SetLevel(kv.Key, kv.Value);
        foreach (var kv in _levels) set.SetLevel(kv.Key, kv.Value);
        return set;
    }

    /// <summary>選んだ段を契約状態へ書き戻す。</summary>
    public void ApplyTo(ContractSet current)
    {
        foreach (var kv in _levels) current.SetLevel(kv.Key, kv.Value);
    }
}
