namespace KokoSim.Engine.Season;

/// <summary>大会種別（夏の地方大会 / 秋季県大会）。大会モードの開始判定に使う（設計書05 §1.1）。</summary>
public enum TournamentKind
{
    Summer,
    Autumn,
}

/// <summary>
/// 年間カレンダー（設計書03 §2）。1年=50週、4月始まり。週→半期・合宿・引退時期を対応づける。
/// </summary>
public sealed record SeasonCalendar
{
    public int WeeksPerYear { get; init; } = 50;

    /// <summary>前半（4〜9月）と後半（10〜3月）の境界週。</summary>
    public int SecondHalfStartWeek { get; init; } = 25;

    /// <summary>3年生が夏で引退する週（7月末 ≒ 第17週）。以降は練習しない。</summary>
    public int SummerRetireWeek { get; init; } = 17;

    /// <summary>夏合宿の週（8月, 新チーム結成直後）。</summary>
    public int SummerCampWeek { get; init; } = 18;
    /// <summary>冬合宿の週（12月末〜1月）。</summary>
    public int WinterCampWeek { get; init; } = 38;

    /// <summary>夏の地方大会が開幕する週（7月1週目 ≒ 第13週。現実の夏の地方予選は7月上旬開幕。設計書05 §1.1 / OPEN-QUESTIONS Q23）。
    /// 大会は「大会モード」1週ターン内で抽象日を一気消化するため、開幕週はトーナメント内部日程(MatchDay)には影響しない
    /// ＝この定数変更は帯/決定論baselineに無影響（暦表示と引退week17までの余裕にのみ効く）。</summary>
    public int SummerTournamentStartWeek { get; init; } = 13;
    /// <summary>秋季県大会が開幕する週（9月, 新チームの初公式戦 ≒ 第23週。設計書05 §1.1）。</summary>
    public int AutumnTournamentStartWeek { get; init; } = 23;

    public bool IsFirstHalf(int week) => week < SecondHalfStartWeek;

    /// <summary>その週に開幕する大会があれば種別を返す（なければ null）。大会モードへの遷移判定に使う。</summary>
    public TournamentKind? TournamentStartingAt(int week)
    {
        if (week == SummerTournamentStartWeek) return TournamentKind.Summer;
        if (week == AutumnTournamentStartWeek) return TournamentKind.Autumn;
        return null;
    }

    // 週(0基点)→暦の対応（4月始まり・50週）。前半(4〜9月)=週0-24／後半(10〜3月)=週25-49。
    // 3年引退(第17週=7月末)・夏合宿(第18週=8月)・冬合宿(第38週=1月)のアンカーに整合するよう
    // 月ごとの週数を配分（合計50週。設計書03 §2）。※月配分はDateOf表示専用で、成長段階/合宿/引退の
    // 判定は週インデックスを直接使うため影響しない。
    private static readonly int[] MonthOrder    = { 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2, 3 };
    private static readonly int[] WeeksPerMonth = { 4, 5, 4, 5, 4, 3,  4,  4,  5, 4, 4, 4 };

    /// <summary>
    /// その週の暦日付を返す。month=暦月(1〜12)、weekOfMonth=月内の週(1〜)、
    /// yearOffset=シーズン開始年からの暦年オフセット(1〜3月は+1、4〜12月は0)。
    /// </summary>
    public (int Month, int WeekOfMonth, int YearOffset) DateOf(int week)
    {
        week = ((week % WeeksPerYear) + WeeksPerYear) % WeeksPerYear;   // 0..49 に正規化
        var acc = 0;
        for (var i = 0; i < MonthOrder.Length; i++)
        {
            if (week < acc + WeeksPerMonth[i])
            {
                var month = MonthOrder[i];
                return (month, week - acc + 1, month <= 3 ? 1 : 0);
            }
            acc += WeeksPerMonth[i];
        }
        return (3, WeeksPerMonth[^1], 1);   // 到達しない（保険）
    }

    /// <summary>学年と週から成長段階インデックス(0〜4)を返す（設計書02 §5.2）。</summary>
    public int StageIndex(int grade, int week)
    {
        if (grade >= 3) return 4;              // 3年(4〜7月)
        var half = IsFirstHalf(week) ? 0 : 1;  // 前半/後半
        return (grade - 1) * 2 + half;         // 1年前半0,後半1 / 2年前半2,後半3
    }

    /// <summary>その週の合宿倍率（合宿でなければ1.0）。</summary>
    public double CampMultiplier(int week, TrainingCoefficients c)
    {
        if (week == SummerCampWeek) return c.SummerCampMult;
        if (week == WinterCampWeek) return c.WinterCampMult;
        return 1.0;
    }

    /// <summary>3年生がこの週に練習可能か（夏で引退後は不可）。</summary>
    public bool CanTrain(int grade, int week) => grade < 3 || week <= SummerRetireWeek;
}
