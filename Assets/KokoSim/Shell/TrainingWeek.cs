using System.Collections.Generic;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 週次練習の単一ソース（設計書03 §3.1 / issue #484）。
    ///
    /// 以前は経路が2本に割れていた:
    ///   ・ホームの「今週を進める」＝固定7周期メニューを全部員へ一律適用（配分も指導力も見ない）
    ///   ・練習計画画面のプレビュー＝配分を使うが実選手のクローンへドライラン（実選手は伸びない）
    /// この2本を1本に束ねる。実適用（<see cref="Home.HomeState"/>）もプレビュー
    /// （<see cref="Training.TrainingPlanState"/>）も <see cref="Apply"/> を呼ぶので、
    /// プレビューバーと実際の伸びが同じ入力・同じエンジンAPIから出ることを保証する。
    ///
    /// 呼び方は engine の <c>SeasonEngine.cs:150-157</c> と同一:
    ///   選手ごとの <see cref="DevelopingPlayer.Plan"/>（null=お任せ）を実効配分へ解決し、
    ///   監督の分野別指導力（#115）・性格の素直さ（design-01）・体質スキル（design-10）・
    ///   施設連動の週練習時間と施設係数（#128）を全て通す。
    /// 係数は全て C#既定（data/ の YAML と一致・不変条件#4）。施設なし・監督基準では従来と1ビット一致。
    /// UnityEngine 非依存の純データ（設計書06 §1）。
    /// </summary>
    public static class TrainingWeek
    {
        // 係数は全画面で同一の C#既定を共有する（プレビューと実適用が別インスタンスで食い違わないための単一ソース）。
        private static readonly SeasonCalendar Calendar = new SeasonCalendar();
        private static readonly GrowthStageTable Stages = new GrowthStageTable();
        private static readonly TrainingCoefficients Training = new TrainingCoefficients();
        private static readonly SkillCoefficients Skills = new SkillCoefficients();
        private static readonly PersonalityCoefficients Personalities = new PersonalityCoefficients();

        /// <summary>
        /// 施設連動の週練習時間[分]（Issue #128）。基準（<see cref="TrainingCoefficients.DefaultBudgetMinutes"/>）へ
        /// 施設保有状態の加算を足す＝ engine の SeasonContext.ResolveFacility と同じ集約を Unity 側でも通す。
        /// 施設Lv0で加算0＝従来の300分と一致。
        /// </summary>
        public static int Budget => Training.DefaultBudgetMinutes + Effects.BudgetAdd;

        /// <summary>
        /// 施設・道具・備品の効果を全軸まとめて解決する（engine の SeasonContext.ResolveEffects と同じ集約）。
        /// 何も持っていなければ全倍率1.0・全加算0＝従来と1ビット一致（不変条件#2）。
        /// </summary>
        public static FacilityEffects Effects =>
            ManagerService.Catalog.AggregateEffects(ManagerService.Facilities);

        /// <summary>施設連動の施設係数（週成長の乗数, Issue #128）。施設Lv0で 1.0＝従来一致。</summary>
        private static double FacilityCoef => Training.FacilityCoef + Effects.CoefAdd;

        /// <summary>
        /// 指定週の合宿倍率（設計書03 §4）。プレビューのバナー表示と実適用が同じ値を使うための単一ソース。
        /// </summary>
        public static double CampMultiplier(int week) => Calendar.CampMultiplier(week, Training);

        /// <summary>その週にこの選手が練習できるか（学年別カレンダー・引退・オフ期間）。</summary>
        public static bool CanTrain(int grade, int week) => Calendar.CanTrain(grade, week);

        /// <summary>
        /// 1週ぶんの練習を選手 <paramref name="p"/> へ適用する（<paramref name="p"/> を破壊的に更新）。
        /// <paramref name="extraMult"/> は大会モード時の効果低下（&lt;1.0）に使う（通常週は1.0）。
        /// <paramref name="roster"/> はチーム内スキル（練習リーダー/お手本, 設計書10）の判定に使う全部員
        /// （null=判定なし＝従来一致）。練習不可週（<see cref="CanTrain"/> が false）は何もしない。
        ///
        /// プレビューは実選手のクローンにこれを呼ぶ（非破壊ドライラン）。実選手の
        /// <see cref="DevelopingPlayer.Clone"/> は Plan・IndividualCoaching を引き継ぐので、
        /// クローンへの適用結果＝実選手への適用結果（プレビューバー＝実際の伸び）。
        /// </summary>
        public static void Apply(
            DevelopingPlayer p, int week, double extraMult = 1.0, IReadOnlyList<DevelopingPlayer>? roster = null)
        {
            if (!Calendar.CanTrain(p.Grade, week)) return;

            var isPitcher = PitcherRoleResolver.LeansPitcher(p);
            var plan = p.Plan ?? TrainingPlan.Auto(isPitcher);
            var alloc = TrainingPresets.Resolve(plan, isPitcher, Budget);
            var stage = Calendar.StageIndex(p.Grade, week);
            var campMult = Calendar.CampMultiplier(week, Training) * extraMult;
            // 施設係数を焼き込んだ係数で走らせる（engine SeasonEngine と同じ: training with { FacilityCoef }）。
            var training = Training with { FacilityCoef = FacilityCoef };
            // 専門コーチ（年契約・設計書04 §1.4a）は雇っている分野の指導力だけを底上げする。
            // 監督自身の指導力は変えない＝解約すればその年から元に戻る。
            var coaching = CoachingProfile.FromManager(ManagerService.Manager, ManagerService.ContractEffects);

            DevelopmentModel.TrainWeekPlan(
                p, alloc, training.ReferenceWeekMinutes,
                stage, campMult, Stages, training, Skills, Personalities, coaching,
                // 練習道具は「振っているメニューの分野」にだけ効く（施設係数は全メニュー一律）。
                Effects.Equipment, roster);
        }
    }
}
