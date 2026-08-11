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
        /// 次の公式戦のカウントダウン見出し（issue #582）。夏＝地方予選なので「夏予選まで」、
        /// 秋＝秋季県大会は予選ではなく本大会なので「秋大会まで」。文言もここが単一ソース。
        /// </summary>
        public static string CountdownLabel(TournamentKind kind)
            => kind == TournamentKind.Summer ? "夏予選まで" : "秋大会まで";

        /// <summary>暦年の小書き（"YYYY年"）。年度と4月始まりの繰り上がりは CurrentLabel と同じ規則。</summary>
        public static string YearLabel(int yearIndex, int week) => CalendarYear(yearIndex, week) + "年";

        /// <summary>
        /// 年度＋週 → 暦年（4月始まりの繰り上がり込み）。表示文字列から年を切り出して数え直さないための
        /// 単一ソース（issue #586: ドラフト指名年・キャリア年表がこの値を数値のまま要る）。
        /// </summary>
        public static int CalendarYear(int yearIndex, int week)
            => SeasonBaseYear + (yearIndex - 1) + Calendar.DateOf(week).YearOffset;
    }
}
