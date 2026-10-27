using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 個別指導3枠: 指名選手の主効果exp×係数（Issue #126・OPEN-QUESTIONS Q7(a)）。
/// ・指名（DevelopingPlayer.IndividualCoaching=true）かつ分野別指導力が中立点より高いと主効果expだけ速く伸びる。
/// ・新任監督（指導力20＝中立点）や coaching=null でも基礎倍率（既定×1.5）が掛かる（#693）。
/// ・非指名は追加倍率1.0＝従来一致（不変条件#2）。
/// ・副効果・守備適性は個別指導の対象外（主効果expのみ）。
/// ・基礎倍率1.0＋スケール0で個別指導の効果を無効化できる（無効化用の逃げ道）。
/// </summary>
public sealed class IndividualCoachingTests
{
    private static int GainByTrainWeek(AbilityKind ability, TrainingMenu menu, bool nominated,
        CoachingProfile? coaching, TrainingCoefficients? coefficients = null, int weeks = 150, int start = 30)
    {
        var c = coefficients ?? new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer { IndividualCoaching = nominated };
        PitcherRoleResolver.SeedAptitudeForRole(p, true);
        p.SetLevel(ability, start);
        p.SetCap(ability, 99);
        for (var i = 0; i < weeks; i++)
            DevelopmentModel.TrainWeek(p, menu, 1, 1.0, stages, c, coaching: coaching);
        return p.Level(ability) - start;
    }

