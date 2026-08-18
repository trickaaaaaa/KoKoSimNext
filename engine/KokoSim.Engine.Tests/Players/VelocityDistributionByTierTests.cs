using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// AI校のエース最速のティア別分布（issue velocity-kmh-mapping-flatten-top）。
/// 全国生成 seed 2026・先頭 600 校を年度3でロスター実体化し、ベンチ入り投手のうち投手能力上位（エース）の最高球速を
/// ティアごとに集計する。
/// </summary>
public sealed class VelocityDistributionByTierTests
{
    private readonly ITestOutputHelper _out;
    public VelocityDistributionByTierTests(ITestOutputHelper o) { _out = o; }

    internal static Dictionary<Tier, List<double>> AceKmhByTier(int schools = 600, ulong seed = 2026, int yearIndex = 3)
    {
        var nation = NationGenerator.Generate(new SchoolNameVocab(), new NationCoefficients(), new Xoshiro256Random(seed));
        var deps = new AiRosterDeps();
        var result = new Dictionary<Tier, List<double>>();
        foreach (var school in nation.Schools.Take(schools))
        {
            var roster = AiRosterFactory.Bootstrap(school, yearIndex, deps);
            var team = AiTeamBuilder.Build(roster, school, yearIndex, deps);
            var pitchers = StrengthTeamFactory.AllPlayers(team).Where(p => p.Position == FieldPosition.Pitcher)
                .OrderByDescending(p => p.Pitching.ReliefScore()).ToList();
            if (pitchers.Count == 0) continue;
            if (!result.TryGetValue(school.Tier, out var list)) result[school.Tier] = list = new List<double>();
            list.Add(pitchers[0].Pitching.MaxVelocityKmh);
        }
        return result;
    }

    private static double Pct(List<double> xs, double q)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Floor(q * s.Count))];
    }

    [Fact]
    public void AceVelocity_ByTier_MatchesRealHighSchoolBaseball()
    {
        // 変更前（傾き 0.47 一本）: C 142／B 148／A 154／S 155（145+ が C校の 25%・A/S 全員 155 で飽和）。
        // 案A（Lv>50 を 0.36 で寝かせる）: C 139.6／B 144.6／A 148.9／S 149.6。
        // ティア圧縮の膝を外し逆算配分の目標も圧縮後へ（issue prefecture-dominance・2026-08-18）: 上位校の能力中心が
        // 一様0.55（raw 80→62・85→64.75）に落ちるので、表示ティア別のエース最速は C 135.6／B 138.1／A 137.8（n=5）。
        // 「表示Aのエースが 138km/h」は膝廃止の代償（オーナー承認）。ティア=表示（raw）のまま集計する。
        var byTier = AceKmhByTier();
        foreach (var t in new[] { Tier.S, Tier.A, Tier.B, Tier.C, Tier.D, Tier.E, Tier.F, Tier.G })
        {
            if (!byTier.TryGetValue(t, out var xs)) continue;
            _out.WriteLine($"{t}: n={xs.Count} p10 {Pct(xs, .1):F1} / p50 {Pct(xs, .5):F1} / p90 {Pct(xs, .9):F1} | 145+ {100.0 * xs.Count(x => x >= 145) / xs.Count:F0}%");
        }
        Assert.InRange(Pct(byTier[Tier.C], .5), 133.0, 139.0);
        Assert.InRange(Pct(byTier[Tier.B], .5), 135.0, 142.0);
        Assert.InRange(Pct(byTier[Tier.A], .5), 135.0, 143.0);   // n=5・小標本
        Assert.InRange(Pct(byTier[Tier.D], .5), 131.0, 137.0);
        Assert.InRange(Pct(byTier[Tier.E], .5), 130.0, 135.0);   // E 以下は変更前後で不変（Lv≦50 側）
        Assert.InRange(Pct(byTier[Tier.G], .5), 116.0, 124.0);
        // C校で 145+ のエースは 1 割未満（変更前 25%）。
        Assert.True(byTier[Tier.C].Count(x => x >= 145) <= byTier[Tier.C].Count * 0.10);
        Assert.InRange(PitcherAttributes.VelocityKmhFromLevel(99), 149.0, 151.0);
    }

    [Fact]
    public void Mapping_IsPiecewiseLinear_AndRoundTrips()
    {
        Assert.Equal(132.0, PitcherAttributes.VelocityKmhFromLevel(50), 9);
        Assert.Equal(108.5 + 0.47 * 30, PitcherAttributes.VelocityKmhFromLevel(30), 9);   // Lv≦50 は従来どおり
        Assert.Equal(132.0 + 0.36 * 20, PitcherAttributes.VelocityKmhFromLevel(70), 9);
        // 単調増加・境で連続
        for (var lv = 1; lv < 100; lv++)
            Assert.True(PitcherAttributes.VelocityKmhFromLevel(lv + 1) > PitcherAttributes.VelocityKmhFromLevel(lv));
        // 往復一致（1〜100 全て）
        for (var lv = 1; lv <= 100; lv++)
            Assert.Equal(lv, PitcherAttributes.LevelFromVelocityKmh(PitcherAttributes.VelocityKmhFromLevel(lv)));
    }
}
