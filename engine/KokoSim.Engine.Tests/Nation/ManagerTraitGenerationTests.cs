using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 監督傾向の付与（issue #55, 決定3）と抜擢型の編成（決定論）。生成が既存列を乱さないことは
/// 独立 Fork で保証され、既存の <see cref="NationTests"/>（強さ・校風の決定論）が非破壊であることで担保する。
/// ここでは付与の分布・制約と、抜擢型が「観測できる頻度で起きる／傾向なしでは起きない」ことを確認する。
/// </summary>
public sealed class ManagerTraitGenerationTests
{
    private static readonly SchoolNameVocab Vocab = new();
    private static readonly NationCoefficients Coeff = new();

    private static KokoSim.Engine.Nation.Nation Gen(ulong seed)
        => NationGenerator.Generate(Vocab, Coeff, new Xoshiro256Random(seed));

    [Fact]
    public void Traits_AreDeterministic_ForSameSeed()
    {
        var a = Gen(2026);
        var b = Gen(2026);
        Assert.Equal(a.Schools.Count, b.Schools.Count);
        for (var i = 0; i < a.Schools.Count; i++)
        {
            Assert.Equal(a.Schools[i].ManagerTraits, b.Schools[i].ManagerTraits);
        }
    }

    [Fact]
    public void Traits_RespectBounds_Distinct_NoContinuationConflict()
    {
        foreach (var s in Gen(2026).Schools)
        {
            Assert.True(s.ManagerTraits.Count <= 2, "1校あたり傾向は0〜2個");
            Assert.Equal(s.ManagerTraits.Count, s.ManagerTraits.Distinct().Count()); // 重複なし
            var bothContinuation = s.ManagerTraits.Contains(ManagerTrait.AceOveruse)
                                   && s.ManagerTraits.Contains(ManagerTrait.QuickHook);
            Assert.False(bothContinuation, "エース酷使と継投早めは同居しない");
        }
    }

    [Fact]
    public void Traits_Distribution_IsRoughly_55_35_10()
    {
        var schools = Gen(2026).Schools;
        var total = (double)schools.Count;
        var none = schools.Count(s => s.ManagerTraits.Count == 0) / total;
        var one = schools.Count(s => s.ManagerTraits.Count == 1) / total;
        var two = schools.Count(s => s.ManagerTraits.Count == 2) / total;
        Assert.InRange(none, 0.50, 0.60);   // 期待55%
        Assert.InRange(one, 0.30, 0.40);    // 期待35%
        Assert.InRange(two, 0.06, 0.14);    // 期待10%
    }

    private static School PromoterSchool(int id, double strength) => new()
    {
        Id = id, Name = $"抜擢校{id}", PrefectureId = 0, Strength = strength,
        ManagerTraits = new[] { ManagerTrait.Promoter },
    };

    private static School PlainSchool(int id, double strength) => new()
    {
        Id = id, Name = $"標準校{id}", PrefectureId = 0, Strength = strength,
    };

    // DH を検討させない（modernRules 未指定）＝控え（背番号10〜）が打順に入るのは抜擢型だけになる。
    [Fact]
    public void Promoter_PromotesBenchStarter_Observably_PlainNever()
    {
        var promoted = 0;
        var plainPromoted = 0;
        const int schools = 40;
        for (var id = 0; id < schools; id++)
        {
            var pro = StrengthTeamFactory.ForSchool(PromoterSchool(id, 62), yearIndex: 1);
            var plain = StrengthTeamFactory.ForSchool(PlainSchool(id, 62), yearIndex: 1);
            if (pro.BattingOrder.Any(p => p.UniformNumber >= 10)) promoted++;
            if (plain.BattingOrder.Any(p => p.UniformNumber >= 10)) plainPromoted++;
        }
        Assert.Equal(0, plainPromoted);   // 抜擢型でなければ控えは先発しない
        Assert.True(promoted > schools / 3, $"抜擢が観測できる頻度で起きるはず: {promoted}/{schools}");
    }

    [Fact]
    public void Promoter_IsDeterministic_PreviewEqualsLive()
    {
        var a = StrengthTeamFactory.ForSchool(PromoterSchool(7, 62), yearIndex: 1);
        var b = StrengthTeamFactory.ForSchool(PromoterSchool(7, 62), yearIndex: 1);
        Assert.Equal(a.BattingOrder.Select(p => p.UniformNumber), b.BattingOrder.Select(p => p.UniformNumber));
    }
}
