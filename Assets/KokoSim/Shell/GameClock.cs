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
                        // issue #529: 自校敗退後の残りブラケット背景消化も、同じロスター（NationService.Rosters）
                        // を読むため同様に完了を待ってからロスターへ触る。
                        BracketFinishSim.EnsureCompleted();
                        NationService.AdvanceAiYear(YearIndex);
                        // 終了した年度ぶんの監督メタ更新（指導力・采配・名声・信頼度・節目イベント, issue #171）。
                        // 夏/秋の大会はこの年度内に消化済み＝自校の年間成績が確定した後で1年1回だけ適用する。
                        ManagerService.ApplyYearlyGrowth(prevYear);
                        // issue #535: 自校ロスターも進級・卒業・新入生を適用する（従来は AI校だけで、自校は
                        // 年度替わりで一切触られず学年が固定されていた）。ロジックは engine の RosterLifecycle.AdvanceYear。
                        // 監督成長（名声更新）の後に呼ぶ＝今季の成績で上がった名声が新入生の質へ効く（design-04 §1）。
                        RosterService.AdvanceYear(YearIndex);
                    }
                    EnterWeek();
                }
                // オートセーブは廃止（監督決定 2026-08-09）。保存はプレイヤーが手動3枠へ行うものだけにする＝
                // 週送りはファイル IO を一切起こさない。以前はここでオート枠を上書きしていたが、
                // 「大会に入った週だけ CanSave が false で黙って書かれない」という穴があり、
                // オートを当てにすると大会前まで巻き戻る事故になっていた（issue #509 の一部を撤回）。
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
                BracketFinishSim.EnsureCompleted();   // issue #529: 同じロスターを読むため同様に完了を待つ。
                NationService.RetireAiGraduates();
            }
        }

        /// <summary>シーズン頭（週0・年度1）へ戻す。監督成長の年度ガードも初期化する（issue #171）。</summary>
        public static void Reset() { Week = 0; YearIndex = 1; ManagerService.ResetGrowth(); }

        /// <summary>セーブ保存: 暦を DTO へ写す（issue #507）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s) { s.Week = Week; s.YearIndex = YearIndex; }

        /// <summary>セーブ復元: 暦を据える。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s) { Week = s.Week; YearIndex = s.YearIndex; }

        /// <summary>共通トップバー「現在」表示（YYYY年M月W週目）。</summary>
        public static string CurrentLabel() => SeasonClock.CurrentLabel(YearIndex, Week);

        /// <summary>年をまたいでも単調増加する絶対週番号。暦の刻み（<see cref="SeasonCalendar.WeeksPerYear"/>）を
        /// 使う唯一の場所にする（<see cref="ManagerService.AbsoluteWeek"/> もここを通す）。</summary>
        public static int AbsoluteWeekOf(int yearIndex, int week) => (yearIndex - 1) * Calendar.WeeksPerYear + week;

        /// <summary>夏予選開幕までの残り週（0以下＝開催中）。トップバーのカウントダウンの単一ソース。</summary>
        public static int WeeksUntilSummer => Calendar.SummerTournamentStartWeek - Week;
    }
}
