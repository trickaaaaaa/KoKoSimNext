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
        // ティア圧縮の膝を外し逆算配分の目標も圧縮後へ（issue prefecture-dominance・2026-08-18）: 一様0.55だけだと上位校の
        // 能力中心が落ち、表示ティア別のエース最速が C 135.6／B 138.1／A 137.8 と横一線になった（A校のエースが 138km/h）。
        // 上位集中＝スター型（issue prefecture-dominance-star）でエース格の投手能力中心を raw へ戻した後: C 137.4／B 142.4／
        // A 145.0／S 145.3（n=5・小標本）＝案A の実測（C 139.6／B 144.6／A 148.9）にほぼ戻る。ティア=表示（raw）で集計。
        // 圧縮撤去（設計書21・2026-08-19。寝かせる役は ability_effect へ）＋スター lift=(強さ−40)×0.25: C 140.3／B 148.2／
        // A 149.6／S 149.6（A/S は Lv99 のクランプ 149.6 に張り付く＝150 超は出ない）。lift 0 でも B 146.0／A 149.6。
        var byTier = AceKmhByTier();
        foreach (var t in new[] { Tier.S, Tier.A, Tier.B, Tier.C, Tier.D, Tier.E, Tier.F, Tier.G })
        {
            if (!byTier.TryGetValue(t, out var xs)) continue;
            _out.WriteLine($"{t}: n={xs.Count} p10 {Pct(xs, .1):F1} / p50 {Pct(xs, .5):F1} / p90 {Pct(xs, .9):F1} | 145+ {100.0 * xs.Count(x => x >= 145) / xs.Count:F0}%");
        }
        Assert.InRange(Pct(byTier[Tier.C], .5), 136.0, 143.0);
        Assert.InRange(Pct(byTier[Tier.B], .5), 143.0, 149.0);
        Assert.InRange(Pct(byTier[Tier.A], .5), 145.0, 150.0);   // n=5・小標本。一様圧縮だけの世界（137.8）は帯外
        Assert.InRange(Pct(byTier[Tier.D], .5), 132.0, 137.5);
        Assert.InRange(Pct(byTier[Tier.E], .5), 130.0, 135.0);   // E 以下は変更前後で不変（Lv≦50 側）
        Assert.InRange(Pct(byTier[Tier.G], .5), 116.0, 125.0);   // 逆算配分の目標が圧縮後（G は raw より上）になり +0.5
        // C校で 145+ のエースは 1 割強まで（変更前 25%）。
        // 上限 0.10→0.13（issue prefecture-high-rank-distribution・2026-09-05）: 母集団のσ 13→15 で
        // 各ティア帯（Cなら 60〜70）の中の密度が平たくなり、C校が帯の上寄りへ寄る＝エースの裾が少し伸びた
        // （実測 10%→11%。中央値は 140.3→140.6 でほぼ不動＝「C校が強くなった」のではなく帯内の分布の話）。
        // 併せて先頭600校の内訳も変わっている（校数テーブルを高野連の実加盟校数へ差し替えたため）。
        Assert.True(byTier[Tier.C].Count(x => x >= 145) <= byTier[Tier.C].Count * 0.13,
            $"C校の 145+ 率 {100.0 * byTier[Tier.C].Count(x => x >= 145) / byTier[Tier.C].Count:F0}%");
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
