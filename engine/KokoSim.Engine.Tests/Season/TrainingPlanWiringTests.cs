using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 練習計画→週次成長の配線が依存する不変条件（issue #484）。
///
/// Unity Shell は、ホームの週送り（実適用）も練習計画画面のプレビュー（クローンへのドライラン）も
/// 「選手ごとの <see cref="DevelopingPlayer.Plan"/>（null=お任せ）を <see cref="TrainingPresets.Resolve"/> で
/// 実効配分へ解決し <see cref="DevelopmentModel.TrainWeekPlan"/> を1回呼ぶ」という同一経路
/// （<c>KokoSim.Unity.Shell.TrainingWeek.Apply</c>）へ束ねた。以前は実適用が固定7周期メニューを全部員へ
/// 一律適用しており、配分・監督指導力・性格・体質スキルが実ゲームで無効だった。
///
/// この配線を守る自動ゲートが Unity 側には無い（CI は Assets を通らない）ため、経路が依存する
/// エンジン側の不変条件をここで固定する:
///   ① 配分どおりに伸びる（打撃全振りは打撃系だけ伸び、投手系は伸びない）
///   ② プレビュー（クローン）と実適用（本体）が同じ入力・同じAPIで完全一致する
///   ③ 施設を強化すると週練習 budget が増える（#128 の集約が効く）
/// </summary>
public sealed class TrainingPlanWiringTests
{
    private static readonly TrainingCoefficients C = new();
    private static readonly GrowthStageTable Stages = new();

    private static DevelopingPlayer MakeFielder()
    {
        var p = new DevelopingPlayer { Grade = 2 };
        PitcherRoleResolver.SeedAptitudeForRole(p, false);
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, 40); p.SetCap(k, 99); }
        return p;
    }

    /// <summary>Unity の TrainingWeek.Apply と同じ順序の純エンジン適用（Plan→Resolve→TrainWeekPlan）。
    /// 監督は中立点（基準指導力20）＝従来と1ビット一致する条件で回す。</summary>
    private static void ApplyWeek(DevelopingPlayer p, int budget)
    {
        var plan = p.Plan ?? TrainingPlan.Default;
        var alloc = TrainingPresets.Resolve(plan, budget);
        DevelopmentModel.TrainWeekPlan(p, alloc, C.ReferenceWeekMinutes, 1, 1.0, Stages, C,
            new SkillCoefficients(), new PersonalityCoefficients(), CoachingProfile.Uniform(20));
    }

    // ① 配分どおりに伸びる: 打撃全振りは打撃系だけ伸び、投手系（球速・制球）は伸びない。
    [Fact]
    public void BattingFocusedPlan_GrowsBatting_NotPitching()
    {
        var p = MakeFielder();
        p.Plan = new TrainingPlan
        {
            Preset = TrainingPreset.Custom,
            Allocations = new[] { new MenuAllocation(TrainingMenu.Batting, 300) },
        };
        for (var i = 0; i < 50; i++) ApplyWeek(p, 300);

        Assert.True(p.Level(AbilityKind.Contact) > 40, "打撃全振りでミートが伸びていない");
        Assert.Equal(40, p.Level(AbilityKind.Control));    // 投手系は伸びない
        Assert.Equal(40, p.Level(AbilityKind.Velocity));
    }

    // ② プレビュー（クローン）と実適用（本体）が完全一致する: 同じ Plan・同じ budget・同じAPIから出るので
    //    全能力のレベル・経験値がビット単位で一致する（＝プレビューバーが嘘をつかない）。
    [Fact]
    public void PreviewClone_EqualsActual()
    {
        var actual = MakeFielder();
        actual.Plan = new TrainingPlan
        {
            Preset = TrainingPreset.Custom,
            Allocations = new[]
            {
                new MenuAllocation(TrainingMenu.Batting, 200),
                new MenuAllocation(TrainingMenu.Strength, 100),
            },
        };
        var preview = actual.Clone();   // プレビューは実選手のクローンにドライラン（Clone は Plan を引き継ぐ）

        ApplyWeek(preview, 300);
        ApplyWeek(actual, 300);

        foreach (var k in AbilityKinds.All)
        {
            Assert.Equal(actual.Level(k), preview.Level(k));
            Assert.Equal(actual.Exp(k), preview.Exp(k), 6);
        }
    }

    // ③ 施設を強化すると週練習 budget が増える（#128 の集約を Unity 側でも通す配線が依存する不変条件）。
    [Fact]
    public void Facilities_IncreaseWeeklyBudget()
    {
        var set = new FacilitySet();
        var baseBudget = C.DefaultBudgetMinutes + FacilityCatalog.Default.Aggregate(set).BudgetAdd;
        Assert.Equal(C.DefaultBudgetMinutes, baseBudget);   // 施設Lv0で従来一致（不変条件#2）

        set.SetLevel("lighting", 1);   // 照明Lv1（週練習時間 +80分）
        var withLighting = C.DefaultBudgetMinutes + FacilityCatalog.Default.Aggregate(set).BudgetAdd;
        Assert.True(withLighting > baseBudget, "施設強化で週練習 budget が増えていない");
    }

    // お任せ（Plan=null）はフォールバックで自動プリセットへ解決され、ちゃんと伸びる（配線の既定経路）。
    [Fact]
    public void NullPlan_FallsBackToAuto_AndGrows()
    {
        var p = MakeFielder();
        Assert.Null(p.Plan);
        for (var i = 0; i < 30; i++) ApplyWeek(p, 300);
        Assert.True(p.Level(AbilityKind.Contact) > 40, "お任せで打撃系が伸びていない");
    }
}
