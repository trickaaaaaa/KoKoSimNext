using KokoSim.Engine.Core;
using KokoSim.Engine.Season.Events;

namespace KokoSim.Engine.Season;

/// <summary>シーズンループの全設定（YAML駆動 ＋ イベント定義）。</summary>
public sealed record SeasonContext
{
    public SeasonCalendar Calendar { get; init; } = new();
    public GrowthStageTable Stages { get; init; } = new();
    public TrainingCoefficients Training { get; init; } = new();
    public RosterCoefficients Roster { get; init; } = new();
    public Players.FormCoefficients Form { get; init; } = new();
    public Players.SkillCoefficients Skills { get; init; } = new();
    public Players.PersonalityCoefficients Personalities { get; init; } = new();
    public InjuryCoefficients Injury { get; init; } = new();
    /// <summary>傷病カタログ（data/injuries.yaml）。種類→部位→段階の一貫抽選に使う（設計書03 §3.5）。</summary>
    public Players.InjuryCatalog InjuryCatalog { get; init; } = Players.InjuryCatalog.Default;
    public GrowthEventCoefficients Growth { get; init; } = new();
    public IReadOnlyList<GameEvent> Events { get; init; } = Array.Empty<GameEvent>();

    /// <summary>
    /// 週あたり練習時間[分]（施設供給, 設計書03 §3.1/§4）。null = 施設レベル（<see cref="FacilityLevel"/>）から解決、
    /// それも無ければ Training.DefaultBudgetMinutes を使う。将来 School/施設Lv から算出して注入する口。
    /// </summary>
    public int? BudgetMinutes { get; init; }

    /// <summary>
    /// 監督の分野別指導力（Issue #115・設計書04 §1）。null＝基準指導力（Training.CoachingLevel）を全分野に適用＝
    /// 従来のシムと1ビット一致（不変条件#2）。CoachingProfile.FromManager で監督メタから注入する。
    /// </summary>
    public CoachingProfile? Coaching { get; init; }

    /// <summary>
    /// 学校の施設レベル（Issue #115・設計書03 §4 / 04）。0＝施設なし（現状の 1.0 / 300分）で従来一致。
    /// Training.FacilityTiers を index として引き、施設係数と週練習時間を決める。
    /// 資金・名声からレベルを上げる接続は将来（OPEN-QUESTIONS Q6）。ここは注入口のみ。
    /// </summary>
    public int FacilityLevel { get; init; }

    /// <summary>
    /// 施設レベル → (施設係数, 週練習時間[分]) の解決（Issue #115）。
    /// レベル0 または FacilityTiers 未指定なら Training の既定値をそのまま返す＝従来と1ビット一致。
    /// </summary>
    public (double FacilityCoef, int BudgetMinutes) ResolveFacility()
    {
        var defaultBudget = BudgetMinutes ?? Training.DefaultBudgetMinutes;
        var tiers = Training.FacilityTiers;
        if (FacilityLevel <= 0 || tiers.Count == 0)
            return (Training.FacilityCoef, defaultBudget);

        var idx = Math.Min(FacilityLevel, tiers.Count - 1);
        var tier = tiers[idx];
        // BudgetMinutes を明示注入した場合はそれを優先（施設係数のみ施設レベルから）。
        return (tier.Coef, BudgetMinutes ?? tier.BudgetMinutes);
    }
}

/// <summary>1年の記録。</summary>
public sealed record YearSnapshot(
    int Year, int RosterCount, double AvgLevelRegulars, double GraduatingAvgLevel, int EventsFired);

/// <summary>複数年キャリアの記録。</summary>
public sealed record CareerSummary(IReadOnlyList<YearSnapshot> Years)
{
    public int TotalEventsFired
    {
        get
        {
            var sum = 0;
            foreach (var y in Years) sum += y.EventsFired;
            return sum;
        }
    }
}