    [Fact]
    public void Nominated_GrowsMainAbilityFaster_WhenCoachingAboveBaseline()
    {
        var ace = new CoachingProfile { Pitching = 90 }; // 名将（投手系, 中立点20より高い）

        var withoutSlot = GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, nominated: false, coaching: ace);
        var withSlot = GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, nominated: true, coaching: ace);

        Assert.True(withSlot > withoutSlot,
            $"個別指導3枠で主効果expが速く伸びていない（指名{withSlot} vs 非指名{withoutSlot}）");
    }

    [Fact]
    public void NotNominated_BitIdenticalToLegacy()
    {
        // IndividualCoaching=false（既定）は、フラグ自体を持たない従来のTrainWeekと1ビットも変わらない。
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var coaching = new CoachingProfile { Pitching = 90 };

        var legacy = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(legacy, true);
        legacy.SetLevel(AbilityKind.Velocity, 30); legacy.SetCap(AbilityKind.Velocity, 99);
        var notNominated = new DevelopingPlayer { IndividualCoaching = false };
        PitcherRoleResolver.SeedAptitudeForRole(notNominated, true);
        notNominated.SetLevel(AbilityKind.Velocity, 30); notNominated.SetCap(AbilityKind.Velocity, 99);

        for (var i = 0; i < 150; i++)
        {
            DevelopmentModel.TrainWeek(legacy, TrainingMenu.VelocityTraining, 1, 1.0, stages, c, coaching: coaching);
            DevelopmentModel.TrainWeek(notNominated, TrainingMenu.VelocityTraining, 1, 1.0, stages, c, coaching: coaching);
        }

        Assert.Equal(legacy.Level(AbilityKind.Velocity), notNominated.Level(AbilityKind.Velocity));
        Assert.Equal(legacy.Exp(AbilityKind.Velocity), notNominated.Exp(AbilityKind.Velocity));
    }

    /// <summary>
    /// 新任監督（指導力は全分野20＝中立点）でも、指名すれば主効果がはっきり速く伸びる（issue #693）。
    /// 旧式は中立点で倍率ちょうど1.0＝指名しても何も起きなかった。
    /// </summary>
    [Fact]
    public void Nominated_AtNewManagerCoaching_StillGrowsClearlyFaster()
    {
        var rookie = CoachingProfile.FromManager(new KokoSim.Engine.Career.Manager());
        Assert.Equal(20, rookie.Pitching);

        var withSlot = GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, nominated: true, coaching: rookie, weeks: 40);
        var withoutSlot = GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, nominated: false, coaching: rookie, weeks: 40);

        Assert.True(withSlot >= withoutSlot * 1.3,
            $"新任監督の個別指導で伸びが変わらない（指名{withSlot} vs 非指名{withoutSlot}）");
    }

    /// <summary>倍率そのもの: 新任×1.5、指導力が上がるほど伸び、名将（90）で×2.5前後（2026-10-28 監督決定）。</summary>
    [Fact]
    public void Bonus_IsOnePointFiveForRookie_AndAboutTwoPointFiveForMaster()
    {
        var c = new TrainingCoefficients();
        var nominated = new DevelopingPlayer { IndividualCoaching = true };
        var baseline = 1.0 + c.CoachingLevel * c.CoachingSlope;
        double Bonus(double level) => DevelopmentModel.IndividualCoachingBonus(
            AbilityKind.Velocity, nominated, CoachingProfile.Uniform(level), c, 1.0, baseline);

        Assert.Equal(1.5, Bonus(20), 6);
        Assert.True(Bonus(40) > Bonus(20) && Bonus(60) > Bonus(40), "指導力が上がっても倍率が伸びない");
        Assert.InRange(Bonus(90), 2.3, 2.6);
        Assert.Equal(1.5, DevelopmentModel.IndividualCoachingBonus(AbilityKind.Velocity, nominated, null, c, 1.0, baseline), 6);
        Assert.Equal(1.0, DevelopmentModel.IndividualCoachingBonus(
            AbilityKind.Velocity, new DevelopingPlayer(), CoachingProfile.Uniform(90), c, 1.0, baseline));
    }

    [Fact]
    public void BaseOneAndScaleZero_DisablesBonus()
    {
        var c = new TrainingCoefficients { IndividualCoachingBaseMultiplier = 1.0, IndividualCoachingBonusScale = 0.0 };
        var ace = new CoachingProfile { Pitching = 90 };
        var withSlot = GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, nominated: true, coaching: ace, coefficients: c);
        var withoutSlot = GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, nominated: false, coaching: ace, coefficients: c);
        Assert.Equal(withoutSlot, withSlot);
    }

    [Fact]
    public void Nomination_DoesNotAffectSubEffectOrAptitude()
    {
        // Battingメニューの副効果(Discipline)・守備適性(なし)には個別指導の追加倍率が乗らない（主効果のみ）。
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var ace = new CoachingProfile { Batting = 90 };

        var notNominated = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(notNominated, false);
        notNominated.SetLevel(AbilityKind.Discipline, 30); notNominated.SetCap(AbilityKind.Discipline, 99);
        var nominated = new DevelopingPlayer { IndividualCoaching = true };
        PitcherRoleResolver.SeedAptitudeForRole(nominated, false);
        nominated.SetLevel(AbilityKind.Discipline, 30); nominated.SetCap(AbilityKind.Discipline, 99);

        for (var i = 0; i < 150; i++)
        {
            DevelopmentModel.TrainWeek(notNominated, TrainingMenu.Batting, 1, 1.0, stages, c, coaching: ace);
            DevelopmentModel.TrainWeek(nominated, TrainingMenu.Batting, 1, 1.0, stages, c, coaching: ace);
        }

        Assert.Equal(notNominated.Level(AbilityKind.Discipline), nominated.Level(AbilityKind.Discipline));
        Assert.Equal(notNominated.Exp(AbilityKind.Discipline), nominated.Exp(AbilityKind.Discipline));
    }

    [Fact]
    public void TrainWeekPlan_Nominated_GrowsMainAbilityFaster()
    {
        // SeasonEngine/練習計画画面の実経路（TrainWeekPlan）でも同じ効果が働く。
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var ace = new CoachingProfile { Pitching = 90 };
        var alloc = new[] { new MenuAllocation(TrainingMenu.VelocityTraining, c.ReferenceWeekMinutes) };

        var notNominated = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(notNominated, true);
        notNominated.SetLevel(AbilityKind.Velocity, 30); notNominated.SetCap(AbilityKind.Velocity, 99);
        var nominated = new DevelopingPlayer { IndividualCoaching = true };
        PitcherRoleResolver.SeedAptitudeForRole(nominated, true);
        nominated.SetLevel(AbilityKind.Velocity, 30); nominated.SetCap(AbilityKind.Velocity, 99);

        for (var i = 0; i < 150; i++)
        {
            DevelopmentModel.TrainWeekPlan(notNominated, alloc, c.ReferenceWeekMinutes, 1, 1.0, stages, c, coaching: ace);
            DevelopmentModel.TrainWeekPlan(nominated, alloc, c.ReferenceWeekMinutes, 1, 1.0, stages, c, coaching: ace);
        }

        Assert.True(nominated.Level(AbilityKind.Velocity) > notNominated.Level(AbilityKind.Velocity),
            $"TrainWeekPlan経路で個別指導3枠が効いていない（指名{nominated.Level(AbilityKind.Velocity)} vs 非指名{notNominated.Level(AbilityKind.Velocity)}）");
    }

    [Fact]
    public void DefaultDevelopingPlayer_IsNotNominated()
    {
        // 既定値false＝生成直後の選手は誰も個別指導3枠に入っていない（後方互換の起点）。
        Assert.False(new DevelopingPlayer().IndividualCoaching);
    }
}
