using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>
/// 成長推移の1点（設計書06 §3.2・issue #1027）。記録した時点の学年・週と、全能力のレベル
/// （<see cref="AbilityKinds.All"/> の並び＝<c>(int)AbilityKind</c> で引く）。
/// </summary>
public sealed record AbilitySample(int Grade, int Week, IReadOnlyList<int> Levels)
{
    public int Level(AbilityKind k) => Levels[(int)k];
}

/// <summary>
/// 成長推移の記録（設計書06 §3.2・issue #1027）。自校の部員について、入学（または記録を始めた時点）から
/// <b>毎月の頭</b>に全能力のレベルを1点ずつ残す。3年間で約36点。
///
/// <para><b>観測だけ</b>＝乱数を引かず能力も変えない（不変条件#2。試合・育成の結果と決定論ベースラインは不変）。
/// AI校の選手には記録しない（4000校ぶんの履歴を持たない・画面にも出ない）。</para>
///
/// <para>呼ぶ場所は週送りの共通入口（Shell の <c>GameClock.EnterWeek</c>）だけ＝どの画面から週を進めても、
/// 大会中でも同じ地点で記録される。</para>
/// </summary>
public static class AbilityHistory
{
    /// <summary>記録する週か（暦月の第1週）。</summary>
    public static bool IsSampleWeek(int week, SeasonCalendar calendar) => calendar.DateOf(week).WeekOfMonth == 1;

    /// <summary>
    /// 週に入った直後に呼ぶ。引退していない部員について、月の頭なら1点残す。まだ1点も無い部員
    /// （ゲーム開始直後の部員・旧セーブ・転任先の部員）は月の頭でなくてもその週から記録を始める。
    /// 引退した部員は、最後の点から値が動いていたときだけ1点足す＝線が引退時の値で終わる
    /// （引退後は能力が動かないので、それ以上は増えない）。
    /// </summary>
    public static void OnWeekEntered(IEnumerable<DevelopingPlayer> roster, int week, SeasonCalendar calendar)
    {
        var sampleWeek = IsSampleWeek(week, calendar);
        foreach (var p in roster)
        {
            if (p.IsRetired)
            {
                if (p.History.Count > 0 && !SameAsLast(p)) Record(p, week);
                continue;
            }
            if (sampleWeek || p.History.Count == 0) Record(p, week);
        }
    }

    private static bool SameAsLast(DevelopingPlayer p)
    {
        var last = p.History[^1];
        foreach (var k in AbilityKinds.All)
            if (last.Level(k) != p.Level(k)) return false;
        return true;
    }

    /// <summary>まだ1点も無い部員にだけ、今の値で始点を置く（ゲーム開始時の部員用）。</summary>
    public static void EnsureStarted(IEnumerable<DevelopingPlayer> roster, int week)
    {
        foreach (var p in roster)
            if (!p.IsRetired && p.History.Count == 0) Record(p, week);
    }

    /// <summary>今の値で1点残す。同じ学年・週の点が既にあれば置き換える（同じ週に二度入っても点は増えない）。</summary>
    public static void Record(DevelopingPlayer p, int week)
    {
        var levels = new int[AbilityKinds.All.Length];
        foreach (var k in AbilityKinds.All) levels[(int)k] = p.Level(k);
        var sample = new AbilitySample(p.Grade, week, levels);

        var h = p.History;
        if (h.Count > 0 && h[^1].Grade == sample.Grade && h[^1].Week == week) h[^1] = sample;
        else h.Add(sample);
    }

    /// <summary>
    /// 画面に描く点列＝記録＋「今」の値。引退していなければ最後に今の値を足す（記録は月の頭だけなので、
    /// 足さないと月の途中で伸びた分が線に出ない）。今の週が最後の記録と同じなら足さない。
    /// </summary>
    public static IReadOnlyList<AbilitySample> SeriesWithCurrent(DevelopingPlayer p, int week)
    {
        var list = new List<AbilitySample>(p.History);
        if (p.IsRetired) return list;
        if (list.Count > 0 && list[^1].Grade == p.Grade && list[^1].Week == week)
            list.RemoveAt(list.Count - 1);
        var levels = new int[AbilityKinds.All.Length];
        foreach (var k in AbilityKinds.All) levels[(int)k] = p.Level(k);
        list.Add(new AbilitySample(p.Grade, week, levels));
        return list;
    }

    /// <summary>
    /// 3年間のうちどこか（0＝1年4月第1週、1＝3年の年度末）。グラフの横軸の位置。
    /// 卒業後（学年4以上）は1に貼り付ける。
    /// </summary>
    public static double CareerFraction(int grade, int week, SeasonCalendar calendar)
    {
        var total = 3.0 * calendar.WeeksPerYear;
        var t = ((grade - 1) * calendar.WeeksPerYear + week) / total;
        return Math.Max(0.0, Math.Min(1.0, t));
    }
}
