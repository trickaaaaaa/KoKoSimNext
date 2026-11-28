using KokoSim.Engine.Nation;   // Tiers
using KokoSim.Engine.Saves;    // SaveSummary
using KokoSim.Engine.Season;   // TournamentKindNames

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// セーブデータ画面に出す概要（時期・チーム総合・監督通算・甲子園出場）を、いまの進行から作る。
    /// 保存のとき（<see cref="SaveGameIO.Capture"/>）にセーブへ一緒に書き、一覧のカードは本体を組み立てずに
    /// それを読む。画面の右列（いま遊んでいるデータ）も同じ値を出す＝計算はここだけ。
    /// </summary>
    public static class SaveSummaries
    {
        /// <summary>いまの進行の概要。</summary>
        public static SaveSummary Current()
        {
            var s = GameSession.Current;
            // 監督通算＝退任済みの赴任ぶん＋今の赴任ぶん（自校の通算から就任時の基準を引いた差分）。
            var record = s.Records.For(NationService.ManagerSchoolId);
            var (wins, losses, summer, spring) = s.PostingHistory.CurrentDelta(record);
            foreach (var p in s.PostingHistory.Closed)
            {
                wins += p.Wins;
                losses += p.Losses;
                summer += p.SummerAppearances;
                spring += p.SpringAppearances;
            }
            // 点数は上の帯（TopBarMeters）と同じ丸め。
            var overall = (int)System.Math.Round(TeamOverall.Of(RosterService.Active));
            // カードの色の帯に塗るスクールカラー（ホームと同じ表示用の色＝色味の無い色は炭色・白文字で読める暗さ）。
            var color = "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(SchoolAccent.ForManager());
            return new SaveSummary(Phase(), overall, wins, losses, spring, summer) { Color = color };
        }

        /// <summary>時期の名前。大会中はその大会の呼び名、それ以外は次の大会の呼び名＋「前」。</summary>
        public static string Phase()
        {
            var s = GameSession.Current;
            return s.InTournament
                ? TournamentKindNames.Short(s.Kind)
                : TournamentKindNames.Short(GameClock.NextTournament.Kind) + "前";
        }

        /// <summary>総合の点数 → ランク（S〜G）。</summary>
        public static string GradeOf(int overall) => Tiers.FromStrength(overall).ToString();
    }
}
