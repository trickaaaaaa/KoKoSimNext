using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の守備位置の割当が肩を見る（issue #767）。旧割当は肩を見ず、先発の肩は遊撃も一塁も同じ分布だった
/// （p50 48前後）。強肩の選手が遊撃・捕手・右翼へ入ることを、全国生成 seed 2026・先頭600校・年度3で固定する。
/// </summary>
public sealed class AiArmByPositionTests
{
    private readonly ITestOutputHelper _out;
    public AiArmByPositionTests(ITestOutputHelper o) { _out = o; }

    private static readonly PersistentRosterCoefficients NoArm = new()
    {
        FieldSlotArmWeightCatcher = 0, FieldSlotArmWeightFirstBase = 0, FieldSlotArmWeightSecondBase = 0,
        FieldSlotArmWeightThirdBase = 0, FieldSlotArmWeightShortstop = 0, FieldSlotArmWeightLeftField = 0,
        FieldSlotArmWeightCenterField = 0, FieldSlotArmWeightRightField = 0,
    };

    /// <summary>守備位置ごとの先発の肩（全校）と、格ごとの遊撃手の肩。</summary>
    private static (Dictionary<FieldPosition, List<int>> ByPos, Dictionary<Tier, List<int>> Shortstop) Measure(
        PersistentRosterCoefficients pc, int schools = 600)
    {
        var nation = NationGenerator.Generate(new SchoolNameVocab(), new NationCoefficients(), new Xoshiro256Random(2026));
        var deps = new AiRosterDeps { Persistent = pc };
        var byPos = new Dictionary<FieldPosition, List<int>>();
        var ss = new Dictionary<Tier, List<int>>();
        foreach (var school in nation.Schools.Take(schools))
        {
            var roster = AiRosterFactory.Bootstrap(school, 3, deps);
            var team = AiTeamBuilder.Build(roster, school, 3, deps);
            foreach (var p in StrengthTeamFactory.AllPlayers(team).Take(9))
            {
                if (p.Position == FieldPosition.Pitcher) continue;
                (byPos.TryGetValue(p.Position, out var l) ? l : byPos[p.Position] = new()).Add(p.ArmStrength);
                if (p.Position == FieldPosition.Shortstop)
                    (ss.TryGetValue(school.Tier, out var q) ? q : ss[school.Tier] = new()).Add(p.ArmStrength);
            }
        }
        return (byPos, ss);
    }

    private static double Median(List<int> xs)
    {
        var s = xs.OrderBy(v => v).ToList();
        return s[s.Count / 2];
    }

    private static double Share(List<int> xs, int atLeast) => (double)xs.Count(v => v >= atLeast) / xs.Count;

    [Fact]
    public void StrongArmsGoToShortstopCatcherRightField()
    {
        var (before, ssBefore) = Measure(NoArm);
        var (after, ssAfter) = Measure(new PersistentRosterCoefficients());
        foreach (var pos in after.Keys.OrderBy(k => k))
            _out.WriteLine($"{pos}: 肩p50 {Median(before[pos])} → {Median(after[pos])}／70+ {Share(before[pos], 70):P1} → {Share(after[pos], 70):P1}");
        foreach (var t in ssAfter.Keys.OrderBy(k => k))
            _out.WriteLine($"遊撃 {t}: p50 {Median(ssBefore[t])} → {Median(ssAfter[t])}／75+ {Share(ssBefore[t], 75):P1} → {Share(ssAfter[t], 75):P1}");

        // 遊撃・捕手・右翼は一塁・左翼より明確に肩が強い（旧: 差 ±2 以内）。
        foreach (var strong in new[] { FieldPosition.Shortstop, FieldPosition.Catcher, FieldPosition.RightField })
        foreach (var weak in new[] { FieldPosition.FirstBase, FieldPosition.LeftField })
            Assert.True(Median(after[strong]) >= Median(after[weak]) + 5,
                $"{strong} p50 {Median(after[strong])} vs {weak} p50 {Median(after[weak])}");
        // 遊撃手の肩は上がる（全校で）。
        var allSsBefore = ssBefore.Values.SelectMany(v => v).ToList();
        var allSsAfter = ssAfter.Values.SelectMany(v => v).ToList();
        Assert.True(Median(allSsAfter) >= Median(allSsBefore) + 4, $"遊撃 p50 {Median(allSsBefore)} → {Median(allSsAfter)}");
        // 中位校（D）にも強肩の遊撃手がいる（肩75＝127km/h 以上。σ6・位置重み付きでは 4%）。
        Assert.True(Share(ssAfter[Tier.D], 75) >= 0.20, $"D校の遊撃手で肩75以上 {Share(ssAfter[Tier.D], 75):P1}");
    }
}
