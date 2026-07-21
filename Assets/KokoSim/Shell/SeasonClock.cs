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

        /// <summary>暦年の小書き（"YYYY年"）。年度と4月始まりの繰り上がりは CurrentLabel と同じ規則。</summary>
        public static string YearLabel(int yearIndex, int week)
        {
            var d = Calendar.DateOf(week);
            return (SeasonBaseYear + (yearIndex - 1) + d.YearOffset) + "年";
        }
    }
}
