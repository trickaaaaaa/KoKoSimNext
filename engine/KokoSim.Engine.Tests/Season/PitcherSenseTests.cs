using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 投手センス（隠し能力・設計書01 §1.3・issue #1472）: 生成時の一時値を選手に持たせ、投手系の伸びやすさ・見極めの見立てに効く。
/// 試合側の効き（配球の選び分け・盗塁の読み・牽制）は Match 側のテストが見る。
/// </summary>
public sealed class PitcherSenseTests
{
    private readonly ITestOutputHelper _out;
    public PitcherSenseTests(ITestOutputHelper output) => _out = output;

    private static readonly RosterCoefficients Rc = new();

    private static List<DevelopingPlayer> Cohorts(int years)
    {
        var all = new List<DevelopingPlayer>();
        for (var y = 0; y < years; y++)
            all.AddRange(ProspectGenerator.Intake(y, Rc, new Xoshiro256Random((ulong)(y + 1))));
        return all;
    }

    // --- 生成: 一時値をそのまま持つ（分布は従来の一時値と同じ＝投手に読める選手ほど高い） ---

    [Fact]
    public void Generator_GivesEveryPlayerASense_PitchersHigherThanFielders()
    {
        var all = Cohorts(60);
        var pitchers = all.Where(p => PitcherRoleResolver.LeansPitcher(p)).Select(p => AbilityScale.Phys(p.PitcherSense)).ToList();
        var fielders = all.Where(p => !PitcherRoleResolver.LeansPitcher(p)).Select(p => AbilityScale.Phys(p.PitcherSense)).ToList();
        Assert.NotEmpty(pitchers);
        Assert.NotEmpty(fielders);
        // 物理Lvで 5〜95（生成のクランプ）。全員が同じ値ではない（個体差 σ6）。
        Assert.All(all, p => Assert.InRange(AbilityScale.Phys(p.PitcherSense), 5, 95 + 0));
        Assert.True(all.Select(p => p.PitcherSense).Distinct().Count() > 10);
        Assert.True(pitchers.Average() > fielders.Average() + 8,
            $"投手に読める選手ほど投手センスが高いはず: pitchers={pitchers.Average():F1} fielders={fielders.Average():F1}");
        // 見立ては生成時には無い（見極めで初めて付く）。
        Assert.All(all, p => Assert.Null(p.PitcherSenseReading));
    }

    [Fact]
    public void Generator_IsDeterministic()
    {
        var a = ProspectGenerator.Intake(3, Rc, new Xoshiro256Random(77));
        var b = ProspectGenerator.Intake(3, Rc, new Xoshiro256Random(77));
        Assert.Equal(a.Select(p => p.PitcherSense), b.Select(p => p.PitcherSense));
    }

    [Fact]
    public void Generator_SenseIsTheVelocityFoundation()
    {
        // 球速 Lv の土台＝地肩×0.5＋投手センス×0.5（球質タイプの上乗せは投手らしさで薄まる）。野手（型の上乗せがほぼ0）で
        // 「地肩とセンスから組んだ球速」と実際の球速が近い＝持たせた値が球速を作った一時値そのものであること。
        var fielders = Cohorts(30).Where(p => !PitcherRoleResolver.LeansPitcher(p)).ToList();
        var err = fielders.Select(p =>
        {
            var expected = AbilityScale.Phys(p.Level(AbilityKind.ArmStrength)) * Rc.VelocityArmWeight
                           + AbilityScale.Phys(p.PitcherSense) * Rc.VelocitySenseWeight;
            return System.Math.Abs(AbilityScale.Phys(p.Level(AbilityKind.Velocity)) - expected);
        }).Average();
        Assert.True(err < 3.0, $"球速が地肩＋投手センスから外れている: 平均誤差 {err:F2}");
    }

    // --- AI校: 同じ形で全員が持つ ---

