using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using KokoSim.Engine.Tests.Balance;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 能力別 trainability（伸ばしやすさ）係数と才能上限gap圧縮・逸材例外（Issue #114）の検証。
/// ・trainability が exp に乗る（&gt;1で速く／&lt;1で遅く）。既定1.0で従来と完全一致（不変条件#2）。
/// ・足(Speed) の cap gap 圧縮＝天井も近い。逸材（IsProdigy）は cap 圧縮・trainability 減衰を免除。
/// </summary>
public sealed class TrainabilityTests
{
    private static DevelopingPlayer MakePlayer(AbilityKind k, int level, int cap, bool prodigy = false)
    {
        var p = new DevelopingPlayer { IsProdigy = prodigy };
        p.SetLevel(k, level);
        p.SetCap(k, cap);
        return p;
    }

    /// <summary>同じ delta を weeks 回 ApplyExp して得たレベル上昇量。</summary>
    private static int GainByApplyExp(AbilityKind k, TrainingCoefficients c, bool prodigy, int weeks = 300, double delta = 85.0)
    {
        var p = MakePlayer(k, 32, 99, prodigy);
        for (var i = 0; i < weeks; i++) DevelopmentModel.ApplyExp(p, k, delta, c);
        return p.Level(k) - 32;
    }

    // --- trainability が exp に効く ---

    [Fact]
    public void Trainability_Above1_GrowsFaster_Below1_GrowsSlower()
    {
        var baseline = new TrainingCoefficients();
        var fast = new TrainingCoefficients { Trainability = new TrainabilityCoefficients { Fielding = 1.5 } };
        var slow = new TrainingCoefficients { Trainability = new TrainabilityCoefficients { Speed = 0.4 } };

        Assert.True(GainByApplyExp(AbilityKind.Fielding, fast, prodigy: false)
                    > GainByApplyExp(AbilityKind.Fielding, baseline, prodigy: false),
            "trainability>1 で速く伸びていない");
        Assert.True(GainByApplyExp(AbilityKind.Speed, slow, prodigy: false)
                    < GainByApplyExp(AbilityKind.Speed, baseline, prodigy: false),
            "trainability<1 で遅く伸びていない");
    }

    // --- 既定1.0は従来と完全一致（不変条件#2） ---

    [Fact]
    public void Trainability_DefaultIsAllOne()
    {
        var t = new TrainabilityCoefficients();
        foreach (var k in AbilityKinds.All) Assert.Equal(1.0, t.For(k));
    }

    [Fact]
    public void Trainability_ExplicitAllOne_BitIdenticalToDefault()
    {
        // 既定 Trainability と「全能力を明示的に1.0」は、同じ練習列で1ビットも変わらない。
        var def = new TrainingCoefficients();
        var explicitOne = new TrainingCoefficients { Trainability = new TrainabilityCoefficients() };
        foreach (var k in AbilityKinds.All)
        {
            var a = MakePlayer(k, 30, 99);
            var b = MakePlayer(k, 30, 99);
            for (var i = 0; i < 120; i++)
            {
                DevelopmentModel.ApplyExp(a, k, 85.0, def);
                DevelopmentModel.ApplyExp(b, k, 85.0, explicitOne);
            }
            Assert.Equal(a.Level(k), b.Level(k));
            Assert.Equal(a.Exp(k), b.Exp(k));
        }
    }

    // --- 逸材は素質固定（<1.0）の trainability 減衰を免除される ---

    [Fact]
    public void Prodigy_IsExemptFromTrainabilityDecay()
    {
        var c = new TrainingCoefficients { Trainability = new TrainabilityCoefficients { Speed = 0.4 } };

        var normalGain = GainByApplyExp(AbilityKind.Speed, c, prodigy: false);
        var prodigyGain = GainByApplyExp(AbilityKind.Speed, c, prodigy: true);
        var baselineGain = GainByApplyExp(AbilityKind.Speed, new TrainingCoefficients(), prodigy: false);

        Assert.True(prodigyGain > normalGain, "逸材が素質固定の減衰を免除されていない");
        // 逸材の足は trainability=1.0 相当（減衰なし＝規格外の俊足）。
        Assert.Equal(baselineGain, prodigyGain);
    }

    [Fact]
    public void Prodigy_DoesNotBoostAbilitiesAlreadyAboveOne()
    {
        // 逸材は「減衰の免除」であって上振れブーストではない（>1.0 の技術系は据え置き）。
        var c = new TrainingCoefficients { Trainability = new TrainabilityCoefficients { Fielding = 1.25 } };
        Assert.Equal(GainByApplyExp(AbilityKind.Fielding, c, prodigy: false),
                     GainByApplyExp(AbilityKind.Fielding, c, prodigy: true));
    }

    // --- 才能上限gap圧縮（足）: 生成コホートで検証 ---

