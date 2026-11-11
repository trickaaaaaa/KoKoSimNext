using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 開始時ロスターの上級生に既習の練習を済ませる（issue #865）。入学時の才能中心を下げたので、これが無いと
/// 2・3年生が1年生と同じ弱さで始まり、開始直後の自校が格上に一方的にやられる（#578 への逆戻り）。
/// </summary>
public sealed class StartingRosterTests
{
    private static DevelopingPlayer[] Cohort(ulong seed, int grade)
    {
        var players = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(seed)).ToArray();
        foreach (var p in players) p.Grade = grade;
        return players;
    }

    private static double SkillMean(DevelopingPlayer[] ps)
        => ps.Average(p => (p.Level(AbilityKind.Contact) + p.Level(AbilityKind.Power)
                            + p.Level(AbilityKind.Fielding) + p.Level(AbilityKind.Catching)) / 4.0);

    [Fact]
    public void FirstYears_AreUntouched()
    {
        var a = Cohort(11, 1);
        var b = Cohort(11, 1);
        StartingRoster.PreTrain(b);
        foreach (var k in System.Enum.GetValues<AbilityKind>())
            Assert.Equal(a.Select(p => p.Level(k)), b.Select(p => p.Level(k)));
    }

    [Fact]
    public void UpperClassmen_GrowByYearsAlreadySpent_AndKeepTheirGrade()
    {
        var fresh = Cohort(12, 1);
        var second = Cohort(12, 2);
        var third = Cohort(12, 3);
        StartingRoster.PreTrain(second);
        StartingRoster.PreTrain(third);

        Assert.All(second, p => Assert.Equal(2, p.Grade));
        Assert.All(third, p => Assert.Equal(3, p.Grade));
        // 同じ素材から: 1年分＞0、2年分＞1年分。伸びは才能上限で頭打ちになるので下回ることはない。
        Assert.True(SkillMean(second) > SkillMean(fresh) + 4.0, $"2年生 {SkillMean(second):F1} vs 入学時 {SkillMean(fresh):F1}");
        Assert.True(SkillMean(third) > SkillMean(second) + 4.0, $"3年生 {SkillMean(third):F1} vs 2年生 {SkillMean(second):F1}");
        for (var i = 0; i < third.Length; i++)
            foreach (var k in System.Enum.GetValues<AbilityKind>())
                Assert.True(third[i].Level(k) <= third[i].Cap(k), $"{k} が上限を超えた");
    }

    [Fact]
    public void PreTrain_IsDeterministic_AndDrawsNoRandomNumbers()
    {
        // 生成の乱数列に触れない＝PreTrain を挟んでも、同じ rng から続けて作る次の学年は同じ選手になる。
        var rngA = new Xoshiro256Random(77);
        var rngB = new Xoshiro256Random(77);
        var c = new RosterCoefficients();
        var a1 = ProspectGenerator.Intake(1, c, rngA).ToList();
        var b1 = ProspectGenerator.Intake(1, c, rngB).ToList();
        foreach (var p in b1) p.Grade = 3;
        StartingRoster.PreTrain(b1);
        var a2 = ProspectGenerator.Intake(2, c, rngA).Select(p => p.Name);
        var b2 = ProspectGenerator.Intake(2, c, rngB).Select(p => p.Name);
        Assert.Equal(a2, b2);

        var x = Cohort(13, 3);
        var y = Cohort(13, 3);
        StartingRoster.PreTrain(x);
        StartingRoster.PreTrain(y);
        foreach (var k in System.Enum.GetValues<AbilityKind>())
            Assert.Equal(x.Select(p => p.Level(k)), y.Select(p => p.Level(k)));
        Assert.NotNull(a1);
    }
}
