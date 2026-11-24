namespace KokoSim.Engine.Season;

/// <summary>
/// 体験版で遊べる範囲（steam-roadmap 段階4・Web 体験版 #1323）。<b>2年目の夏が全部終わるまで</b>
/// ＝1年目から見てきた選手の最後の夏（3年目）は遊ばせない。
///
/// 「夏が全部終わった」は、3年引退の週（<see cref="SeasonCalendar.SummerRetireWeek"/>）に入っていて、
/// 夏の大会（自校が出る甲子園・観るだけの甲子園を含む）がもう動いていないこと（2026-11-24 オーナー決定）。
/// 週だけで切らないのは、県大会で深く勝ち進んで負けた年は観るだけの甲子園が引退の週を過ぎても続くため
/// （甲子園は暦の週でなく県大会の直後から日で進む・KoshienEntry）。決勝の結果までは見せる。
///
/// 体験版かどうかは Unity 側（KOKOSIM_DEMO）が決める＝ここは範囲の判定だけ。
/// </summary>
public static class DemoScope
{
    /// <summary>体験版で遊べる最後の年度（1始まりの YearIndex）。</summary>
    public const int LastYear = 2;

    /// <summary>
    /// 体験版の範囲を出ているか。
    /// </summary>
    /// <param name="yearIndex">年度（1始まり）。</param>
    /// <param name="week">年度内の週（0始まり）。</param>
    /// <param name="summerOngoing">夏の大会（県大会・甲子園・観るだけの甲子園）がまだ動いているか。</param>
    public static bool IsOver(int yearIndex, int week, bool summerOngoing, SeasonCalendar calendar)
    {
        if (yearIndex > LastYear) return true;
        if (yearIndex < LastYear) return false;
        return week >= calendar.SummerRetireWeek && !summerOngoing;
    }

    /// <summary>
    /// 週を進める前に止めるか＝今すでに範囲を出ているか、1週進めると出るか。
    /// 止めたら週は進めず、体験版の終了画面を出す。
    /// </summary>
    public static bool StopBeforeAdvance(int yearIndex, int week, bool summerOngoing, SeasonCalendar calendar)
    {
        if (IsOver(yearIndex, week, summerOngoing, calendar)) return true;
        var nextWeek = week + 1;
        var nextYear = yearIndex;
        if (nextWeek >= calendar.WeeksPerYear) { nextWeek = 0; nextYear++; }
        return IsOver(nextYear, nextWeek, summerOngoing, calendar);
    }
}