    private static List<DevelopingPlayer> Cohort(RosterCoefficients c, int seeds = 120)
    {
        var all = new List<DevelopingPlayer>();
        for (var s = 0; s < seeds; s++)
            all.AddRange(ProspectGenerator.Intake(1, c, new Xoshiro256Random((ulong)(5000 + s))));
        return all;
    }

    [Fact]
    public void SpeedCapGapFactor_BelowOne_LowersSpeedCaps_ButLeavesOtherCapsBitIdentical()
    {
        var loose = new RosterCoefficients { SpeedCapGapFactor = 1.0 };
        var tight = new RosterCoefficients { SpeedCapGapFactor = 0.3 };

        var a = Cohort(loose);
        var b = Cohort(tight);
        Assert.Equal(a.Count, b.Count);

        double SpeedGap(IEnumerable<DevelopingPlayer> pop) =>
            pop.Average(p => p.Cap(AbilityKind.Speed) - p.Level(AbilityKind.Speed));
        Assert.True(SpeedGap(b) < SpeedGap(a), $"足のcap圧縮が効いていない (loose={SpeedGap(a):F1} tight={SpeedGap(b):F1})");

        // 足の現在値は不変、他能力の cap は1ビットも変わらない（圧縮は足だけ・不変条件#2）。
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Level(AbilityKind.Speed), b[i].Level(AbilityKind.Speed));
            Assert.Equal(a[i].Cap(AbilityKind.Contact), b[i].Cap(AbilityKind.Contact));
            Assert.Equal(a[i].Cap(AbilityKind.Fielding), b[i].Cap(AbilityKind.Fielding));
            Assert.Equal(a[i].Cap(AbilityKind.ArmStrength), b[i].Cap(AbilityKind.ArmStrength));
        }
    }

    [Fact]
    public void Prodigy_IsExemptFromSpeedCapCompression_ExactlyMatchesUncompressed()
    {
        // 全員逸材（ProdigyProb=1）＋圧縮0.3 は、非逸材＋非圧縮(1.0)と足のcapが1ビットも一致する
        // （逸材は gapFactor=max(1,0.3)=1.0 に免除され、足levelも同一のため）。
        var tightAllProdigy = new RosterCoefficients { SpeedCapGapFactor = 0.3, ProdigyProb = 1.0 };
        var looseNoProdigy = new RosterCoefficients { SpeedCapGapFactor = 1.0, ProdigyProb = 0.0 };
        var tightNoProdigy = new RosterCoefficients { SpeedCapGapFactor = 0.3, ProdigyProb = 0.0 };

        var prod = Cohort(tightAllProdigy);
        var loose = Cohort(looseNoProdigy);
        var tight = Cohort(tightNoProdigy);

        for (var i = 0; i < prod.Count; i++)
        {
            Assert.True(prod[i].IsProdigy);
            Assert.Equal(loose[i].Cap(AbilityKind.Speed), prod[i].Cap(AbilityKind.Speed)); // 免除＝非圧縮と厳密一致
        }

        double SpeedGap(IEnumerable<DevelopingPlayer> pop) =>
            pop.Average(p => p.Cap(AbilityKind.Speed) - p.Level(AbilityKind.Speed));
        Assert.True(SpeedGap(prod) > SpeedGap(tight), "逸材が圧縮を免除されていない（天井が近いまま）");
    }

    // --- 決定論: 既定係数（ProdigyProb=0・factor=1.0）で逸材は出ず従来と一致 ---

    [Fact]
    public void DefaultCoefficients_ProduceNoProdigies()
    {
        Assert.All(Cohort(new RosterCoefficients()), p => Assert.False(p.IsProdigy));
    }

    [Fact]
    public void ProdigyRoll_DoesNotPerturbAbilityRollSequence()
    {
        // 逸材ロールは独立Forkストリーム＝ProdigyProb を変えても既存の能力・cap 列は1ビットも変わらない。
        var none = new RosterCoefficients { ProdigyProb = 0.0 };
        var some = new RosterCoefficients { ProdigyProb = 0.5 };
        var a = ProspectGenerator.Intake(1, none, new Xoshiro256Random(7));
        var b = ProspectGenerator.Intake(1, some, new Xoshiro256Random(7));
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
            foreach (var k in AbilityKinds.All)
            {
                Assert.Equal(a[i].Level(k), b[i].Level(k));
                Assert.Equal(a[i].Cap(k), b[i].Cap(k)); // factor=1.0 なので逸材でも cap 不変
            }
    }

    // --- 実測レポート（完了条件）: 出荷 YAML の値で F野手の3年成長を測り out/ に保存する ---

    [Trait("Category", "Heavy")]
    [Fact]
    public void Report_ThreeYearGrowth_SpeedStaysFlat_TechnicalGrows()
    {
        var bundle = CoefficientsLoader.LoadFromFile(BalanceRegressionTests.FindDataFile("coefficients.yaml"));
        var c = bundle.Training;
        var stages = new GrowthStageTable();

        // 成長段階（6,6,6,6,4ヶ月）を月数×4週で近似した「1能力全振り3年」。start=32(F帯)。
        int[] weeksPerStage = { 24, 24, 24, 24, 16 };

        int FullTrain(AbilityKind ability, TrainingMenu menu)
        {
            var p = MakePlayer(ability, 32, 99);
            for (var stage = 0; stage < weeksPerStage.Length; stage++)
                for (var w = 0; w < weeksPerStage[stage]; w++)
                    DevelopmentModel.TrainWeek(p, menu, stage, 1.0, stages, c);
            return p.Level(ability);
        }

        // trainability を打ち消した基準（全1.0）で同条件を測り、係数の効きを対比する。
        var baseC = c with { Trainability = new TrainabilityCoefficients() };
        int FullTrainBaseline(AbilityKind ability, TrainingMenu menu)
        {
            var p = MakePlayer(ability, 32, 99);
            for (var stage = 0; stage < weeksPerStage.Length; stage++)
                for (var w = 0; w < weeksPerStage[stage]; w++)
                    DevelopmentModel.TrainWeek(p, menu, stage, 1.0, stages, baseC);
            return p.Level(ability);
        }

        var speedFinal = FullTrain(AbilityKind.Speed, TrainingMenu.BaseRunning);
        var speedBase = FullTrainBaseline(AbilityKind.Speed, TrainingMenu.BaseRunning);
        var fieldFinal = FullTrain(AbilityKind.Fielding, TrainingMenu.Defense);
        var fieldBase = FullTrainBaseline(AbilityKind.Fielding, TrainingMenu.Defense);
        var veloFinal = FullTrain(AbilityKind.Velocity, TrainingMenu.VelocityTraining);
        var veloBase = FullTrainBaseline(AbilityKind.Velocity, TrainingMenu.VelocityTraining);

        // 才能上限gap（出荷値）: 足の cap 圧縮／逸材出現率の実測。
        var cohort = Cohort(bundle.Roster, seeds: 200);
        double GapMean(AbilityKind k) => cohort.Average(p => (double)(p.Cap(k) - p.Level(k)));
        var prodigyRate = cohort.Count(p => p.IsProdigy) / (double)cohort.Count;

        var sb = new StringBuilder();
        sb.AppendLine("# Issue #114 — 能力別 trainability 実測レポート");
        sb.AppendLine();
        sb.AppendLine($"出荷係数（data/coefficients.yaml）: Speed={c.Trainability.Speed}, Fielding={c.Trainability.Fielding}, Velocity={c.Trainability.Velocity}");
        sb.AppendLine($"足のcap圧縮 speed_cap_gap_factor={bundle.Roster.SpeedCapGapFactor}, 逸材出現率 prodigy_prob={bundle.Roster.ProdigyProb}");
        sb.AppendLine();
        sb.AppendLine("## F帯(32)から1能力全振り3年（各成長段階24/24/24/24/16週, standard）");
        sb.AppendLine();
        sb.AppendLine("| 能力 | trainability | 出荷値での到達 | 係数1.0基準 | 差 |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        sb.AppendLine($"| 足(Speed) | {c.Trainability.Speed} | {speedFinal} | {speedBase} | {speedFinal - speedBase} |");
        sb.AppendLine($"| 守備(Fielding) | {c.Trainability.Fielding} | {fieldFinal} | {fieldBase} | {fieldFinal - fieldBase} |");
        sb.AppendLine($"| 球速(Velocity) | {c.Trainability.Velocity} | {veloFinal} | {veloBase} | {veloFinal - veloBase} |");
        sb.AppendLine();
        sb.AppendLine("## 生成コホート（200シード）の才能上限gap平均");
        sb.AppendLine();
        sb.AppendLine($"- 足(Speed) 平均gap: {GapMean(AbilityKind.Speed):F1}（圧縮対象）");
        sb.AppendLine($"- 守備(Fielding) 平均gap: {GapMean(AbilityKind.Fielding):F1}");
        sb.AppendLine($"- ミート(Contact) 平均gap: {GapMean(AbilityKind.Contact):F1}");
        sb.AppendLine($"- 逸材出現率: {prodigyRate:P1}");

        var outDir = Path.Combine(BalanceRegressionTests.FindDataFile("coefficients.yaml"), "..", "..", "out", "overnight", "issue-114");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "growth-report.md"), sb.ToString());

        // 手触りの回帰: 足はほぼ据え置き（trainability 1.0 基準より明確に伸びが小さい）、守備は基準より伸びる。
        Assert.True(speedFinal < speedBase, $"足が圧縮されていない (出荷{speedFinal} vs 基準{speedBase})");
        Assert.True(fieldFinal > fieldBase, $"守備が伸びやすくなっていない (出荷{fieldFinal} vs 基準{fieldBase})");
        Assert.Equal(veloFinal, veloBase); // 球速は 1.0 固定＝基準と一致
        Assert.True(GapMean(AbilityKind.Speed) < GapMean(AbilityKind.Contact), "足の天井が近くなっていない");
    }
}
