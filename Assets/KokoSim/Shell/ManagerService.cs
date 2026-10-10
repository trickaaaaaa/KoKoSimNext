using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation.Tournaments;
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
        private static ContractSet _contracts;

        /// <summary>
        /// 監督（資金は万円単位）。**新設校スタート水準**（issue #585 確定事項5）＝
        /// 部費60万・施設基金0・名声0・信頼30。
        ///
        /// 施設基金0は「初年度は建物が一切買えない」を意味する（最安の医務室Lv1でも150万）。
        /// 名声0は実績ゼロの新任＝新入生の質（talentCenter）も底から始まる。信頼30は
        /// 中立(50)より下＝学校からも選手からもまだ信用されていない状態。以降は名声と戦績で増える
        /// （学校の設備投資100万＋名声×20万＋甲子園1000万＋全国優勝5000万）。
        /// </summary>
        public static Manager Manager =>
            _manager ?? (_manager = new Manager { Funds = 60, FacilityFund = 0, Fame = 0, Trust = 30 });

        /// <summary>
        /// 学校の施設保有状態（Issue #128）の単一ソース。funds→施設購入はここを更新し、
        /// シーズンの練習効率・週練習時間は SeasonContext.Facilities にこれを注入して反映する。
        /// カタログは <see cref="Catalog"/>（C#既定＝data/coefficients.yaml facilities: と同値）。
        /// </summary>
        public static FacilitySet Facilities => _facilities ?? (_facilities = new FacilitySet());

        /// <summary>施設カタログ（効果・費用・上限の参照元, Issue #128）。</summary>
        public static FacilityCatalog Catalog { get; } = FacilityCatalog.Default;

        /// <summary>
        /// 転任時: 施設は学校資産＝置いていく（設計書04 §4、OPEN-QUESTIONS Q6）。施設基金も学校の設備投資・
        /// 寄付の積み上げなので同様にゼロへ戻す。部費・名声は監督個人の実績として持ち越す（ここでは触らない）。
        /// </summary>
        internal static void ResetFacilitiesForTransfer()
        {
            _facilities = new FacilitySet();
            Manager.FacilityFund = 0;
        }

        /// <summary>
        /// 年契約の契約状態（設計書04 §1.4a）の単一ソース。買い切りの施設と違い、年額を毎年部費から払い、
        /// いつでも段を下げて解約できる。新規ゲームの既定はスカウト網 Lv1 だけ＝従来のスカウト費15万/年と同額。
        /// </summary>
        public static ContractSet Contracts => _contracts ?? (_contracts = ContractSet.Initial());

        /// <summary>年契約カタログ（年額・効果・上限の参照元）。C#既定＝data/coefficients.yaml contracts: と同値。</summary>
        public static ContractCatalog ContractList { get; } = ContractCatalog.Default;

        /// <summary>
        /// 契約中の年契約をまとめた実効値。年額・新入生の才能加算・分野別指導力の上乗せ・遠征可否を
        /// ここから配る（各所で Aggregate を呼び直さない）。
        /// </summary>
        public static ContractEffects ContractEffects => ContractList.Aggregate(Contracts);

        /// <summary>練習試合の申込窓口（週1制約・資金・受諾判定を保持）。</summary>
        public static PracticeMatchScheduler Practice =>
            _practice ?? (_practice = new PracticeMatchScheduler(PracticeCoefficients));

        /// <summary>
        /// 年をまたいでも単調増加する絶対週番号（週1制約のキー）。GameClock の年度＋週から導く。
        /// </summary>
        public static int AbsoluteWeek => GameClock.AbsoluteWeekOf(GameClock.YearIndex, GameClock.Week);

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
        // 今年度の自校公式戦の番狂わせ名声デルタ（試合ごと）。大会の確定時に積み、年度末の成長で畳み込んで消費する
        // （2026-08-15・issue #170 の実ゲーム配線）。engine の CareerEngine（sim）と同じ FameUpsetModel を通す。
        private static readonly List<double> _pendingUpsetDeltas = new List<double>();
        private static readonly CareerCoefficients CareerCoeff = new CareerCoefficients();

        /// <summary>
        /// 監督メタ・資金の係数（C#既定＝<c>data/coefficients.yaml career:</c> と同値・不変条件#4）。
        /// 実ゲームで資金を動かす側（甲子園の激励金など）が同じ実体を引くための入口＝
        /// 呼び出し側で <c>new CareerCoefficients()</c> を作らない。
        /// </summary>
        public static CareerCoefficients Coefficients => CareerCoeff;

        /// <summary>直近の年度替わりで発火した監督成長イベント（ホーム通知フィード用・任意消費）。</summary>
        public static IReadOnlyList<ManagerGrowthNotice> LastGrowthNotices { get; private set; } =
            System.Array.Empty<ManagerGrowthNotice>();

        /// <summary>
        /// 直近の年度更新で監督メタが<b>実際にどれだけ動いたか</b>（issue #612）。
        /// 節目イベントの種別から効果を推測せず、適用の前後を測って作る＝
        /// engine の中身（どの分野が伸びたか・自然増と節目の合算）と食い違わない。
        /// </summary>
        public static ManagerGrowthDelta LastGrowthDelta { get; private set; } = new ManagerGrowthDelta();

        /// <summary>
        /// 発火した節目イベントと実測の差分を取り出して<b>空にする</b>（issue #612）。
        /// 提示は1回だけ＝週送りのたびに同じ話が出ないようにする。
        /// </summary>
        public static IReadOnlyList<ManagerGrowthNotice> DrainGrowthNotices()
        {
            var notices = LastGrowthNotices;
            LastGrowthNotices = System.Array.Empty<ManagerGrowthNotice>();
            return notices;
        }

        /// <summary>
        /// 監督成長の内部状態をリセットする（新規ゲーム開始時。<see cref="GameClock.Reset"/> と対で呼ぶ）。
        /// </summary>
        public static void ResetGrowth()
        {
            _lastGrownYear = 0;
            _grownWinsBaseline = 0;
            _grownKoshienBaseline = 0;
            _pendingUpsetDeltas.Clear();
            LastGrowthNotices = System.Array.Empty<ManagerGrowthNotice>();
        }

        /// <summary>
        /// 大会1回ぶんのブラケットから自校の公式戦を拾い、番狂わせの名声デルタ（金星/取りこぼし）を
        /// <b>その時点の自校の強さ</b>で試合ごとに積む（設計書04 §1.2・issue #170）。年度末の
        /// <see cref="ApplyYearlyGrowth"/> が畳み込んで消費する。自校が居ない大会は何も積まない。乱数を引かない。
        /// <para>強さを試合時点で固定するのは、実ゲームの自校の強さがロスター由来で季節ごとに動くため
        /// （夏は3年生込み・3月は新チーム）。年度末の1値で評価すると「格上に勝った」が誇張される。</para>
        /// </summary>
        public static void RecordTournamentUpsets(IReadOnlyList<BracketMatch> matches)
        {
            if (matches == null) return;
            var self = NationService.ManagerSchool().Strength;
            foreach (var m in matches)
            {
                if (!m.ManagerInvolved) continue;
                var won = m.WinnerId == NationService.ManagerSchoolId;
                var opponent = NationService.SchoolById(won ? m.LoserId : m.WinnerId);
                if (opponent == null) continue;
                _pendingUpsetDeltas.Add(FameUpsetModel.MatchDelta(self, opponent.Strength, won, CareerCoeff));
            }
        }

        /// <summary>今年度に積まれた番狂わせデルタの畳み込み値（表示・デバッグ用。消費しない）。</summary>
        public static double PendingUpsetFameDelta => FameUpsetModel.SeasonDelta(_pendingUpsetDeltas, CareerCoeff);

        /// <summary>セーブ保存: 監督メタ・施設・年契約・成長の二重適用ガードを DTO へ写す（issue #507）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            s.Manager = Manager;
            s.Facilities = Facilities;
            s.Contracts = Contracts;
            s.ManagerLastGrownYear = _lastGrownYear;
            s.ManagerGrownWinsBaseline = _grownWinsBaseline;
            s.ManagerGrownKoshienBaseline = _grownKoshienBaseline;
            s.ManagerPendingUpsetDeltas = new List<double>(_pendingUpsetDeltas);
        }

        /// <summary>セーブ復元: 監督メタ・施設・年契約・成長ガードを据える（練習スケジューラは作り直し）。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s)
        {
            _manager = s.Manager;
            _facilities = s.Facilities;
            _contracts = s.Contracts;
            _lastGrownYear = s.ManagerLastGrownYear;
            _grownWinsBaseline = s.ManagerGrownWinsBaseline;
            _grownKoshienBaseline = s.ManagerGrownKoshienBaseline;
            _pendingUpsetDeltas.Clear();
            if (s.ManagerPendingUpsetDeltas != null) _pendingUpsetDeltas.AddRange(s.ManagerPendingUpsetDeltas);
            _practice = null;
            LastGrowthNotices = System.Array.Empty<ManagerGrowthNotice>();
        }

        /// <summary>新規ゲームの完全リセット: 監督メタ・施設・年契約・練習・成長ガードを初期状態へ。</summary>
        public static void Reset()
        {
            _manager = null;
            _facilities = null;
            _contracts = null;
            _practice = null;
            ResetGrowth();
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

            // 全国優勝はその年度の甲子園を自校が制したか（設計書05 §1.1a）。歴代大会結果は
            // 「自校が1試合でも戦った大会」だけを年度つきで残すので、ここが唯一の判定入口
            // （通算戦績 BestResult は年をまたぐ最高成績なので「今年優勝したか」は答えられない）。
            var nationalChampion = false;
            foreach (var e in GameSession.Current.History.Entries)
                if (e.Kind == TournamentKind.SummerKoshien && e.Year == endedYearIndex && e.ManagerIsChampion)
                {
                    nationalChampion = true;
                    break;
                }

            // 年契約（設計書04 §1.4a）の年額は部費からここで引かれる。既定（スカウト網 Lv1）なら
            // 従来のスカウト費15万と同額＝年契約を入れても初期状態の収支は変わらない。
            // 適用の前後を測って差分を作る（issue #612）。種別から効果を推測すると、自然増や
            // 「弱点分野が伸びる」ような engine 側の分岐と食い違う。
            var before = ManagerGrowthDelta.Snapshot(Manager);

            // 番狂わせ加減点（issue #170）: 大会確定時に積んだ試合ごとのデルタを畳み込む（上昇側は季8上限）。
            // 二重適用ガードの内側で消費するので、同じ年度を複数回叩いても1回だけ効く。
            var upsetFameDelta = FameUpsetModel.SeasonDelta(_pendingUpsetDeltas, CareerCoeff);
            _pendingUpsetDeltas.Clear();

            LastGrowthNotices = ManagerSeasonUpdate.Apply(
                Manager, reachedKoshien, nationalChampion, wins, milestoneRng,
                upsetFameDelta: upsetFameDelta, contracts: ContractEffects);

            LastGrowthDelta = ManagerGrowthDelta.Since(before, Manager);

            // 転任判定（設計書04 §1.3）。成長更新の直後の Fame/Trust を判定に使う
            // （CareerEngine.Run と同じ順序: 自然増→節目→フリー化資格/残留判定）。
            // 独立ストリーム（milestoneRng とは別タグ）で本編の乱数列を乱さない（不変条件#2）。
            var transferRng = new Xoshiro256Random(GameSeed.Master ^ (0xA6A7_0000UL ^ (ulong)endedYearIndex));
            var transferResult = ManagerTransferModel.Evaluate(
                Manager, NationService.PrefectureId, NationService.Nation, transferRng, CareerCoeff);
            ManagerTransferService.Open(transferResult);

            _lastGrownYear = endedYearIndex;
            _grownWinsBaseline = record.OfficialWins;
            _grownKoshienBaseline = record.SummerAppearances;
        }
    }
}
