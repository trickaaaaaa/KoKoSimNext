namespace KokoSim.Engine.Season;

/// <summary>センバツ選考発表の場面の型（issue #1732）。</summary>
public enum SenbatsuAnnounceKind
{
    /// <summary>自校が選ばれた（激励金と組み合わせ抽選の場面を積み、大会モードへ入る）。</summary>
    Selected,
    /// <summary>秋の地区大会に出ていて、選ばれなかった。</summary>
    NotSelected,
    /// <summary>秋の地区大会に出ていない（候補ですらなかった）。短く流す。</summary>
    NotEntered,
}

/// <summary>
/// センバツ選考発表の場面の振り分け（設計書05 §1.5b）。保存済みの結果を読むだけで乱数を引かない＝決定論に触れない。
/// 発表は選考発表週（<see cref="SeasonCalendar.SenbatsuAnnounceWeek"/>＝2月1週目）に1回だけ。
/// </summary>
public static class SenbatsuAnnounceScene
{
    public const string NotSelectedEventId = "senbatsu_not_selected";
    public const string NotEnteredEventId = "senbatsu_not_entered";

    public static SenbatsuAnnounceKind KindOf(bool selected, bool enteredRegional)
        => selected ? SenbatsuAnnounceKind.Selected
            : enteredRegional ? SenbatsuAnnounceKind.NotSelected
            : SenbatsuAnnounceKind.NotEntered;

    /// <summary>その型で積む単発の場面ID。選ばれた年は激励金・抽選の2場面を別に積むので null。</summary>
    public static string? EventIdOf(SenbatsuAnnounceKind kind) => kind switch
    {
        SenbatsuAnnounceKind.NotSelected => NotSelectedEventId,
        SenbatsuAnnounceKind.NotEntered => NotEnteredEventId,
        _ => null,
    };
}
