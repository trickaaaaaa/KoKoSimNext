using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 共通トップバー「現在」表示（YYYY年M月W週目）の単一ソース。
    /// 週→暦の変換はエンジンの SeasonCalendar.DateOf（設計書03 §2）に委譲し、
    /// ここでは暦年の起点（年度1 = SeasonBaseYear 年度・4月始まり）と表記だけを決める。
    /// 全画面のトップバーはこの関数を通して日付を表示すること（画面ごとに書式を作らない）。
    /// </summary>
    public static class SeasonClock
    {
        /// <summary>年度1（yearIndex=1）のシーズン開始暦年。4月始まりのため4〜12月はこの年、1〜3月は翌年。</summary>
        public const int SeasonBaseYear = 2026;

        private static readonly SeasonCalendar Calendar = new SeasonCalendar();

        /// <summary>
        /// yearIndex=年度(1..)、week=0基点の週（0=シーズン頭4月1週目）。
        /// 例: (1, 0) → "2026年4月1週目"、(1, 38) → "2027年1月1週目"。
        /// </summary>
        public static string CurrentLabel(int yearIndex, int week)
        {
            var d = Calendar.DateOf(week);
            var calYear = SeasonBaseYear + (yearIndex - 1) + d.YearOffset;
            return calYear + "年" + d.Month + "月" + d.WeekOfMonth + "週目";
        }

        /// <summary>
        /// 掲示板の升目に載せる短縮形（"M月W週"）。年は升目に載せず <see cref="YearLabel"/> の
        /// 小書きに回す（設計書16 F1-b 案B）。書式を画面ごとに作らないための単一ソース。
        /// </summary>
        public static string CompactLabel(int yearIndex, int week)
        {
            var d = Calendar.DateOf(week);
            return d.Month + "月" + d.WeekOfMonth + "週";
        }

        /// <summary>
        /// 実月日（大会の日付）が入る週（0基点）。月内の週は日付から 1〜7日＝1週・8〜14日＝2週…と数え、その月の週数で頭打ち。
        /// 週暦は1年50週で実暦と一致しないので、大会の日付を週の表示へ写すときだけ使う（issue #1534 項目9）。
        /// </summary>
        public static int WeekOfDate(int month, int day)
        {
            var wanted = System.Math.Max(1, (day + 6) / 7);
            var last = -1;
            for (var w = 0; w < Calendar.WeeksPerYear; w++)
            {
                var d = Calendar.DateOf(w);
                if (d.Month != month) continue;
                last = w;
                if (d.WeekOfMonth == wanted) return w;
            }
            return last;
        }

        /// <summary>
        /// トップバーに出す週。直前に終わった大会の最終日（<paramref name="floorDate"/>）より前の週は出さない
        /// （issue #1534 項目9: 大会は日で進み、明けの週送りは「最終日が入る週」までの切り捨てなので、
        /// 7月16日に負けた後に「7月2週」と出て日付が逆戻りして見えた）。下限は同じ年度・数週以内のときだけ効かせる。
        /// </summary>
        public static int DisplayWeek(int yearIndex, int week, (int YearIndex, int Month, int Day)? floorDate)
        {
            if (floorDate is not { } f || f.YearIndex != yearIndex) return week;
            var floor = WeekOfDate(f.Month, f.Day);
            return floor > week && floor - week <= 5 ? floor : week;
        }

        /// <summary>
        /// 次の公式戦のカウントダウン見出し（issue #582）。夏＝地方予選なので「夏予選まで」、
        /// 秋＝秋季県大会は予選ではなく本大会なので「秋大会まで」。文言もここが単一ソース。
        /// </summary>
        public static string CountdownLabel(TournamentKind kind) => kind switch
        {
            TournamentKind.Summer => "夏予選まで",
            TournamentKind.SenbatsuKoshien => KokoSim.Engine.Season.DisplayNames.Current.Senbatsu + "まで",
            TournamentKind.Regional => "地区大会まで",
            TournamentKind.Jingu => KokoSim.Engine.Season.DisplayNames.Current.JinguShort + "まで",
            _ => "秋大会まで",
        };

        /// <summary>
        /// ホームの主役のカードの見出し（「夏の大会まで」）。季節だけで呼ぶ＝夏予選・夏の甲子園は「夏」、
        /// センバツは「春」、秋季大会・地区大会・神宮は「秋」。
        /// </summary>
        public static string SeasonCountdownLabel(TournamentKind kind) => kind switch
        {
            TournamentKind.Summer or TournamentKind.SummerKoshien => "夏の大会まで",
            TournamentKind.SenbatsuKoshien => "春の大会まで",
            _ => "秋の大会まで",
        };

        /// <summary>
        /// 次の大会の呼び名（年なし・ホームの主役のカード用）。県の大会は「呼び名（県名＋大会）」
        /// （例「夏予選（秋田大会）」「秋季大会（秋田大会）」）、全国・地区の大会は呼び名だけ。
        /// </summary>
        public static string UpcomingTournamentName(TournamentKind kind, int prefectureId)
        {
            var names = KokoSim.Engine.Season.DisplayNames.Current;
            var pref = "（" + PrefectureNames.ShortOf(prefectureId) + "大会）";
            return kind switch
            {
                TournamentKind.Summer => "夏予選" + pref,
                TournamentKind.SummerKoshien => "夏の" + names.Koshien,
                TournamentKind.SenbatsuKoshien => names.Senbatsu,
                TournamentKind.Regional => "地区大会",
                TournamentKind.Jingu => names.JinguShort,
                _ => "秋季大会" + pref,
            };
        }

        /// <summary>週 → (月, 月の何週目)。週は年をまたいでよい（0..49 に正規化する）。</summary>
        public static (int Month, int WeekOfMonth) MonthWeekOf(int week)
        {
            var d = Calendar.DateOf(week);
            return (d.Month, d.WeekOfMonth);
        }

        /// <summary>暦年の小書き（"YYYY年"）。年度と4月始まりの繰り上がりは CurrentLabel と同じ規則。</summary>
        public static string YearLabel(int yearIndex, int week) => CalendarYear(yearIndex, week) + "年";

        /// <summary>
        /// 年度＋週 → 暦年（4月始まりの繰り上がり込み）。表示文字列から年を切り出して数え直さないための
        /// 単一ソース（issue #586: ドラフト指名年・キャリア年表がこの値を数値のまま要る）。
        /// </summary>
        public static int CalendarYear(int yearIndex, int week)
            => SeasonBaseYear + (yearIndex - 1) + Calendar.DateOf(week).YearOffset;

        /// <summary>
        /// 大会の開催年（暦年）。年度は4月始まりなので、センバツ（選考週＝翌年1月・開催は3月）だけ年度の暦年より1年遅い。
        /// ほかの大会（夏・秋・地区・神宮）は4〜12月＝年度の暦年と一致する。
        /// </summary>
        public static int TournamentCalendarYear(TournamentKind kind, int yearIndex)
            => kind == TournamentKind.SenbatsuKoshien
                ? CalendarYear(yearIndex, Calendar.SenbatsuAnnounceWeek)
                : SeasonBaseYear + (yearIndex - 1);
    }
}
