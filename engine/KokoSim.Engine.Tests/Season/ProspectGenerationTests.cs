using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 新入生生成の作り直し（Slice 2B, 設計書01 §1.1b-c / 04 §2.1）の分布特性を検証。
/// 固定値ではなく「運動能力ベースラインの名声独立」「投打左右分布」「凸凹配分」「専門外フロア」
/// 「球速二層（地肩＋投手センス）」「決定論」を統計的に確認する。
/// </summary>
public sealed class ProspectGenerationTests
{
    private static readonly RosterCoefficients C = new();

    /// <summary>複数シードで新入生を大量生成して母集団を作る。</summary>
    private static List<DevelopingPlayer> Cohort(double talentCenter, int seeds = 250)
    {
        var all = new List<DevelopingPlayer>();
        for (var s = 0; s < seeds; s++)
            all.AddRange(ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)(1000 + s)), talentCenter: talentCenter));
        return all;
    }

    // --- 決定論 ---

    [Fact]
    public void Intake_IsDeterministic_SameSeedSameLevels()
    {
        var a = ProspectGenerator.Intake(1, C, new Xoshiro256Random(7));
        var b = ProspectGenerator.Intake(1, C, new Xoshiro256Random(7));
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Name, b[i].Name);
            foreach (var k in AbilityKinds.All)
                Assert.Equal(a[i].Level(k), b[i].Level(k));
            Assert.Equal(a[i].Throws, b[i].Throws);
            Assert.Equal(a[i].Bats, b[i].Bats);
        }
    }

    // --- 守備位置適性の初期分布（設計書01 §1.1） ---

    private static readonly FieldPosition[] AllPos =
    {
        FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    [Fact]
    public void Aptitude_IsDeterministic()
    {
        // 同シードで適性まで一致（独立Forkでも再現）。
        var a = ProspectGenerator.Intake(1, C, new Xoshiro256Random(7));
        var b = ProspectGenerator.Intake(1, C, new Xoshiro256Random(7));
        for (var i = 0; i < a.Count; i++)
            foreach (var pos in AllPos)
                Assert.Equal(a[i].Aptitude(pos), b[i].Aptitude(pos));
    }

    [Fact]
    public void Aptitude_ProfilesEmerge_NotPredetermined()
    {
        // 思想: 本職を先に決めず創発させる。母集団に「全ポジ器用」も「専門特化」も自然発生する。
        var pop = Cohort(C.TalentCenterDefault);

        // 全ポジ器用（最小適性すら高い万能型）が一定数いる。
        var allRound = pop.Count(p => AllPos.Min(p.Aptitude) >= 62);
        Assert.True(allRound > 0, "全ポジ器用な個体が生まれていない");

        // 専門特化（1ポジ突出・他は平凡＝凸凹が大きい）も一定数いる。
        var specialists = pop.Count(p => AllPos.Max(p.Aptitude) - AllPos.Min(p.Aptitude) >= 35);
        Assert.True(specialists > 0, "特化型の個体が生まれていない");

        // 平均適性は基準50付近（守備力補正が母集団で中立＝既存バランス非破壊）。
        var grandMean = pop.Average(p => AllPos.Average(p.Aptitude));
        Assert.InRange(grandMean, 45.0, 55.0);
    }

    [Fact]
    public void Aptitude_WithinGroupCoheres_MoreThanCrossGroup()
    {
        // 系統ごとの向き不向きにより、同系統ポジ同士は近く、別系統とは離れる（創発の副産物, 本職の事前指定ではない）。
        var pop = Cohort(C.TalentCenterDefault);
        double AbsDiff(DevelopingPlayer p, FieldPosition x, FieldPosition y) => Math.Abs(p.Aptitude(x) - p.Aptitude(y));

        // 同系統（内野）2ポジ vs 別系統（内野×外野）の平均差。
        var sameGroup = pop.Average(p => AbsDiff(p, FieldPosition.SecondBase, FieldPosition.Shortstop));
        var crossGroup = pop.Average(p => AbsDiff(p, FieldPosition.Shortstop, FieldPosition.CenterField));
        Assert.True(sameGroup < crossGroup, $"同系統({sameGroup:F1})が別系統({crossGroup:F1})より近くない");
    }

    // --- 投打の利き分布（設計書01 §1.1c） ---

    [Fact]
    public void Handedness_FollowsConditionalDistribution()
    {
        var pop = Cohort(C.TalentCenterDefault);
        var total = pop.Count;
        var leftThrow = pop.Count(p => p.Throws == Handedness.Left);
        var leftThrowRatio = (double)leftThrow / total;
        // 左投げ 約12%（広めの帯）。
        Assert.InRange(leftThrowRatio, 0.06, 0.19);

        // 右投げ → 左打ち 約40%。
        var rightThrowers = pop.Where(p => p.Throws == Handedness.Right).ToList();
        var rtLeftBat = rightThrowers.Count(p => p.Bats == Handedness.Left) / (double)rightThrowers.Count;
        Assert.InRange(rtLeftBat, 0.30, 0.50);

        // 左投げ右打ちは絶滅危惧種（左投げのうち右打ちはごく僅か）。
        var leftThrowers = pop.Where(p => p.Throws == Handedness.Left).ToList();
        var ltRightBat = leftThrowers.Count(p => p.Bats == Handedness.Right) / (double)leftThrowers.Count;
        Assert.True(ltRightBat < 0.10, $"左投右打が多すぎる: {ltRightBat:P1}");
    }

    // --- 運動能力ベースラインは名声（総合力）と独立（設計書01 §1.1b-2） ---

    [Fact]
    public void PhysicalAbilities_AreIndependentOfTalent()
    {
        var low = Cohort(20.0);
        var high = Cohort(70.0);

        double Mean(IEnumerable<DevelopingPlayer> pop, AbilityKind k) => pop.Average(p => p.Level(k));

        // 走力・地肩は総合力が大きく違っても平均がほぼ変わらない（弱小校の俊足・強肩が出る）。
        Assert.True(System.Math.Abs(Mean(low, AbilityKind.Speed) - Mean(high, AbilityKind.Speed)) < 5.0,
            "走力が名声に連動してしまっている");
        Assert.True(System.Math.Abs(Mean(low, AbilityKind.ArmStrength) - Mean(high, AbilityKind.ArmStrength)) < 5.0,
            "地肩が名声に連動してしまっている");

        // 一方、専門能力（ミート）は総合力に明確に連動する。
        Assert.True(Mean(high, AbilityKind.Contact) - Mean(low, AbilityKind.Contact) > 15.0,
            "専門能力が総合力に連動していない");
    }

    // --- パワプロ型特化の禁止（専門外にも下限, 設計書01 §1.1b） ---

    [Fact]
    public void NoCatastrophicSpecialization_OffAbilitiesHaveFloor()
    {
        var pop = Cohort(C.TalentCenterDefault);

        // 投手の打撃（ミート）も、野手の投球関連も壊滅ゼロにはならない。
        Assert.All(pop.Where(p => p.IsPitcher), p =>
            Assert.True(p.Level(AbilityKind.Contact) >= C.OffSpecialtyFloor,
                $"投手のミートがフロア割れ: {p.Level(AbilityKind.Contact)}"));
        Assert.All(pop.Where(p => !p.IsPitcher), p =>
            Assert.True(p.Level(AbilityKind.Control) >= C.OffSpecialtyFloor,
                $"野手のコントロールがフロア割れ: {p.Level(AbilityKind.Control)}"));
    }

    // --- 凸凹配分（金太郎飴の回避）: 選手内の専門能力にばらつきがある ---

    [Fact]
    public void SkillAllocation_IsUneven_NotUniform()
    {
        var pop = Cohort(C.TalentCenterDefault);
        var batters = pop.Where(p => !p.IsPitcher).ToList();

        AbilityKind[] skills =
        {
            AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline,
            AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.Bunt,
        };

        double InternalStd(DevelopingPlayer p)
        {
            var vals = skills.Select(k => (double)p.Level(k)).ToList();
            var m = vals.Average();
            return System.Math.Sqrt(vals.Average(v => (v - m) * (v - m)));
        }

        // 平均的な選手でも内訳に相応のばらつき（金太郎飴ではない）。
        var avgStd = batters.Average(InternalStd);
        Assert.True(avgStd > 4.0, $"内訳が均一すぎる（金太郎飴）: 平均内部σ={avgStd:F1}");
    }

    // --- 球速二層（地肩＋投手センス, 設計書01 §1.1b-3 / 02 §1.1b） ---

    [Fact]
    public void PitcherVelocity_ScalesWithTalent_TwoTier()
    {
        double VeloMean(double talent) =>
            Cohort(talent).Where(p => p.IsPitcher).Average(p => p.Level(AbilityKind.Velocity));

        // 強豪（高総合力）の投手球速levelは一般校より高い（投手センス連動）。
        Assert.True(VeloMean(70.0) > VeloMean(25.0) + 8.0, "球速が総合力で二層化していない");
    }

    // --- 投手経歴フラグ（設計書01 §1.1b）: 稀に存在、支配的でない ---

    [Fact]
    public void PitcherBackground_IsRare_ButPresent()
    {
        var batters = Cohort(C.TalentCenterDefault).Where(p => !p.IsPitcher).ToList();
        var ratio = batters.Count(p => p.HasPitcherBackground) / (double)batters.Count;
        Assert.InRange(ratio, 0.01, 0.15); // 稀少（数%）だがゼロではない
    }

    // --- 伸びしろは分野別にばらつく（現在値と独立） ---

    [Fact]
    public void GrowthMultipliers_Vary_WithinRange()
    {
        var pop = Cohort(C.TalentCenterDefault);
        Assert.All(pop, p =>
        {
            Assert.InRange(p.PitchingGrowth, C.GrowthMin, C.GrowthMax);
            Assert.InRange(p.BattingGrowth, C.GrowthMin, C.GrowthMax);
            Assert.InRange(p.DefenseGrowth, C.GrowthMin, C.GrowthMax);
        });
        // 分野間で差がある選手が多数（一律ではない）。
        var varied = pop.Count(p =>
            System.Math.Abs(p.PitchingGrowth - p.DefenseGrowth) > 0.1);
        Assert.True(varied > pop.Count / 2, "伸びしろが分野一律になっている");
    }

    // --- 捕手リード（設計書01 §2①: 天性＝野球脳相関。Q8で「未熟(中心46)→実戦で開花」へ引き下げ） ---

    [Fact]
    public void Lead_IsCenteredLow_AndCorrelatesWithMental()
    {
        var pop = Cohort(C.TalentCenterDefault);
        var leads = pop.Select(p => (double)p.Lead).ToList();
        var mentals = pop.Select(p => (double)p.Mental).ToList();

        // Q8（実戦成長ループ）: 新入生は未熟＝中心46−(50−MentalMean)×corr ≈ 44.8 近傍。実戦出場で開花する。
        Assert.InRange(leads.Average(), 43.0, 46.5);
        // 個体差が潰れていない（横並びの中にも良し悪しがある）。
        var sd = System.Math.Sqrt(leads.Select(v => (v - leads.Average()) * (v - leads.Average())).Average());
        Assert.InRange(sd, 5.0, 13.0);

        // 天性＝野球脳(Mental)との正の相関（高IQほどリードの素地が高い）。
        var mLead = leads.Average();
        var mMental = mentals.Average();
        var cov = pop.Sum(p => (p.Lead - mLead) * (p.Mental - mMental));
        var vLead = pop.Sum(p => (p.Lead - mLead) * (p.Lead - mLead));
        var vMental = pop.Sum(p => (p.Mental - mMental) * (p.Mental - mMental));
        var pearson = cov / System.Math.Sqrt(vLead * vMental);
        Assert.True(pearson > 0.2, $"リードが野球脳と相関していない (r={pearson:F2})");
    }

    [Fact]
    public void Lead_IsDeterministic()
    {
        var a = ProspectGenerator.Intake(1, C, new Xoshiro256Random(21));
        var b = ProspectGenerator.Intake(1, C, new Xoshiro256Random(21));
        for (var i = 0; i < a.Count; i++) Assert.Equal(a[i].Lead, b[i].Lead);
    }

    // --- 実戦成長の隠しcap（Q8・2026-07-20: 現在値＋gap＋Late上振れ、リードは野球脳相関） ---

    [Fact]
    public void MatchGrowthCaps_AreAboveCurrentValue_AndLateGetsHigherCeiling()
    {
        var pop = Cohort(C.TalentCenterDefault);
        foreach (var p in pop)
        {
            Assert.InRange(p.MentalCap, p.Mental + 2, 99);
            Assert.InRange(p.LeadCap, p.Lead + 2, 99);
        }

        // Late は cap 上振れ（LateCapBonus）: 現在値との平均ギャップが Standard/Early より大きい。
        double Gap(GrowthType g) => pop.Where(p => p.GrowthType == g).Average(p => (double)(p.LeadCap - p.Lead));
        Assert.True(Gap(GrowthType.Late) > Gap(GrowthType.Standard),
            $"Lateのリードcap上振れが無い (Late={Gap(GrowthType.Late):F1} Std={Gap(GrowthType.Standard):F1})");
    }
}
