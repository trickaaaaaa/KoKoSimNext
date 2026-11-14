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
        // 上限 137.5→138.0（issue #767・2026-11-03）: AI校の肩の個人差σを 6→12 に広げ、肩から球速を推定する
        // 野手兼投手がエースに選ばれる校が出た（D 137.4→137.8・F/G +1.4〜1.9・C以上は不変）。
        Assert.InRange(Pct(byTier[Tier.D], .5), 132.0, 138.0);
        Assert.InRange(Pct(byTier[Tier.E], .5), 130.0, 135.0);   // E 以下は変更前後で不変（Lv≦50 側）
        Assert.InRange(Pct(byTier[Tier.G], .5), 116.0, 125.0);   // 逆算配分の目標が圧縮後（G は raw より上）になり +0.5
        // C校で 145+ のエースは 1 割強まで（変更前 25%）。
        // 上限 0.10→0.13（issue prefecture-high-rank-distribution・2026-09-05）: 母集団のσ 13→15 で
        // 各ティア帯（Cなら 60〜70）の中の密度が平たくなり、C校が帯の上寄りへ寄る＝エースの裾が少し伸びた
        // （実測 10%→11%。中央値は 140.3→140.6 でほぼ不動＝「C校が強くなった」のではなく帯内の分布の話）。
        // 併せて先頭600校の内訳も変わっている（校数テーブルを高野連の実加盟校数へ差し替えたため）。
        Assert.True(byTier[Tier.C].Count(x => x >= 145) <= byTier[Tier.C].Count * 0.13,
            $"C校の 145+ 率 {100.0 * byTier[Tier.C].Count(x => x >= 145) / byTier[Tier.C].Count:F0}%");
        // issue #908: Lv90 から上を伸ばして Lv100＝158（150km/h超を作れるように）。人数は AI校の天井で合わせる。
        Assert.Equal(158.0, PitcherAttributes.VelocityKmhFromLevel(100), 9);
    }

    [Fact]
    public void Mapping_IsPiecewiseLinear_AndRoundTrips()
    {
        Assert.Equal(132.0, PitcherAttributes.VelocityKmhFromLevel(50), 9);
        Assert.Equal(108.5 + 0.47 * 30, PitcherAttributes.VelocityKmhFromLevel(30), 9);   // Lv≦50 は従来どおり
        Assert.Equal(132.0 + 0.36 * 20, PitcherAttributes.VelocityKmhFromLevel(70), 9);
        Assert.Equal(146.4, PitcherAttributes.VelocityKmhFromLevel(90), 9);                 // 上位の境（issue #908）
        Assert.Equal(146.4 + 1.16 * 5, PitcherAttributes.VelocityKmhFromLevel(95), 9);      // 境から上は 1.16 km/h/Lv
        Assert.Equal(158.0, PitcherAttributes.VelocityKmhFromLevel(100), 9);
        // 端数つきの逆変換も往復する（AI校の天井が端数の球速Lvを作る）
        foreach (var x in new[] { 12.3, 49.9, 50.0, 67.25, 90.0, 91.7, 99.4 })
            Assert.Equal(x, PitcherAttributes.LevelFromVelocityKmhExact(PitcherAttributes.VelocityKmhFromLevel(x)), 6);
        // 単調増加・境で連続
        for (var lv = 1; lv < 100; lv++)
            Assert.True(PitcherAttributes.VelocityKmhFromLevel(lv + 1) > PitcherAttributes.VelocityKmhFromLevel(lv));
        // 往復一致（1〜100 全て）
        for (var lv = 1; lv <= 100; lv++)
            Assert.Equal(lv, PitcherAttributes.LevelFromVelocityKmh(PitcherAttributes.VelocityKmhFromLevel(lv)));
    }
    [Fact]
    public void AiVelocityCap_IsIdentityBelowKnee_Monotone_AndNeverReaches100()
    {
        var c = new KokoSim.Engine.Season.RosterCoefficients();
        for (var x = 10.0; x <= c.AiVelocityCapKnee; x += 0.5)
            Assert.Equal(x, c.VelocitySoftCap(x), 9);                       // 膝以下は1ビットも変えない
        var prev = c.VelocitySoftCap(c.AiVelocityCapKnee);
        for (var x = c.AiVelocityCapKnee + 0.25; x <= 400; x += 0.25)
        {
            var y = c.VelocitySoftCap(x);
            Assert.True(y > prev, $"単調でない: raw {x}");
            Assert.True(y < 100.0, $"100 に届いた: raw {x}");              // 上限への張り付きが起きない
            Assert.Equal(x, c.VelocitySoftCapInverse(y), 6);                 // 逆関数（毎年の底上げに使う）
            prev = y;
        }
        // 折れ目は係数どおり（YAML ai_velocity_cap_*）
        Assert.Equal(c.AiVelocityCapMidLevel, c.VelocitySoftCap(c.AiVelocityCapMidRaw), 9);
        Assert.Equal(c.AiVelocityCapTopLevel, c.VelocitySoftCap(c.AiVelocityCapTopRaw), 9);
    }

    /// <summary>
    /// 全国の3年生の最速（issue #908）。現実の高校生は最速150km/h以上が年7〜18人（2021〜2026・ドラフト・レポート）・
    /// その年の1位が 153〜158km/h。旧実装は球速Lvを 99 で切り詰め、123〜155人が 149.6km/h に張り付いていた
    /// （150km/h 以上は 0人）。全国 seed 2026 を 3/5/7年目で実体化して数える。
    /// 実測（2026-11-14）: 150km/h以上 11/12/13人・1位 154.8/156.5/158.0km/h。
    /// </summary>
    [Fact]   // 全国4000校×3年ぶんの実体化だが並列で約2秒（fast に置く）
    public void ThirdYears_Over150Kmh_MatchRealHighSchoolCounts()
    {
        var nation = NationGenerator.Generate(new SchoolNameVocab(), new NationCoefficients(), new Xoshiro256Random(2026));
        var deps = new AiRosterDeps();
        foreach (var year in new[] { 3, 5, 7 })
        {
            var tops = new System.Collections.Concurrent.ConcurrentBag<double>();
            System.Threading.Tasks.Parallel.ForEach(nation.Schools, school =>
            {
                foreach (var p in AiRosterFactory.Bootstrap(school, year, deps).Players.Where(p => p.Grade == 3))
                    tops.Add(p.Snapshot.Pitching.MaxVelocityKmh);
            });
            var xs = tops.ToList();
            var over150 = xs.Count(x => Math.Round(x) >= 150);
            _out.WriteLine($"{year}年目: 3年生 {xs.Count}人 ／ 150km/h以上 {over150}人 ／ 145km/h以上 {xs.Count(x => Math.Round(x) >= 145)}人 ／ 最速 {xs.Max():F1}");
            Assert.InRange(over150, 7, 18);
            Assert.InRange(xs.Max(), 153.0, 158.0);
        }
    }
}