    [Fact]
    public void AiTeams_CarrySense_AceAboveFielders_RisingWithStrength()
    {
        double AceMean(double strength)
        {
            var xs = new List<int>();
            for (var i = 0; i < 200; i++)
            {
                var t = StrengthTeamFactory.Create(strength, "x", new Xoshiro256Random((ulong)(i + 11)));
                xs.AddRange(t.BattingOrder.Where(p => p.Position == FieldPosition.Pitcher).Select(p => p.PitcherSense));
            }
            return xs.Average();
        }
        var team = StrengthTeamFactory.Create(55, "x", new Xoshiro256Random(5));
        var ace = team.BattingOrder.First(p => p.Position == FieldPosition.Pitcher);
        var fielderMean = team.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher).Average(p => p.PitcherSense);
        Assert.True(ace.PitcherSense > fielderMean, $"エース {ace.PitcherSense} 野手平均 {fielderMean:F1}");
        Assert.True(AceMean(70) > AceMean(40) + 10, "強い学校ほどエースの投手センスが高いはず");
    }

    // --- 投手系の伸びやすさ ---

    [Fact]
    public void GrowthFactor_NoBonusAboveThirty_SlowerBelow()
    {
        var c = new TrainingCoefficients();
        Assert.Equal(1.0, c.PitcherSenseGrowthFactor(AbilityScale.Disp(30)), 9);
        // 上乗せはしない（上限1.0・オーナー判断 2026-11-23）＝物理30以上は本来どおり。
        Assert.Equal(1.0, c.PitcherSenseGrowthMax, 9);
        Assert.Equal(1.0, c.PitcherSenseGrowthFactor(AbilityScale.Disp(45)), 9);
        Assert.Equal(1.0, c.PitcherSenseGrowthFactor(99), 9);
        // 表示1＝物理 1+Δ＝8。1+(8−30)×0.03＝0.34 は下限で止まる。
        Assert.Equal(c.PitcherSenseGrowthMin, c.PitcherSenseGrowthFactor(1), 9);
        Assert.Equal(1.0 + (20 - 30) * c.PitcherSenseGrowthSlope, c.PitcherSenseGrowthFactor(AbilityScale.Disp(20)), 9);
    }

    private static DevelopingPlayer Blank(int senseDisplay)
    {
        var p = new DevelopingPlayer { PitcherSense = senseDisplay };
        foreach (var k in AbilityKinds.All)
        {
            p.SetLevel(k, 20);
            p.SetCap(k, 99);
        }
        return p;
    }

    [Fact]
    public void HighSense_GrowsPitchingFaster_BattingUnaffected()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var high = Blank(AbilityScale.Disp(60));
        var low = Blank(AbilityScale.Disp(10));
        for (var w = 0; w < 30; w++)
            foreach (var p in new[] { high, low })
            {
                DevelopmentModel.TrainWeek(p, TrainingMenu.Pitching, 0, 1.0, stages, c);
                DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 0, 1.0, stages, c);
            }
        Assert.True(high.Level(AbilityKind.Control) > low.Level(AbilityKind.Control) + 3,
            $"投手センスが高いほど制球が伸びるはず: high={high.Level(AbilityKind.Control)} low={low.Level(AbilityKind.Control)}");
        // 打撃は投手センスと独立。
        Assert.Equal(high.Level(AbilityKind.Contact), low.Level(AbilityKind.Contact));
        Assert.Equal(high.Exp(AbilityKind.Contact), low.Exp(AbilityKind.Contact), 9);
    }

    // --- 「投手転向で覚醒」の測定（オーナーコメント 2026-11-23） ---

    /// <summary>週あたりの成長段階（入学4月〜3年夏の引退まで＝設計書02 §5.2 の 6/6/6/6/4 か月）。</summary>
    private static IEnumerable<int> ThreeYearStages(GrowthStageTable t)
    {
        for (var s = 0; s < t.StageMonths.Count; s++)
            for (var w = 0; w < (int)System.Math.Round(t.StageMonths[s] * 52.0 / 12.0); w++)
                yield return s;
    }

    private static readonly TrainingMenu[] PitchingRotation =
        { TrainingMenu.Pitching, TrainingMenu.VelocityTraining, TrainingMenu.BreakingBall };

    private static (double Kmh, double Control, double Rank, double Stamina) ConvertAndTrain(DevelopingPlayer src, int sensePhys,
        TrainingCoefficients? coeff = null)
    {
        var p = src.Clone();
        p.PitcherSense = AbilityScale.Disp(sensePhys);
        var c = coeff ?? new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var kmh0 = PitcherAttributes.VelocityKmhFromLevel(p.Level(AbilityKind.Velocity));
        var c0 = p.Level(AbilityKind.Control);
        var r0 = p.Level(AbilityKind.PitchRank);
        var s0 = p.Level(AbilityKind.Stamina);
        var i = 0;
        foreach (var stage in ThreeYearStages(stages))
            DevelopmentModel.TrainWeek(p, PitchingRotation[i++ % PitchingRotation.Length], stage, 1.0, stages, c);
        return (PitcherAttributes.VelocityKmhFromLevel(p.Level(AbilityKind.Velocity)) - kmh0,
            p.Level(AbilityKind.Control) - c0, p.Level(AbilityKind.PitchRank) - r0, p.Level(AbilityKind.Stamina) - s0);
    }

    [Fact]
    public void PitcherConversion_MediocreFielderWithHighSense_Awakens()
    {
        // 既定の新入生（talent_center_default）から投手に読めない＝野手の選手を集め、3年間ずっと投手の練習
        // （投げ込み・球速強化・変化球を週替わり）を振る。投手センスだけを野手の上位（物理35≈p99）と下位（10≈p10）に
        // 置き換えて比べる＝能力・伸びしろ・成長タイプは同じ選手。
        var fielders = Cohorts(20).Where(p => !PitcherRoleResolver.LeansPitcher(p)).Take(150).ToList();
        Assert.True(fielders.Count >= 100);
        var hi = fielders.Select(p => ConvertAndTrain(p, 35)).ToList();
        var mid = fielders.Select(p => ConvertAndTrain(p, 16)).ToList();
        var lo = fielders.Select(p => ConvertAndTrain(p, 10)).ToList();
        string Row(string name, List<(double Kmh, double Control, double Rank, double Stamina)> xs)
            => $"{name}: 球速 +{xs.Average(x => x.Kmh):F1}km/h 制球 +{xs.Average(x => x.Control):F1} キレ +{xs.Average(x => x.Rank):F1} スタミナ +{xs.Average(x => x.Stamina):F1}";
        _out.WriteLine($"野手 {fielders.Count} 人を3年間投手練習（施設なし・指導力既定）");
        _out.WriteLine(Row("投手センス高（物理35）", hi));
        _out.WriteLine(Row("投手センス並（物理16＝野手の中央）", mid));
        _out.WriteLine(Row("投手センス低（物理10）", lo));

        double Sum(List<(double Kmh, double Control, double Rank, double Stamina)> xs)
            => xs.Average(x => x.Control + x.Rank + x.Stamina);
        var ratio = Sum(hi) / Sum(lo);
        _out.WriteLine($"制球+キレ+スタミナの伸び 高/低 = {ratio:F2}（既定の傾き {new TrainingCoefficients().PitcherSenseGrowthSlope}）");

        // オーナー判断（2026-11-23・傾き0.03・倍率0.5〜1.0＝上乗せせず下だけ効かせる）: 実測 高/低 1.40。高＞並＞低。
        Assert.True(Sum(hi) > Sum(mid) && Sum(mid) > Sum(lo), "投手センスの順に伸びるはず");
        Assert.True(ratio > 1.35, $"投手センスが高い野手は投手転向でよく伸びるはず: 高/低={ratio:F2}");
        Assert.True(hi.Average(x => x.Kmh) > lo.Average(x => x.Kmh), "球速も伸びるはず");
    }

    // --- 見極め: 見立てを記録する ---

    [Fact]
    public void Insight_RecordsSenseReading_AccurateEqualsTruth_MisreadIsOffset()
    {
        var c = new InsightCoefficients
        {
            BaseWeeklyProb = 1.0, ProbPerTalentEye = 0, RecentGrowthTopicProb = 0, PersonalityTopicProb = 0,
            PitcherSenseTopicProb = 1.0, HiddenSkillTopicProb = 0,
        };
        var roster = Enumerable.Range(0, 200).Select(i => new DevelopingPlayer { Id = i + 1, PitcherSense = 40 }).ToList();
        var notices = InsightModel.Week(roster, 50, new Xoshiro256Random(3), c);
        Assert.Equal(roster.Count, notices.Count);
        Assert.All(notices, n => Assert.Equal(InsightTopic.PitcherSense, n.Topic));
        Assert.Contains(notices, n => n.IsAccurate);
        Assert.Contains(notices, n => !n.IsAccurate);
        foreach (var n in notices)
        {
            Assert.Equal(n.PitcherSenseReading, n.Player.PitcherSenseReading);
            if (n.IsAccurate) Assert.Equal(40, n.PitcherSenseReading);
            else Assert.Equal(c.PitcherSenseMisreadOffset, System.Math.Abs(n.PitcherSenseReading!.Value - 40));
        }
    }

    // --- 物差し（issue #1080）: 投手センスも表示Lvで持ち、+Δ で読む ---

    [Fact]
    public void Scale_ShiftsSenseWithOtherAbilities()
    {
        var player = new Player { PitcherSense = 30 };
        Assert.Equal(30 + AbilityScale.Delta, AbilityScale.ToPhysical(player).PitcherSense);
        var dp = new DevelopingPlayer { PitcherSense = 30, PitcherSenseReading = 20 };
        DevelopingScale.ToPhysical(dp);
        Assert.Equal(30 + AbilityScale.Delta, dp.PitcherSense);
        Assert.Equal(20 + AbilityScale.Delta, dp.PitcherSenseReading);
    }

    [Fact]
    public void Projection_CarriesSenseIntoTheMatchPlayer()
    {
        var dp = ProspectGenerator.Intake(1, Rc, new Xoshiro256Random(9))[0];
        var clone = dp.Clone();
        Assert.Equal(dp.PitcherSense, clone.PitcherSense);
        var player = RosterTeamBuilder.ToPlayer(dp, FieldPosition.Pitcher);
        Assert.Equal(dp.PitcherSense, player.PitcherSense);
    }
}
