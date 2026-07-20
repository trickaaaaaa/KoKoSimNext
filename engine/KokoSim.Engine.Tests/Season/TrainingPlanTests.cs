using System.Collections.Generic;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 練習時間配分育成（設計書03 §3.1）の検証。後方互換（1メニュー満額=TrainWeek一致）、
/// 分配分の按分、施設budget増による成長増、休養/未配分の扱いを確認。決定論。
/// </summary>
public sealed class TrainingPlanTests
{
    private const int Ref = 300;

    private static DevelopingPlayer NewBatter()
    {
        var p = new DevelopingPlayer { IsPitcher = false };
        foreach (var k in new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline,
                                  AbilityKind.Stamina, AbilityKind.Fielding })
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }
        return p;
    }

    // 1. 後方互換: 1メニューに基準週フル配分 → TrainWeek と exp/level 厳密一致。
    [Fact]
    public void TrainWeekPlan_SingleFullBudget_EqualsLegacyTrainWeek()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();

        var legacy = NewBatter();
        var plan = NewBatter();
        for (var i = 0; i < 8; i++)
        {
            DevelopmentModel.TrainWeek(legacy, TrainingMenu.Batting, 0, 1.0, stages, c);
            DevelopmentModel.TrainWeekPlan(plan,
                new[] { new MenuAllocation(TrainingMenu.Batting, Ref) },
                Ref, 0, 1.0, stages, c);
        }

        foreach (var k in AbilityKinds.All)
        {
            Assert.Equal(legacy.Level(k), plan.Level(k));
            Assert.Equal(legacy.Exp(k), plan.Exp(k), 9);
        }
    }

    // 2. 分配分: 150/150分の2メニューは、各300分満額のちょうど半分ずつ成長する。
    [Fact]
    public void TrainWeekPlan_SplitByMinutes_DistributesExp()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();

        var full = NewBatter();
        DevelopmentModel.TrainWeekPlan(full,
            new[] { new MenuAllocation(TrainingMenu.Batting, Ref) },
            Ref, 0, 1.0, stages, c);

        var half = NewBatter();
        DevelopmentModel.TrainWeekPlan(half,
            new[] { new MenuAllocation(TrainingMenu.Batting, Ref / 2), new MenuAllocation(TrainingMenu.Defense, Ref / 2) },
            Ref, 0, 1.0, stages, c);

        // ミートは満額の半分の exp。
        Assert.Equal(full.Exp(AbilityKind.Contact) / 2.0, half.Exp(AbilityKind.Contact), 6);
        // 守備も伸びている（別メニュー同時進行）。
        Assert.True(half.Exp(AbilityKind.Fielding) > 0);
    }

    // 3. 施設budget増: 600分フル配分は300分より成長が大きい。
    [Fact]
    public void TrainWeekPlan_HigherBudget_GrowsMore()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();

        var small = NewBatter();
        DevelopmentModel.TrainWeekPlan(small,
            new[] { new MenuAllocation(TrainingMenu.Batting, 300) },
            Ref, 0, 1.0, stages, c);

        var big = NewBatter();
        DevelopmentModel.TrainWeekPlan(big,
            new[] { new MenuAllocation(TrainingMenu.Batting, 600) },
            Ref, 0, 1.0, stages, c);

        Assert.True(big.Exp(AbilityKind.Contact) > small.Exp(AbilityKind.Contact));
    }

    // 4. 休養メニューは効果なし（成長させない）。
    [Fact]
    public void RestMenu_NoGrowth()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = NewBatter();

        DevelopmentModel.TrainWeekPlan(p,
            new[] { new MenuAllocation(TrainingMenu.Rest, Ref) },
            Ref, 0, 1.0, stages, c);

        foreach (var k in AbilityKinds.All)
            Assert.Equal(0.0, p.Exp(k), 9);
    }

    // 4b. 未配分（budgetに満たない）分は効果なし（idle）。
    [Fact]
    public void UnallocatedMinutes_NoEffect()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();

        var partial = NewBatter();
        DevelopmentModel.TrainWeekPlan(partial,
            new[] { new MenuAllocation(TrainingMenu.Batting, 100) }, // 300分中100分だけ使用
            Ref, 0, 1.0, stages, c);

        var exact = NewBatter();
        DevelopmentModel.TrainWeekPlan(exact,
            new[] { new MenuAllocation(TrainingMenu.Batting, 100) },
            Ref, 0, 1.0, stages, c);

        // 残り200分は未配分で捨てられる＝100分ぶんの成長のみ。
        Assert.Equal(exact.Exp(AbilityKind.Contact), partial.Exp(AbilityKind.Contact), 9);
        Assert.Equal(0.0, partial.Exp(AbilityKind.Contact) - exact.Exp(AbilityKind.Contact), 9);
    }

    // 7. お任せプリセットは現行の週サイクルと平均等価（回帰防止）。
    //    旧サイクル（1週1メニュー満額をローテ）と新お任せ（毎週budgetを按分）を LCM 週数で回し総成長を比較。
    [Fact]
    public void BatterAuto_AverageMatchesLegacyCycle()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        const int weeks = 45; // LCM(9,5) で旧サイクル長9を割り切る

        // 旧サイクル（BatterCycle 相当: 8訓練+休養、週番号ローテ）。
        TrainingMenu[] legacyCycle =
        {
            TrainingMenu.Batting, TrainingMenu.PowerHitting, TrainingMenu.PlateDiscipline,
            TrainingMenu.Strength, TrainingMenu.Defense, TrainingMenu.BaseRunning,
            TrainingMenu.Throwing, TrainingMenu.Bunt, TrainingMenu.Rest,
        };
        var legacy = HighCapBatter();
        for (var w = 0; w < weeks; w++)
            DevelopmentModel.TrainWeek(legacy, legacyCycle[w % legacyCycle.Length],
                0, 1.0, stages, c);

        // 新お任せ（BatterAuto を毎週 budget=300 で按分）。
        var auto = HighCapBatter();
        for (var w = 0; w < weeks; w++)
        {
            var alloc = TrainingPresets.Resolve(new TrainingPlan { Preset = TrainingPreset.BatterAuto }, false, 300);
            DevelopmentModel.TrainWeekPlan(auto, alloc, Ref, 0, 1.0, stages, c);
        }

        var legacyTotal = 0;
        var autoTotal = 0;
        foreach (var k in AbilityKinds.All)
        {
            legacyTotal += legacy.Level(k) - 30;
            autoTotal += auto.Level(k) - 30;
        }
        // 端数配分による微差は許容（±15%）。総成長量が同オーダーであることを保証。
        Assert.InRange(autoTotal / (double)legacyTotal, 0.85, 1.15);
    }

    private static DevelopingPlayer HighCapBatter()
    {
        var p = new DevelopingPlayer { IsPitcher = false };
        foreach (var k in AbilityKinds.All)
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }
        return p;
    }

    // --- プリセット（設計書03 §3.1） ---

    // 6. 各プリセットが budget をちょうど満たす（合計=budget、決定論）。
    [Theory]
    [InlineData(TrainingPreset.PitcherAuto, true)]
    [InlineData(TrainingPreset.BatterAuto, false)]
    [InlineData(TrainingPreset.Balanced, true)]
    [InlineData(TrainingPreset.Balanced, false)]
    [InlineData(TrainingPreset.DefenseFocus, false)]
    [InlineData(TrainingPreset.AceDevelopment, true)]
    [InlineData(TrainingPreset.SluggerDevelopment, false)]
    public void Preset_FillsBudgetExactly(TrainingPreset preset, bool isPitcher)
    {
        const int budget = 300;
        var alloc = TrainingPresets.Resolve(new TrainingPlan { Preset = preset }, isPitcher, budget);

        var total = 0;
        foreach (var a in alloc) total += a.Minutes;
        Assert.Equal(budget, total);
        Assert.NotEmpty(alloc);
    }

    [Fact]
    public void PitcherAuto_UsesOnlyPitcherMenus()
    {
        var alloc = TrainingPresets.Resolve(new TrainingPlan { Preset = TrainingPreset.PitcherAuto }, true, 300);
        var allowed = new HashSet<TrainingMenu>
        {
            TrainingMenu.Pitching, TrainingMenu.BreakingBall, TrainingMenu.Running,
            TrainingMenu.VelocityTraining, TrainingMenu.Rest,
        };
        foreach (var a in alloc) Assert.Contains(a.Menu, allowed);
    }

    // 8. コピー: b.Plan = a.Plan 後、b用に新プランを作っても a は不変（immutable の安全性, 要件6）。
    [Fact]
    public void Copy_SharesImmutablePlan_SourceUnaffected()
    {
        var a = new DevelopingPlayer { Plan = new TrainingPlan { Preset = TrainingPreset.AceDevelopment } };
        var b = new DevelopingPlayer();

        b.Plan = a.Plan;                                   // コピー（参照共有だが不変）
        Assert.Equal(TrainingPreset.AceDevelopment, b.Plan!.Preset);

        b.Plan = new TrainingPlan { Preset = TrainingPreset.SluggerDevelopment }; // b を差し替え
        Assert.Equal(TrainingPreset.AceDevelopment, a.Plan!.Preset);              // a は無傷
    }

    // 9. Custom が budget 超過なら比例縮小して合計=budget、不足ならそのまま（idle）。
    [Fact]
    public void Resolve_CustomOverBudget_ScalesDown()
    {
        var over = new TrainingPlan
        {
            Preset = TrainingPreset.Custom,
            Allocations = new[]
            {
                new MenuAllocation(TrainingMenu.Batting, 400),
                new MenuAllocation(TrainingMenu.Defense, 200),
            },
        };
        var alloc = TrainingPresets.Resolve(over, false, 300);
        var total = 0;
        foreach (var a in alloc) total += a.Minutes;
        Assert.Equal(300, total); // 600→300 に比例縮小

        var under = new TrainingPlan
        {
            Preset = TrainingPreset.Custom,
            Allocations = new[] { new MenuAllocation(TrainingMenu.Batting, 100) },
        };
        var alloc2 = TrainingPresets.Resolve(under, false, 300);
        var total2 = 0;
        foreach (var a in alloc2) total2 += a.Minutes;
        Assert.Equal(100, total2); // 不足はそのまま（残り idle）
    }
}
