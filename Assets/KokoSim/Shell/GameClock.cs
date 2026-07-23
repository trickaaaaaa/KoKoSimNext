using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// セッション単位の「現在週」を保持する単一ソース（全画面が共有）。
    /// 4月始まり・50週/年（設計書03 §2）。ホーム/練習/選手など全トップバーはここから週を読み、
    /// 「今週を進める」はどの画面から押しても Advance() でこの共有週を進める。
    /// ※ プロトタイプのため部員データ等は各画面で ephemeral のまま。共有するのは「現在週」だけ。
    /// エディタは Play 開始時のドメインリロードで静的値が初期化され、Week=0/YearIndex=1（＝2026年4月1週目）から始まる。
    ///
    /// 週送りに伴うシーズン遷移（夏の3年引退＝新チーム発足）もここに集約する。ホーム・練習・大会・練習試合と
    /// 週を進める入口は複数あるが、遷移フックを各画面に散らさずこの1経路（<see cref="EnterWeek"/>）だけに置く。
    /// </summary>
    public static class GameClock
    {
        private static readonly SeasonCalendar Calendar = new SeasonCalendar();

        /// <summary>現在週（0基点・0=シーズン頭4月1週目）。</summary>
        public static int Week { get; private set; }

        /// <summary>年度（1..）。週が年をまたぐと繰り上がる。</summary>
        public static int YearIndex { get; private set; } = 1;

        /// <summary>
        /// 共有週を delta 進める（負も可）。50週で年度をまたぐ。年度1の前には戻らない。
        /// 前進のときは1週ずつ入り直し、通過した週それぞれでシーズン遷移フックを回す
        /// （複数週まとめて進めても引退週を跳び越さない）。
        /// </summary>
        public static void Advance(int delta = 1)
        {
            if (delta >= 0)
            {
                for (var i = 0; i < delta; i++)
                {
                    var prevYear = YearIndex;
                    Step(1);
                    if (YearIndex != prevYear)
                    {
                        // 年度替わり（4月）: AI校も進級・新入生加入・逆算配分成長（#80）。背景の全国裏試合を
                        // 完了させてからロスターを触る（背景タスクとロスター変更を重ねない）。
                        NationBackgroundSim.EnsureCompleted();
                        NationService.AdvanceAiYear(YearIndex);
                    }
                    EnterWeek();
                }
                return;
            }
            Step(delta);   // 巻き戻し（練習画面のプレビュー）は遷移フックを回さない
        }

        /// <summary>週インデックスの加算だけ（遷移フックなし）。</summary>
        private static void Step(int delta)
        {
            var w = Week + delta;
            while (w >= Calendar.WeeksPerYear) { w -= Calendar.WeeksPerYear; YearIndex++; }
            while (w < 0 && YearIndex > 1) { w += Calendar.WeeksPerYear; YearIndex--; }
            Week = w < 0 ? 0 : w;
        }

        /// <summary>
        /// 週に入った直後のシーズン遷移（設計書03 §2 / 05 §1.1 / 09 §8）。大会開幕週と新チーム発足週
        /// （夏の引退）を単一フック <see cref="SeasonTransitions.OnWeekEntered"/> でまとめて拾い、
        /// どのタブから週を進めても同じ地点で処理する（issue #134: 大会入りをホーム限定にしない）。
        /// UI演出（開幕バナー・試合ダイアログ）はここでは行わず、Home 側が <see cref="GameSession"/> の
        /// フラグを見て出す（遷移とUI提示を分離）。非Homeタブからの遷移は BannerPending を見て
        /// <see cref="ScreenRouter"/> がホームへ回送する。
        /// </summary>
        private static void EnterWeek()
        {
            var t = SeasonTransitions.OnWeekEntered(RosterService.Roster, Week, Calendar);

            // 大会開幕週なら大会モードへ入る（要件1）。二重入場を避けるため通常モードのときだけ。
            if (t.TournamentStarting is { } kind && GameSession.Current.Mode == GameMode.Normal)
                TournamentEntry.Enter(kind);

            // 新チーム発足（夏の3年引退の翌週）: 主将指名の導線（NewTeamService）を開き、AI校も代替わりさせる。
            if (t.NewTeam != null)
            {
                NewTeamService.Open(t.NewTeam);
                // AI校も夏後に代替わり: 進行中の全国裏試合を完了させてから3年生を引退させる（#80・秋は下級生のみ）。
                NationBackgroundSim.EnsureCompleted();
                NationService.RetireAiGraduates();
            }
        }

        /// <summary>シーズン頭（週0・年度1）へ戻す。</summary>
        public static void Reset() { Week = 0; YearIndex = 1; }

        /// <summary>共通トップバー「現在」表示（YYYY年M月W週目）。</summary>
        public static string CurrentLabel() => SeasonClock.CurrentLabel(YearIndex, Week);

        /// <summary>夏予選開幕までの残り週（0以下＝開催中）。トップバーのカウントダウンの単一ソース。</summary>
        public static int WeeksUntilSummer => Calendar.SummerTournamentStartWeek - Week;
    }
}
