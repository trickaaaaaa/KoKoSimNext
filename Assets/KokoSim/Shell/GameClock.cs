using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// セッション単位の「現在週」を保持する単一ソース（全画面が共有）。
    /// 4月始まり・50週/年（設計書03 §2）。ホーム/練習/選手など全トップバーはここから週を読み、
    /// 「今週を進める」はどの画面から押しても Advance() でこの共有週を進める。
    /// ※ プロトタイプのため部員データ等は各画面で ephemeral のまま。共有するのは「現在週」だけ。
    /// エディタは Play 開始時のドメインリロードで静的値が初期化され、Week=0/YearIndex=1（＝2026年4月1週目）から始まる。
    /// </summary>
    public static class GameClock
    {
        private static readonly SeasonCalendar Calendar = new SeasonCalendar();

        /// <summary>現在週（0基点・0=シーズン頭4月1週目）。</summary>
        public static int Week { get; private set; }

        /// <summary>年度（1..）。週が年をまたぐと繰り上がる。</summary>
        public static int YearIndex { get; private set; } = 1;

        /// <summary>共有週を delta 進める（負も可）。50週で年度をまたぐ。年度1の前には戻らない。</summary>
        public static void Advance(int delta = 1)
        {
            var w = Week + delta;
            while (w >= Calendar.WeeksPerYear) { w -= Calendar.WeeksPerYear; YearIndex++; }
            while (w < 0 && YearIndex > 1) { w += Calendar.WeeksPerYear; YearIndex--; }
            Week = w < 0 ? 0 : w;
        }

        /// <summary>シーズン頭（週0・年度1）へ戻す。</summary>
        public static void Reset() { Week = 0; YearIndex = 1; }

        /// <summary>共通トップバー「現在」表示（YYYY年M月W週目）。</summary>
        public static string CurrentLabel() => SeasonClock.CurrentLabel(YearIndex, Week);
    }
}
