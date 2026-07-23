using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Practice;
using KokoSim.Engine.Season;

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
        private static FacilitySet _facilities;

        /// <summary>監督（資金は万円単位。初期値は従来のホーム表示に合わせて120万円）。</summary>
        public static Manager Manager => _manager ?? (_manager = new Manager { Funds = 120 });

        /// <summary>
        /// 学校の施設保有状態（Issue #128）の単一ソース。funds→施設購入はここを更新し、
        /// シーズンの練習効率・週練習時間は SeasonContext.Facilities にこれを注入して反映する。
        /// カタログは <see cref="Catalog"/>（C#既定＝data/coefficients.yaml facilities: と同値）。
        /// </summary>
        public static FacilitySet Facilities => _facilities ?? (_facilities = new FacilitySet());

        /// <summary>施設カタログ（効果・費用・上限の参照元, Issue #128）。</summary>
        public static FacilityCatalog Catalog { get; } = FacilityCatalog.Default;

        /// <summary>練習試合の申込窓口（週1制約・資金・受諾判定を保持）。</summary>
        public static PracticeMatchScheduler Practice =>
            _practice ?? (_practice = new PracticeMatchScheduler(PracticeCoefficients));

        /// <summary>
        /// 年をまたいでも単調増加する絶対週番号（週1制約のキー）。GameClock の年度＋週から導く。
        /// </summary>
        public static int AbsoluteWeek => (GameClock.YearIndex - 1) * 50 + GameClock.Week;

        /// <summary>
        /// 試合の委任AIが読む采配値（設計書09/11）。旧ハードコード70を廃し、成長した監督の実値を単一入口で返す
        /// （issue #171）。0-99 帯の TacticalSense を int へ丸めて渡す。
        /// </summary>
        public static int TacticalSenseForAi =>
            (int)System.Math.Round(MathUtil.Clamp(Manager.TacticalSense, 0, 99));

        // 監督成長の二重適用ガード（issue #171）。年度替わりは GameClock が複数回叩きうるため、
        // 「終了した年度」を1回だけ処理する。0＝未適用。
        private static int _lastGrownYear;
        // 前回成長適用時点の自校 通算成績スナップショット（このシーズンぶんの勝数・甲子園到達を差分で取る）。
        private static int _grownWinsBaseline;
        private static int _grownKoshienBaseline;

        /// <summary>直近の年度替わりで発火した監督成長イベント（ホーム通知フィード用・任意消費）。</summary>
        public static IReadOnlyList<ManagerGrowthNotice> LastGrowthNotices { get; private set; } =
            System.Array.Empty<ManagerGrowthNotice>();

        /// <summary>
        /// 監督成長の内部状態をリセットする（新規ゲーム開始時。<see cref="GameClock.Reset"/> と対で呼ぶ）。
        /// </summary>
        public static void ResetGrowth()
        {
            _lastGrownYear = 0;
            _grownWinsBaseline = 0;
            _grownKoshienBaseline = 0;
            LastGrowthNotices = System.Array.Empty<ManagerGrowthNotice>();
        }

        /// <summary>
        /// 終了した年度 <paramref name="endedYearIndex"/> の自校年間成績を集計し、監督メタ（指導力・采配・
        /// 育成眼・名声・信頼度・資金）と節目イベントを1年ぶん適用する（issue #171・設計書04 §1.1/§1.1b）。
        /// 年間成績は自校（<see cref="NationService.ManagerSchoolId"/>）の通算戦績スナップショット差分から取る
        /// （夏の県予選優勝＝甲子園到達。全国優勝は甲子園本戦の実プレイ接続まで false）。
        /// 二重適用しない（同じ年度を複数回呼んでも1回だけ）。節目イベントの抽選は母種由来の独立ストリームで引き、
        /// 本編の乱数列を乱さない（不変条件#2 決定論・同母種同結果）。
        /// </summary>
        public static void ApplyYearlyGrowth(int endedYearIndex)
        {
            if (endedYearIndex <= _lastGrownYear) return;   // 二重適用ガード

            // 自校の通算戦績から、このシーズンぶんの勝数・甲子園到達を差分で取る。
            var record = GameSession.Current.Records.For(NationService.ManagerSchoolId);
            var wins = System.Math.Max(0, record.OfficialWins - _grownWinsBaseline);
            var reachedKoshien = record.SummerAppearances > _grownKoshienBaseline;

            // 節目イベント抽選は母種＋年度から導く独立ストリーム（本編の乱数列を乱さない・同母種同結果）。
            var milestoneRng = new Xoshiro256Random(GameSeed.Master ^ (0xA6A6_0000UL ^ (ulong)endedYearIndex));

            // 全国優勝は甲子園本戦が実プレイに接続する（tournament Phase 3）まで到達不能＝false 固定。
            LastGrowthNotices = ManagerSeasonUpdate.Apply(
                Manager, reachedKoshien, nationalChampion: false, wins, milestoneRng);

            _lastGrownYear = endedYearIndex;
            _grownWinsBaseline = record.OfficialWins;
            _grownKoshienBaseline = record.SummerAppearances;
        }
    }
}
