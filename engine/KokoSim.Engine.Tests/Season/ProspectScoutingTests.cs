using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 入部予定者の事前開示（設計書04 §2.1 効果2・issue #587）。スカウト網の契約段を上げるほど
/// 「見立てが実態に近づき、？が減る」ことを固定する。表示専用＝選手そのものには触れない。
/// </summary>
public sealed class ProspectScoutingTests
{
    private static readonly RosterCoefficients Roster = new();
    private static readonly TeamStrengthCoefficients Strength = new();
    private static readonly ProspectScoutingCoefficients Scout = new();

    /// <summary>スカウト網の段が上がるほど推定誤差が縮む（Lv1 &gt; Lv2 &gt; Lv3）。</summary>
    [Fact]
    public void HigherContractTier_ShrinksEstimateError()
    {
        var errors = new List<double>();
        foreach (var level in new[] { 1, 2, 3 })
            errors.Add(MeanAbsoluteError(DisclosureOf(level), seed: 560000));

        Assert.True(errors[0] > errors[1], $"Lv1({errors[0]:F2}) が Lv2({errors[1]:F2}) 以下");
        Assert.True(errors[1] > errors[2], $"Lv2({errors[1]:F2}) が Lv3({errors[2]:F2}) 以下");
    }

    /// <summary>未契約（開示0）はいちばん当てにならない。</summary>
    [Fact]
    public void NoContract_HasLargestError()
    {
        var none = MeanAbsoluteError(DisclosureOf(0), seed: 561000);
        var lv1 = MeanAbsoluteError(DisclosureOf(1), seed: 561000);
        Assert.True(none > lv1, $"未契約({none:F2}) が Lv1({lv1:F2}) 以下");
    }

    /// <summary>段が上がるほど隠しパラメータの「？」が減る。</summary>
    [Fact]
    public void HigherContractTier_RevealsMoreHiddenParams()
    {
        var unknowns = new[] { 0, 1, 2, 3 }.Select(lv => MeanUnknownCount(DisclosureOf(lv), seed: 562000)).ToList();
        for (var i = 1; i < unknowns.Count; i++)
            Assert.True(unknowns[i] < unknowns[i - 1],
                $"段{i} の？平均({unknowns[i]:F2}) が段{i - 1}({unknowns[i - 1]:F2}) 以上");
    }

    /// <summary>同シード同結果（不変条件#2）。</summary>
    [Fact]
    public void Survey_IsDeterministic()
    {
        var cohort = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random(563000));
        var a = ProspectScouting.Survey(cohort, 0.45, Strength, Scout, new Xoshiro256Random(999));
        var b = ProspectScouting.Survey(cohort, 0.45, Strength, Scout, new Xoshiro256Random(999));

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Batting, b[i].Batting, 12);
            Assert.Equal(a[i].Pitching, b[i].Pitching, 12);
            Assert.Equal(a[i].UnknownCount, b[i].UnknownCount);
        }
    }

    /// <summary>見立ては選手に触れない（能力・適性は下見の前後で不変）。</summary>
    [Fact]
    public void Survey_DoesNotMutatePlayers()
    {
        var cohort = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random(564000));
        var before = cohort.Select(p => PlayerStrengthProfile.Compute(p, Strength)).ToList();

        ProspectScouting.Survey(cohort, 0.75, Strength, Scout, new Xoshiro256Random(1));

        var after = cohort.Select(p => PlayerStrengthProfile.Compute(p, Strength)).ToList();
        Assert.Equal(before, after);
    }

    /// <summary>推定値は 0〜100 に収まる（極端な乱数でも画面がはみ出さない）。</summary>
    [Fact]
    public void Estimates_StayInRange()
    {
        for (var s = 0; s < 200; s++)
        {
            var cohort = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random((ulong)(565000 + s)));
            foreach (var r in ProspectScouting.Survey(cohort, 0.0, Strength, Scout, new Xoshiro256Random((ulong)s)))
            {
                Assert.InRange(r.Batting, 0.0, 100.0);
                Assert.InRange(r.Mobility, 0.0, 100.0);
                Assert.InRange(r.Defense, 0.0, 100.0);
                Assert.InRange(r.Pitching, 0.0, 100.0);
                Assert.InRange(r.UnknownCount, 0, 4);
            }
        }
    }

    /// <summary>開示精度は契約段で単調に上がる（カタログ側の固定）。</summary>
    [Fact]
    public void ContractCatalog_DisclosureIsMonotonic()
    {
        var d = new[] { 0, 1, 2, 3 }.Select(DisclosureOf).ToList();
        for (var i = 1; i < d.Count; i++) Assert.True(d[i] > d[i - 1], $"段{i} の開示が段{i - 1} 以下");
        Assert.InRange(d[^1], 0.0, 1.0);
    }

    // --- 補助 ---

    private static double DisclosureOf(int scoutingLevel)
    {
        var set = new ContractSet();
        set.SetLevel("scouting", scoutingLevel);
        return ContractCatalog.Default.Aggregate(set).ProspectDisclosure;
    }

    private static double MeanAbsoluteError(double disclosure, int seed)
    {
        double sum = 0;
        var n = 0;
        for (var s = 0; s < 300; s++)
        {
            var cohort = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random((ulong)(seed + s)));
            var reports = ProspectScouting.Survey(
                cohort, disclosure, Strength, Scout, new Xoshiro256Random((ulong)(seed + 100000 + s)));
            for (var i = 0; i < cohort.Count; i++)
            {
                var actual = PlayerStrengthProfile.Compute(cohort[i], Strength);
                sum += Math.Abs(actual.Batting - reports[i].Batting)
                       + Math.Abs(actual.Pitching - reports[i].Pitching);
                n += 2;
            }
        }
        return sum / n;
    }

    private static double MeanUnknownCount(double disclosure, int seed)
    {
        double sum = 0;
        var n = 0;
        for (var s = 0; s < 300; s++)
        {
            var cohort = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random((ulong)(seed + s)));
            foreach (var r in ProspectScouting.Survey(
                         cohort, disclosure, Strength, Scout, new Xoshiro256Random((ulong)(seed + 100000 + s))))
            {
                sum += r.UnknownCount;
                n++;
            }
        }
        return sum / n;
    }
}
