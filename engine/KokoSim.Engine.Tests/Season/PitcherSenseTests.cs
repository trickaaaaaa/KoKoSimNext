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
/// 投手センス（隠し能力・設計書01 §1.3・issue #1472）: 選手に持たせ、投手系の伸びやすさ・見極めの見立てに効く。
/// 生成は全員に独立の乱数で引く（issue #1523）。見出し（見立て→外部コーチ）は <see cref="PitcherTalentTests"/>。
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

    // --- 生成: 全員に独立の乱数で引く（issue #1523）。投手か野手かを見ない ---

    [Fact]
    public void Generator_GivesEveryPlayerAnIndependentSense_PitchersAndFieldersAlike()
    {
        var all = Cohorts(60);
        var pitchers = all.Where(p => PitcherRoleResolver.LeansPitcher(p)).Select(p => (double)AbilityScale.Phys(p.PitcherSense)).ToList();
        var fielders = all.Where(p => !PitcherRoleResolver.LeansPitcher(p)).Select(p => (double)AbilityScale.Phys(p.PitcherSense)).ToList();
        Assert.NotEmpty(pitchers);
        Assert.NotEmpty(fielders);
        Assert.All(all, p => Assert.InRange(AbilityScale.Phys(p.PitcherSense), 5, 95));
        Assert.True(all.Select(p => p.PitcherSense).Distinct().Count() > 20);

        var phys = all.Select(p => (double)AbilityScale.Phys(p.PitcherSense)).ToList();
        var mean = phys.Average();
        var sd = System.Math.Sqrt(phys.Average(x => (x - mean) * (x - mean)));
        _out.WriteLine($"投手センス（物理Lv）全体: 平均 {mean:F1} σ {sd:F1} ／ 投手 {pitchers.Average():F1} ／ 野手 {fielders.Average():F1}");
        Assert.InRange(mean, Rc.PitcherSenseMean - 1.5, Rc.PitcherSenseMean + 1.5);
        Assert.InRange(sd, Rc.PitcherSenseIndependentSd * 0.85, Rc.PitcherSenseIndependentSd * 1.15);
        // 投手に読める選手と野手で分布が変わらない（役判定を見ない）。
        Assert.InRange(pitchers.Average() - fielders.Average(), -2.5, 2.5);
        // 野手にも高センスが自然に混ざる（+1.5σ 以上が数％〜1割）。
        var highFielders = fielders.Count(x => x >= Rc.PitcherSenseMean + 1.5 * Rc.PitcherSenseIndependentSd) / (double)fielders.Count;
        Assert.InRange(highFielders, 0.03, 0.12);
        // 見立ては生成時には無い（見極めで初めて付く）。見出しの段も None。
        Assert.All(all, p => Assert.Null(p.PitcherSenseReading));
        Assert.All(all, p => Assert.Equal(PitcherTalentStage.None, p.PitcherTalent));
    }

    [Fact]
    public void Generator_IsDeterministic()
    {
        var a = ProspectGenerator.Intake(3, Rc, new Xoshiro256Random(77));
        var b = ProspectGenerator.Intake(3, Rc, new Xoshiro256Random(77));
        Assert.Equal(a.Select(p => p.PitcherSense), b.Select(p => p.PitcherSense));
    }

    [Fact]
    public void Generator_SenseIsDetachedFromTheVelocityFoundation()
    {
        // 球速 Lv の土台は従来どおり「地肩×0.5＋球速の土台（投手適性で決まる）×0.5」。選手が持つ投手センスは土台と切り離した
        // 独立の値＝地肩で説明できない球速の残りと、投手センスに相関が無い（旧: 残りの0.5倍がそのまま投手センスだった）。
        var fielders = Cohorts(40).Where(p => !PitcherRoleResolver.LeansPitcher(p)).ToList();
        var resid = fielders.Select(p => (double)AbilityScale.Phys(p.Level(AbilityKind.Velocity))
                                          - AbilityScale.Phys(p.Level(AbilityKind.ArmStrength)) * Rc.VelocityArmWeight).ToList();
        var sense = fielders.Select(p => (double)AbilityScale.Phys(p.PitcherSense)).ToList();
        var r = Correlation(resid, sense);
        _out.WriteLine($"球速の残り×投手センスの相関 r={r:F3}（n={fielders.Count}）");
        Assert.InRange(r, -0.15, 0.15);
    }

    private static double Correlation(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        var mx = xs.Average();
        var my = ys.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            sxy += (xs[i] - mx) * (ys[i] - my);
            sxx += (xs[i] - mx) * (xs[i] - mx);
            syy += (ys[i] - my) * (ys[i] - my);
        }
        return sxy / System.Math.Sqrt(sxx * syy);
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
        TrainingCoefficients? coeff = null, bool discovered = false)
    {
        var p = src.Clone();
        p.PitcherSense = AbilityScale.Disp(sensePhys);
        if (discovered) PitcherTalentModel.Discover(p, new InsightCoefficients());
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
        // （投げ込み・球速強化・変化球を週替わり）を振る。投手センスだけを高（物理45）と低（10）に置き換えて比べる
        // ＝能力・伸びしろ・成長タイプは同じ選手。高センスの選手が外部コーチに見出された（覚醒）ときと、見出されないときも並べる。
        var fielders = Cohorts(20).Where(p => !PitcherRoleResolver.LeansPitcher(p)).Take(150).ToList();
        Assert.True(fielders.Count >= 100);
        var awakened = fielders.Select(p => ConvertAndTrain(p, 45, discovered: true)).ToList();
        var hiHidden = fielders.Select(p => ConvertAndTrain(p, 45)).ToList();
        var mid = fielders.Select(p => ConvertAndTrain(p, 30)).ToList();
        var lo = fielders.Select(p => ConvertAndTrain(p, 10)).ToList();
        string Row(string name, List<(double Kmh, double Control, double Rank, double Stamina)> xs)
            => $"{name}: 球速 +{xs.Average(x => x.Kmh):F1}km/h 制球 +{xs.Average(x => x.Control):F1} キレ +{xs.Average(x => x.Rank):F1} スタミナ +{xs.Average(x => x.Stamina):F1}";
        _out.WriteLine($"野手 {fielders.Count} 人を3年間投手練習（施設なし・指導力既定）");
        _out.WriteLine(Row("高センス（物理45）・見出された", awakened));
        _out.WriteLine(Row("高センス（物理45）・見出されない", hiHidden));
        _out.WriteLine(Row("並（物理30）", mid));
        _out.WriteLine(Row("低（物理10）", lo));

        double Sum(List<(double Kmh, double Control, double Rank, double Stamina)> xs)
            => xs.Average(x => x.Control + x.Rank + x.Stamina);
        var ratio = Sum(awakened) / Sum(lo);
        _out.WriteLine($"制球+キレ+スタミナの伸び 見出された高 / 低 = {ratio:F2}（見出されない高 / 低 = {Sum(hiHidden) / Sum(lo):F2}）");

        // 完了条件（issue #1523）: 見出された高センスの野手は、低センスの野手の2倍以上伸びる。
        Assert.True(Sum(awakened) > Sum(hiHidden) && Sum(hiHidden) >= Sum(mid) && Sum(mid) > Sum(lo), "見出された＞高＞並＞低の順に伸びるはず");
        Assert.True(ratio >= 2.0, $"見出された高センスの野手は投手転向で大きく伸びるはず: 高/低={ratio:F2}");
        Assert.True(awakened.Average(x => x.Kmh) > lo.Average(x => x.Kmh) * 1.5, "球速も大きく伸びるはず");
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
