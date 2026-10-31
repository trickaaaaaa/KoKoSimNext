using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 個別指導の効果2「見極め」・効果3「信頼関係」（issue #702・設計書03 §3.3）。
/// ・指名中の選手は気づきの発生率と精度が上がる（InsightModel）。
/// ・指名中の選手は週ごとに精神力が伸びる（DevelopmentModel）。
/// ・誰も指名していなければ従来と1ビット一致（不変条件#2）。
/// </summary>
public sealed class IndividualCoachingInsightTrustTests
{
    private static readonly InsightCoefficients Ins = new();
    private static readonly InsightCoefficients InsOff = new() { NominatedProbFactor = 1.0, NominatedAccuracyAdd = 0.0 };

    private static DevelopingPlayer Guy(bool nominated)
    {
        var p = new DevelopingPlayer { IndividualCoaching = nominated, Personality = Personality.HotBlood };
        PitcherRoleResolver.SeedAptitudeForRole(p, false);
        return p;
    }

    // ===== 効果2: 見極め =====

    [Fact]
    public void Insight_Nominated_NoticedMoreOftenAndMoreAccurately()
    {
        var nominated = Guy(true);
        var other = Guy(false);
        var roster = new[] { nominated, other };
        var rng = new Xoshiro256Random(702);
        var all = new List<InsightNotice>();
        for (var w = 0; w < 40000; w++) all.AddRange(InsightModel.Week(roster, 50, rng, Ins));

        var mine = all.Where(n => n.Player == nominated).ToList();
        var theirs = all.Where(n => n.Player == other).ToList();
        // 発生率は×5（0.01 → 0.05/週）
        Assert.InRange(mine.Count / (double)theirs.Count, 4.3, 5.7);
        // 精度は 0.75 → 0.90
        Assert.InRange(mine.Count(n => n.IsAccurate) / (double)mine.Count, 0.87, 0.93);
        Assert.InRange(theirs.Count(n => n.IsAccurate) / (double)theirs.Count, 0.70, 0.80);
        Assert.All(mine, n => Assert.True(n.ViaCoaching));
        Assert.All(theirs, n => Assert.False(n.ViaCoaching));
    }

    [Fact]
    public void Insight_AccuracyStaysUnderCap()
    {
        var p = Guy(true);
        var rng = new Xoshiro256Random(7);
        var notices = new List<InsightNotice>();
        // 育成眼100＋備品+0.05 で素の精度は上限0.97。見極めを足しても上限は超えない（答え合わせにしない）。
        for (var w = 0; w < 60000; w++) notices.AddRange(InsightModel.Week(new[] { p }, 100, rng, Ins, 1.0, 0.05));
        var acc = notices.Count(n => n.IsAccurate) / (double)notices.Count;
        Assert.InRange(acc, 0.955, 0.985);
    }

    [Fact]
    public void Insight_NoNomination_BitIdenticalToEffectOff()
    {
        // 誰も指名していなければ、見極め係数があってもなくても結果は1ビットも変わらない。
        var rosterA = Enumerable.Range(0, 30).Select(_ => Guy(false)).ToArray();
        var rngA = new Xoshiro256Random(11);
        var rngB = new Xoshiro256Random(11);
        for (var w = 0; w < 500; w++)
        {
            var a = InsightModel.Week(rosterA, 60, rngA, Ins);
            var b = InsightModel.Week(rosterA, 60, rngB, InsOff);
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Same(a[i].Player, b[i].Player);
                Assert.Equal(a[i].Topic, b[i].Topic);
                Assert.Equal(a[i].Domain, b[i].Domain);
                Assert.Equal(a[i].IsAccurate, b[i].IsAccurate);
            }
        }
    }

    // ===== 効果3: 信頼関係 =====

    private static int MentalAfterWeeks(bool nominated, int weeks, TrainingCoefficients? c = null,
        InjurySeverity injury = InjurySeverity.None)
    {
        var p = Guy(nominated);
        p.Mental = 55;
        p.MentalCap = 99;
        p.Injury = injury;
        var stages = new GrowthStageTable();
        for (var w = 0; w < weeks; w++)
            DevelopmentModel.TrainWeek(p, TrainingMenu.Running, 1, 1.0, stages, c ?? new TrainingCoefficients());
        return p.Mental;
    }

    [Fact]
    public void Trust_NominatedForAYear_GainsAboutFiveMental()
    {
        Assert.Equal(55, MentalAfterWeeks(false, 48));
        Assert.InRange(MentalAfterWeeks(true, 48), 59, 61);
    }

    [Fact]
    public void Trust_AlsoAccruesWhileInjured()
    {
        Assert.True(MentalAfterWeeks(true, 48, injury: InjurySeverity.Minor) > 55);
    }

    [Fact]
    public void Trust_RespectsMentalCap()
    {
        var p = Guy(true);
        p.Mental = 60;
        p.MentalCap = 61;
        var stages = new GrowthStageTable();
        for (var w = 0; w < 200; w++)
            DevelopmentModel.TrainWeek(p, TrainingMenu.Running, 1, 1.0, stages, new TrainingCoefficients());
        Assert.Equal(61, p.Mental);
        Assert.Equal(0.0, p.MentalExp);
    }

    [Fact]
    public void Trust_ZeroExp_DisablesEffect()
    {
        var off = new TrainingCoefficients { IndividualCoachingMentalExp = 0.0 };
        Assert.Equal(55, MentalAfterWeeks(true, 48, off));
    }

    [Fact]
    public void Trust_AppliesInPlannedTrainingToo()
    {
        var p = Guy(true);
        p.Mental = 55;
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var alloc = TrainingPresets.Resolve(TrainingPlan.Default, c.DefaultBudgetMinutes);
        for (var w = 0; w < 48; w++)
            DevelopmentModel.TrainWeekPlan(p, alloc, c.ReferenceWeekMinutes, 1, 1.0, stages, c);
        Assert.InRange(p.Mental, 59, 61);
    }
}