/// <summary>
/// シーズンループ（設計書03）。週ターンで練習→育成を回し、年度替わりで昇級・卒業・新入生を処理する。
/// 決定論（注入乱数）。DoD: 10年自動プレイが無エラー完走し、育成曲線が設計通り。
/// </summary>
public static class SeasonEngine
{
    public static CareerSummary Run(int years, SeasonContext ctx, IRandomSource rng)
    {
        var roster = new List<DevelopingPlayer>();
        var scheduler = new EventScheduler(ctx.Events);
        var snapshots = new List<YearSnapshot>(years);

        for (var year = 1; year <= years; year++)
        {
            // 年度替わり（4月）: 昇級 → 卒業 → 新入生。
            foreach (var p in roster) p.Grade++;
            var graduating = roster.Where(p => p.Grade > 3).ToList();
            var gradAvg = graduating.Count > 0 ? graduating.Average(p => p.AverageLevel()) : 0;
            roster.RemoveAll(p => p.Grade > 3);
            // 在籍部員の氏名を渡し、新入生の下の名前が既存部員と被らないようにする（重複回避）。
            roster.AddRange(ProspectGenerator.Intake(year, ctx.Roster, rng, skills: ctx.Skills,
                personalities: ctx.Personalities, existingNames: roster.Select(p => p.Name).ToList(),
                form: ctx.Form));

            // 主将の年度更新（設計書09 §8）: 3年生引退で主将が抜けたら選び直す。手動指名が在籍なら尊重。
            CaptainSelector.EnsureCaptain(roster);

            scheduler.ResetAnnualFlags();
            var eventsFired = 0;

            // 施設レベル→施設係数・週練習時間（Issue #115）。レベル0で従来と1ビット一致。
            var (facilityCoef, budget) = ctx.ResolveFacility();
            var training = ctx.Training with { FacilityCoef = facilityCoef };
            for (var week = 0; week < ctx.Calendar.WeeksPerYear; week++)
            {
                // 引退週フック（設計書03 §2 / 09 §8）: 夏の3年引退の翌週＝新チーム発足。3年を引退フラグ扱いに
                // （ロスターには残す。除去は年度替わりの卒業）し、新チームの最上級生から暫定主将を立てる。
                // 以降は指名ウィンドウ中だけプレイヤーが差し替える。
                RosterLifecycle.OnWeekEntered(roster, week, ctx.Calendar);

                var campMult = ctx.Calendar.CampMultiplier(week, ctx.Training);
                foreach (var p in roster)
                {
                    if (!ctx.Calendar.CanTrain(p.Grade, week)) continue;
                    // 選手ごとの練習計画（未設定はお任せ）を実効配分へ解決して1週ぶん適用（設計書03 §3.1）。
                    var plan = p.Plan ?? TrainingPlan.Auto(p.IsPitcher);
                    var alloc = TrainingPresets.Resolve(plan, p.IsPitcher, budget);
                    var stage = ctx.Calendar.StageIndex(p.Grade, week);
                    DevelopmentModel.TrainWeekPlan(
                        p, alloc, training.ReferenceWeekMinutes,
                        stage, campMult, ctx.Stages, training, ctx.Skills, ctx.Personalities, ctx.Coaching);
                }

                // 調子の週次更新（設計書02 §3.3: 数週間続く波）。
                // 独立ストリーム(Fork)で更新し、イベント抽選(root rng)の乱数列を乱さない（決定論の後方互換）。
                var formRng = rng.Fork(0xF0F0_0000UL ^ (ulong)(year * 100 + week));
                foreach (var p in roster)
                {
                    p.ConditionValue = Players.FormModel.NextWeeklyCondition(p.ConditionValue, formRng, ctx.Form);
                }

                // 怪我の週次処理（設計書03 §3.5: 発生判定→回復進行）。独立ストリーム。
                var injuryRng = rng.Fork(0x1213_0000UL ^ (ulong)(year * 100 + week));
                foreach (var p in roster)
                {
                    if (p.Injury == Players.InjurySeverity.None)
                    {
                        InjuryModel.WeeklyCheck(p, injuryRng, ctx.Injury, ctx.Skills, ctx.InjuryCatalog);
                    }
                    else
                    {
                        InjuryModel.WeeklyRecover(p, ctx.Injury, ctx.InjuryCatalog);
                    }
                }

                // 選手成長イベント（設計書03 §5.5: 覚醒/開眼/スランプ/伸び悩み/イップス）。独立ストリーム。
                var growthRng = rng.Fork(0x6707_0000UL ^ (ulong)(year * 100 + week));
                GrowthEventModel.Week(roster, growthRng, ctx.Growth);

                eventsFired += scheduler.Week(week, rng).Count;
            }

            var regulars = roster.Where(p => p.Grade >= 2).ToList();
            var regAvg = regulars.Count > 0 ? regulars.Average(p => p.AverageLevel()) : 0;
            snapshots.Add(new YearSnapshot(year, roster.Count, regAvg, gradAvg, eventsFired));
        }

        return new CareerSummary(snapshots);
    }
}
