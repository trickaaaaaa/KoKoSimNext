using UnityEngine.UIElements;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// トップバーの日付欄（上に年と時期の小書き・下に明朝の日付）の充填ロジック。
    /// 旧・電光掲示板（升目）の入口をそのまま残し、全画面がこの1経路で日付を埋める
    /// （画面ごとに週の書式を作らない。呼ばない画面は UXML の既定値が出たままになる）。
    /// 大会期間は実月日（「9月13日」）、大会のない時期は週（「4月1週」）。
    /// トップバーの進行ボタンの文言（大会中は「1日進める」）もここで埋める（issue #1534 項目9）。
    /// </summary>
    public static class ScoreboardStrip
    {
        /// <summary>全画面共通の入口（大会中は小書きに大会の呼び名を添える）。</summary>
        public static void Fill(VisualElement root) => Fill(root, null, null, null);

        /// <summary>
        /// いまの戦績（見出しと「N勝N敗」）。大会中はいまの大会の勝敗、それ以外は今季の公式戦の勝敗。
        /// 公式戦の戦績（MatchHistory）が単一ソース。上の帯とセーブデータ画面の右列が読む。
        /// </summary>
        public static (string Label, string Text) RecordNow()
        {
            var s = GameSession.Current;
            int won = 0, lost = 0, drawn = 0;
            foreach (var m in s.MatchHistory.ForSchool(KokoSim.Engine.Stats.MatchRecordBook.ManagerKey))
            {
                if (m.Year != s.Year) continue;
                if (s.InTournament && m.Kind != s.Kind) continue;
                if (m.Won) won++; else if (m.Lost) lost++; else drawn++;
            }
            return (s.InTournament ? TournamentKindNames.Short(s.Kind) + "の戦績" : "今季の公式戦",
                won + "勝" + lost + "敗" + (drawn > 0 ? drawn + "分" : ""));
        }

        /// <summary>
        /// いまの日付（上の帯の升目と同じ）。大会中は大会内の実月日、結果を閉じるまでは最終日、それ以外は「M月W週」。
        /// </summary>
        public static string DateNow()
        {
            var s = GameSession.Current;
            return s.InTournament
                ? s.TournamentDateLabel
                : !string.IsNullOrEmpty(s.WrapUpDateLabel)
                    ? s.WrapUpDateLabel   // 結果を閉じるまでは最終日の日付（止まった開幕週に戻さない・issue #1548）
                    : SeasonClock.CompactLabel(GameClock.YearIndex, GameClock.Week);
        }

        /// <summary>
        /// 旧シグネチャ（カウントダウンを呼び出し側が決める版）。カウントダウンはトップバーから外した
        /// （ホームの主役のカードが持つ）ので、引数は読まずに日付だけ埋める。
        /// </summary>
        public static void Fill(VisualElement root, string countdownLabel, string countdownValue, string weekSuffix)
        {
            if (root == null) return;
            var s = GameSession.Current;

            var year = SeasonClock.YearLabel(GameClock.YearIndex, GameClock.Week);
            var sup = root.Q<Label>("week-year");
            // 大会中は正式な大会名（「2026年 秋季神奈川県地区予選（川崎地区）」。年は大会名に含まれる）。
            if (sup != null) sup.text = s.InTournament && !string.IsNullOrEmpty(s.Title) ? s.Title : year;

            // 大会中は GameClock.Week が開幕時点で凍結する（issue #583）＝大会内の実月日を出す。
            // 戦績（大会中はいまの大会の勝敗、それ以外は今季の公式戦の勝敗）。公式戦の戦績（MatchHistory）が単一ソース。
            var recordK = root.Q<Label>("tb-record-k");
            var record = root.Q<Label>("tb-record");
            if (recordK != null || record != null)
            {
                var (label, text) = RecordNow();
                if (recordK != null) recordK.text = label;
                if (record != null) record.text = text;
            }

            var date = root.Q<Label>("tb-date");
            if (date != null) date.text = DateNow();

            // 進行ボタン（issue #1534 項目9）: 大会中はどのタブでも「1日進める」。文言はここだけが決める。
            var advance = root.Q<Button>("advance");
            if (advance != null) advance.text = s.InTournament ? "1日進める ▶" : "今週を進める ▶";
        }
    }
}
