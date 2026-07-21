using KokoSim.Engine.Career;
using KokoSim.Engine.Practice;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 監督メタ（設計書04 §1）の単一ソース。資金・名声・信頼度はここが持ち、ホームのステータス表示も
    /// 練習試合の費用減算も同じインスタンスを見る（画面ごとに別の値を持たない）。
    /// 練習試合の週1制約・受諾判定を担う <see cref="PracticeMatchScheduler"/> もセッション横断でここに置く。
    /// UnityEngine 非依存の純データ（設計書06 §1）。
    /// </summary>
    public static class ManagerService
    {
        /// <summary>係数の既定値（data/coefficients.yaml practice_match: と同値）。</summary>
        private static readonly PracticeMatchCoefficients PracticeCoefficients = new PracticeMatchCoefficients();

        private static Manager _manager;
        private static PracticeMatchScheduler _practice;

        /// <summary>監督（資金は万円単位。初期値は従来のホーム表示に合わせて120万円）。</summary>
        public static Manager Manager => _manager ?? (_manager = new Manager { Funds = 120 });

        /// <summary>練習試合の申込窓口（週1制約・資金・受諾判定を保持）。</summary>
        public static PracticeMatchScheduler Practice =>
            _practice ?? (_practice = new PracticeMatchScheduler(PracticeCoefficients));

        /// <summary>
        /// 年をまたいでも単調増加する絶対週番号（週1制約のキー）。GameClock の年度＋週から導く。
        /// </summary>
        public static int AbsoluteWeek => (GameClock.YearIndex - 1) * 50 + GameClock.Week;
    }
}
