using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Engine.Tests.Balance;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 監督の分野別指導力→育成係数の写像（Issue #115・OPEN-QUESTIONS Q7(b)）と練習効率／施設システムの検証。
/// ・分野別指導力が能力分野ごとに exp へ効く（投手系←Pitching / 守備・走塁系←Defense / 打撃系←Batting）。
/// ・監督null注入＝基準指導力（20）を全分野に適用＝従来のシムと1ビット一致（不変条件#2）。
/// ・施設レベル0で従来一致、レベルを上げると施設係数・週練習時間が増えて全能力が速く伸びる。
/// ・実測レポート: 豪腕素質×名将＋フル施設で3年 +15〜20km/h が成立すること。
/// </summary>
public sealed class CoachingAndFacilityTests
{
    private static DevelopingPlayer MakePitcher(int velocityLevel = 46, int cap = 99)
    {
        var p = new DevelopingPlayer { IsPitcher = true };
        p.SetLevel(AbilityKind.Velocity, velocityLevel);
        p.SetCap(AbilityKind.Velocity, cap);
        return p;
    }

    private static int GainByTrainWeek(AbilityKind ability, TrainingMenu menu, CoachingProfile? coaching,
        int weeks = 200, int start = 32)
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer { IsPitcher = true };
        p.SetLevel(ability, start);
        p.SetCap(ability, 99);
        for (var i = 0; i < weeks; i++)
            DevelopmentModel.TrainWeek(p, menu, 1, 1.0, stages, c, coaching: coaching);
        return p.Level(ability) - start;
    }

    // --- A. 分野別指導力が育成expに効く（完了条件: 監督指導力が効くこと） ---

    [Fact]
    public void HigherPitchingCoaching_GrowsVelocityFaster()
    {
        var weak = CoachingProfile.Uniform(20);       // 基準（中立点）
        var ace = new CoachingProfile { Pitching = 90 }; // 名将（投手）

        var weakGain = GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, weak);
        var aceGain = GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, ace);
        Assert.True(aceGain > weakGain, $"投手指導力で球速が速く伸びていない (名将{aceGain} vs 基準{weakGain})");
    }

    [Fact]
    public void CoachingIsRoutedByAbilityCategory()
    {
        // 投手指導力だけ高い監督は球速を伸ばすが打撃（ミート）は基準どおり。逆も然り。
        var pitchingOnly = new CoachingProfile { Pitching = 90, Batting = 20, Defense = 20 };
        var battingOnly = new CoachingProfile { Pitching = 20, Batting = 90, Defense = 20 };
        var neutral = CoachingProfile.Uniform(20);

        Assert.True(GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, pitchingOnly)
                    > GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, neutral));
        Assert.Equal(GainByTrainWeek(AbilityKind.Contact, TrainingMenu.Batting, neutral),
                     GainByTrainWeek(AbilityKind.Contact, TrainingMenu.Batting, pitchingOnly));

        Assert.True(GainByTrainWeek(AbilityKind.Contact, TrainingMenu.Batting, battingOnly)
                    > GainByTrainWeek(AbilityKind.Contact, TrainingMenu.Batting, neutral));
        Assert.Equal(GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, neutral),
                     GainByTrainWeek(AbilityKind.Velocity, TrainingMenu.VelocityTraining, battingOnly));
    }

    // --- 監督null注入・基準指導力(20)均一は従来と1ビット一致（不変条件#2） ---

    [Fact]
    public void NullCoaching_BitIdenticalToLegacy()
    {
        // coaching=null は「分野別なし」＝従来の TrainWeek（coaching引数なし）と1ビットも変わらない。
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        foreach (var (ability, menu) in new[]
                 {
                     (AbilityKind.Velocity, TrainingMenu.VelocityTraining),
                     (AbilityKind.Contact, TrainingMenu.Batting),
                     (AbilityKind.Fielding, TrainingMenu.Defense),
                 })
        {
            var legacy = new DevelopingPlayer { IsPitcher = true };
            legacy.SetLevel(ability, 30); legacy.SetCap(ability, 99);
            var withNull = new DevelopingPlayer { IsPitcher = true };
            withNull.SetLevel(ability, 30); withNull.SetCap(ability, 99);
            for (var i = 0; i < 150; i++)
            {
                DevelopmentModel.TrainWeek(legacy, menu, 1, 1.0, stages, c);
                DevelopmentModel.TrainWeek(withNull, menu, 1, 1.0, stages, c, coaching: null);
            }
            Assert.Equal(legacy.Level(ability), withNull.Level(ability));
            Assert.Equal(legacy.Exp(ability), withNull.Exp(ability));
        }
    }

    [Fact]
    public void UniformBaselineCoaching_BitIdenticalToNull()
    {
        // 基準指導力（CoachingLevel=20）を全分野に均一注入すると、中立点との比が厳密に1.0となり
        // coaching=null（従来）と1ビットも変わらない。
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var uniform = CoachingProfile.Uniform(c.CoachingLevel);
        foreach (var (ability, menu) in new[]
                 {
                     (AbilityKind.Velocity, TrainingMenu.VelocityTraining),
                     (AbilityKind.Contact, TrainingMenu.Batting),
                     (AbilityKind.Fielding, TrainingMenu.Defense),
                     (AbilityKind.Speed, TrainingMenu.BaseRunning),
                 })
        {
            var a = new DevelopingPlayer { IsPitcher = true };
            a.SetLevel(ability, 30); a.SetCap(ability, 99);
            var b = new DevelopingPlayer { IsPitcher = true };
            b.SetLevel(ability, 30); b.SetCap(ability, 99);
            for (var i = 0; i < 150; i++)
            {
                DevelopmentModel.TrainWeek(a, menu, 1, 1.0, stages, c, coaching: null);
                DevelopmentModel.TrainWeek(b, menu, 1, 1.0, stages, c, coaching: uniform);
            }
            Assert.Equal(a.Level(ability), b.Level(ability));
            Assert.Equal(a.Exp(ability), b.Exp(ability));
        }
    }

    // --- B. 施設システム ---

    [Fact]
    public void FacilityLevelZero_ResolvesToLegacyValues()
    {
        var ctx = new SeasonContext();
        var (coef, budget) = ctx.ResolveFacility();
        Assert.Equal(ctx.Training.FacilityCoef, coef);       // 1.0
        Assert.Equal(ctx.Training.DefaultBudgetMinutes, budget); // 300
    }

    [Fact]
    public void FacilityLevelUp_RaisesCoefAndBudget()
    {
        var tiers = new List<FacilityTier>
        {
            new() { Coef = 1.0, BudgetMinutes = 300 },
            new() { Coef = 1.5, BudgetMinutes = 480 },
        };
        var training = new TrainingCoefficients { FacilityTiers = tiers };
        var lv0 = new SeasonContext { Training = training, FacilityLevel = 0 };
        var lv1 = new SeasonContext { Training = training, FacilityLevel = 1 };

        Assert.Equal((1.0, 300), lv0.ResolveFacility());
        Assert.Equal((1.5, 480), lv1.ResolveFacility());
    }

    [Fact]
    public void FacilityLevel_ClampsToLastTier()
    {
        var tiers = new List<FacilityTier> { new() { Coef = 1.0 }, new() { Coef = 1.5, BudgetMinutes = 480 } };
        var ctx = new SeasonContext { Training = new TrainingCoefficients { FacilityTiers = tiers }, FacilityLevel = 99 };
        Assert.Equal((1.5, 480), ctx.ResolveFacility());
    }

    // --- SeasonEngine 全体で従来一致（監督null・施設0） ---

    [Fact]
    public void SeasonEngine_DefaultInjection_BitIdenticalToLegacy()
    {
        // 監督null・施設レベル0（既定）は、Coaching/FacilityLevel を触らないシムと年度記録が完全一致。
        var legacy = new SeasonContext();
        var injected = new SeasonContext { Coaching = null, FacilityLevel = 0 };
        var a = SeasonEngine.Run(3, legacy, new Xoshiro256Random(2025));
        var b = SeasonEngine.Run(3, injected, new Xoshiro256Random(2025));
        Assert.Equal(a.Years.Count, b.Years.Count);
        for (var i = 0; i < a.Years.Count; i++)
        {
            Assert.Equal(a.Years[i].RosterCount, b.Years[i].RosterCount);
            Assert.Equal(a.Years[i].AvgLevelRegulars, b.Years[i].AvgLevelRegulars);
            Assert.Equal(a.Years[i].GraduatingAvgLevel, b.Years[i].GraduatingAvgLevel);
        }
    }

    [Fact]
    public void SeasonEngine_UniformBaselineCoaching_BitIdenticalToLegacy()
    {
        var legacy = new SeasonContext();
        var injected = new SeasonContext { Coaching = CoachingProfile.Uniform(20) };
        var a = SeasonEngine.Run(3, legacy, new Xoshiro256Random(77));
        var b = SeasonEngine.Run(3, injected, new Xoshiro256Random(77));
        for (var i = 0; i < a.Years.Count; i++)
            Assert.Equal(a.Years[i].AvgLevelRegulars, b.Years[i].AvgLevelRegulars);
    }

    [Fact]
    public void CoachingProfile_FromManager_MapsFields()
    {
        var m = new KokoSim.Engine.Career.Manager { CoachingBatting = 55, CoachingPitching = 80, CoachingDefense = 30 };
        var cp = CoachingProfile.FromManager(m);
        Assert.Equal(80, cp.LevelFor(AbilityKind.Velocity));   // 投手系
        Assert.Equal(55, cp.LevelFor(AbilityKind.Contact));    // 打撃系
        Assert.Equal(30, cp.LevelFor(AbilityKind.Fielding));   // 守備・走塁系
        Assert.Equal(30, cp.LevelFor(AbilityKind.Speed));      // 走力＝走塁系
    }

    // --- C. 実測レポート（完了条件）: 豪腕素質×名将＋フル施設で3年 +15〜20km/h ---

    [Trait("Category", "Heavy")]
    [Fact]
    public void Report_PowerArmProspect_WithAceAndFullFacility_Gains15To20Kmh()
    {
        var bundle = CoefficientsLoader.LoadFromFile(BalanceRegressionTests.FindDataFile("coefficients.yaml"));
        var c = bundle.Training;
        var stages = new GrowthStageTable();

        // 成長段階（6,6,6,6,4ヶ月）を月数×4週で近似した「球速全振り3年」。
        int[] weeksPerStage = { 24, 24, 24, 24, 16 };

        // 環境（監督指導力・施設レベル）を与えて球速全振り3年を回し、到達 km/h を返す。
        double FinalVelocityKmh(CoachingProfile coaching, int facilityLevel, int startLevel = 46, int cap = 99)
        {
            var facilityCoef = facilityLevel <= 0 || c.FacilityTiers.Count == 0
                ? c.FacilityCoef
                : c.FacilityTiers[Math.Min(facilityLevel, c.FacilityTiers.Count - 1)].Coef;
            var budget = facilityLevel <= 0 || c.FacilityTiers.Count == 0
                ? c.DefaultBudgetMinutes
                : c.FacilityTiers[Math.Min(facilityLevel, c.FacilityTiers.Count - 1)].BudgetMinutes;
            var training = c with { FacilityCoef = facilityCoef };

            var p = MakePitcher(startLevel, cap);
            var alloc = new[] { new MenuAllocation(TrainingMenu.VelocityTraining, budget) };
            for (var stage = 0; stage < weeksPerStage.Length; stage++)
                for (var w = 0; w < weeksPerStage[stage]; w++)
                    DevelopmentModel.TrainWeekPlan(p, alloc, training.ReferenceWeekMinutes, stage, 1.0,
                        stages, training, coaching: coaching);
            return PitcherAttributes.VelocityKmhFromLevel(p.Level(AbilityKind.Velocity));
        }

        var startKmh = PitcherAttributes.VelocityKmhFromLevel(46); // ≈130km/h
        var ace = new CoachingProfile { Pitching = 90, Batting = 90, Defense = 90 };
        var weak = CoachingProfile.Uniform(20);

        var aceFullKmh = FinalVelocityKmh(ace, facilityLevel: 4);          // 名将＋フル施設（強豪）
        var weakNoneKmh = FinalVelocityKmh(weak, facilityLevel: 0);        // 無名監督＋施設なし（弱小）
        var aceFullGain = aceFullKmh - startKmh;
        var weakNoneGain = weakNoneKmh - startKmh;

        var sb = new StringBuilder();
        sb.AppendLine("# Issue #115 — 監督指導力＋施設の環境倍率 実測レポート");
        sb.AppendLine();
        sb.AppendLine($"入学時球速: {startKmh:F1} km/h（球速Lv46・豪腕素質）");
        sb.AppendLine($"基準指導力(中立点) coaching_level={c.CoachingLevel}, slope={c.CoachingSlope}");
        sb.AppendLine($"施設Lv4 係数/時間: coef={c.FacilityTiers[4].Coef}, budget={c.FacilityTiers[4].BudgetMinutes}分");
        sb.AppendLine();
        sb.AppendLine("## 球速全振り3年（成長段階24/24/24/24/16週, standard, cap99）");
        sb.AppendLine();
        sb.AppendLine("| 環境 | 監督(投手指導) | 施設Lv | 到達球速 | 3年の伸び |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        sb.AppendLine($"| 強豪（名将＋フル施設） | 90 | 4 | {aceFullKmh:F1} km/h | +{aceFullGain:F1} km/h |");
        sb.AppendLine($"| 弱小（無名＋施設なし） | 20 | 0 | {weakNoneKmh:F1} km/h | +{weakNoneGain:F1} km/h |");
        sb.AppendLine();
        sb.AppendLine($"ターゲット「豪腕素質×名将＋フル施設で3年 +15〜20km/h」: {(aceFullGain >= 15.0 ? "成立" : "未達")}");

        var outDir = Path.Combine(BalanceRegressionTests.FindDataFile("coefficients.yaml"),
            "..", "..", "out", "overnight", "issue-115");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "environment-growth-report.md"), sb.ToString());

        // 完了条件: 強豪環境で +15〜20km/h（B〜A級球速）が成立する。弱小との差が環境倍率の効き。
        Assert.True(aceFullGain >= 15.0, $"強豪環境で +15km/h に届かない（実測 +{aceFullGain:F1}km/h）");
        Assert.True(aceFullGain <= 22.0, $"強豪環境の伸びが過大（実測 +{aceFullGain:F1}km/h）");
        Assert.True(aceFullGain > weakNoneGain + 8.0,
            $"環境倍率の効きが弱い（強豪+{aceFullGain:F1} vs 弱小+{weakNoneGain:F1}km/h）");
    }
}
