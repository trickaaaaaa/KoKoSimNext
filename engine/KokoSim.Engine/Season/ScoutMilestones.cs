namespace KokoSim.Engine.Season;

/// <summary>スカウトの節目（操作が要る場面）。強制イベントにして見落とさせない（issue #1035）。</summary>
public enum ScoutMilestone
{
    None,
    /// <summary>探す分野を選ぶ（新チーム発足の流れのあと）。</summary>
    ChooseField,
    /// <summary>候補リストが届いた（第24週）。</summary>
    ListArrived,
    /// <summary>訪問期間の終わり前の念押し（枠が余っているか、まだ誰も訪問していない）。</summary>
    DeadlineReminder,
}

/// <summary>
/// スカウトの節目イベントを「出すか・いつ出すか」の判定（設計書04 §2.4「年間の流れ」・issue #1035）。
/// スカウト網が未契約（枠0）の年は、どの節目も出さない。判定はここ1箇所に集め、乱数も時刻も使わない純関数。
/// </summary>
public static class ScoutMilestones
{
    public const string ChooseFieldEventId = "scout_choose_field";
    public const string ListArrivedEventId = "scout_list_arrived";
    public const string DeadlineEventId = "scout_deadline";

    /// <summary>念押しを出す週。確定週（第46週）の2週前＝年契約の更新（確定週の直前）と重ならない。</summary>
    public static int DeadlineWeek(SeasonCalendar calendar) => calendar.ProspectConfirmWeek - 2;

    /// <summary>節目のイベントID（<see cref="ScoutMilestone.None"/> は null）。</summary>
    public static string? EventId(ScoutMilestone milestone) => milestone switch
    {
        ScoutMilestone.ChooseField => ChooseFieldEventId,
        ScoutMilestone.ListArrived => ListArrivedEventId,
        ScoutMilestone.DeadlineReminder => DeadlineEventId,
        _ => null,
    };

    /// <summary>
    /// 新チーム発足の流れ（引退→主将指名→背番号決定）が終わった直後に分野を選ばせるか。
    /// 契約済み（枠あり）で、今年ぶんの候補リストがまだ無い（＝分野を選べる）ときだけ。
    /// </summary>
    public static ScoutMilestone AfterNewTeamStarted(
        SeasonCalendar calendar, int week, int yearIndex, int slots, ScoutingState? list)
    {
        if (slots <= 0) return ScoutMilestone.None;
        if (week >= calendar.RecruitListWeek) return ScoutMilestone.None;
        return HasListFor(list, yearIndex) ? ScoutMilestone.None : ScoutMilestone.ChooseField;
    }

    /// <summary>
    /// 週に入った直後に出す節目（候補リスト到着の週・念押しの週）。リスト生成（第24週）の<b>後</b>に呼ぶこと。
    /// 念押しは「枠が余っている」か「まだ誰も訪問していない」ときだけ＝枠を使い切って訪問も済んでいれば出さない。
    /// </summary>
    public static ScoutMilestone AtWeek(
        SeasonCalendar calendar, int week, int yearIndex, int slots, ScoutingState? list)
    {
        if (slots <= 0 || !HasListFor(list, yearIndex) || list!.Resolved) return ScoutMilestone.None;

        if (calendar.IsRecruitListWeek(week)) return ScoutMilestone.ListArrived;

        if (week == DeadlineWeek(calendar))
        {
            var visits = 0;
            foreach (var c in list.Candidates) visits += c.Visits;
            if (list.TargetedCount < slots || visits == 0) return ScoutMilestone.DeadlineReminder;
        }
        return ScoutMilestone.None;
    }

    /// <summary>今年度（4月〜翌3月）の勧誘対象のリスト（合流年度＝今年度＋1）が、候補つきで出ているか。</summary>
    public static bool HasListFor(ScoutingState? list, int yearIndex)
        => list != null && list.YearIndex == yearIndex + 1 && list.Candidates.Count > 0;

    /// <summary>探す分野を選べるか（今年ぶんのリストが出ると凍結される）。</summary>
    public static bool CanChooseField(ScoutingState? list, int yearIndex)
        => list == null || list.YearIndex != yearIndex + 1;
}
