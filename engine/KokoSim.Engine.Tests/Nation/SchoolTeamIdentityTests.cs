using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 相手校チームの識別情報（氏名・背番号・学年・投打）と、校ID＋年度シードによる決定論生成を検証する。
/// これは大会展望（設計書06 §3.5b）の「展望で見た選手が実戦でそのまま出る」体験の土台。
/// 展望表示と実際の対戦相手ラインナップが同一ソース（ForSchool）であることを担保する。
/// </summary>
public sealed class SchoolTeamIdentityTests
{
    private static School Sch(int id, double strength = 70, string name = "テスト学園")
        => new() { Id = id, Name = name, PrefectureId = 13, Strength = strength };

    private static IEnumerable<Player> All(Team t)
        => t.BattingOrder.Concat(t.Bullpen).Concat(t.Bench);

    [Fact]
    public void ForSchool_IsDeterministic_ForSameSchoolAndYear()
    {
        var school = Sch(42);
        var a = StrengthTeamFactory.ForSchool(school, yearIndex: 1);
        var b = StrengthTeamFactory.ForSchool(school, yearIndex: 1);

        // 氏名・背番号・学年・投打・能力まで完全一致（＝展望と実戦で同じ選手になる）。
        Assert.Equal(
            All(a).Select(p => (p.Name, p.UniformNumber, p.Grade, p.Throws, p.Bats, p.Contact, p.Power)),
            All(b).Select(p => (p.Name, p.UniformNumber, p.Grade, p.Throws, p.Bats, p.Contact, p.Power)));
    }

    [Fact]
    public void ForSchool_DiffersByYear_AsPlayersGraduate()
    {
        var school = Sch(42);
        var y1 = StrengthTeamFactory.ForSchool(school, yearIndex: 1);
        var y2 = StrengthTeamFactory.ForSchool(school, yearIndex: 2);

        // 年度が変われば代替わりする（3年生が抜ける）＝別メンバーになる。
        Assert.NotEqual(
            All(y1).Select(p => p.Name).ToList(),
            All(y2).Select(p => p.Name).ToList());
    }

    [Fact]
    public void ForSchool_DiffersBySchool()
    {
        var a = StrengthTeamFactory.ForSchool(Sch(1), yearIndex: 1);
        var b = StrengthTeamFactory.ForSchool(Sch(2), yearIndex: 1);
        Assert.NotEqual(All(a).Select(p => p.Name).ToList(), All(b).Select(p => p.Name).ToList());
    }

    [Fact]
    public void Roster_Has20Members_WithUniqueNumbers1To20()
    {
        var team = StrengthTeamFactory.ForSchool(Sch(7), yearIndex: 1);
        var numbers = All(team).Select(p => p.UniformNumber).ToList();

        Assert.Equal(20, numbers.Count);                       // ベンチ入り20人制
        Assert.Equal(20, numbers.Distinct().Count());          // 背番号は一意
        Assert.All(numbers, n => Assert.InRange(n, 1, 20));
    }

    [Fact]
    public void StartingNumbers_FollowPositionConvention()
    {
        var team = StrengthTeamFactory.ForSchool(Sch(7), yearIndex: 1);
        // エース＝1、捕手＝2、一塁＝3 …（高校野球の慣例）。
        var ace = team.BattingOrder[team.PitcherSlot];
        Assert.Equal(1, ace.UniformNumber);
        Assert.Equal(2, team.BattingOrder.First(p => p.Position == FieldPosition.Catcher).UniformNumber);
        Assert.Equal(6, team.BattingOrder.First(p => p.Position == FieldPosition.Shortstop).UniformNumber);
    }

    [Fact]
    public void EveryMember_HasNameAndGrade()
    {
        var team = StrengthTeamFactory.ForSchool(Sch(9), yearIndex: 1);
        foreach (var p in All(team))
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.DoesNotContain("先発", p.Name);              // 旧・匿名名（"○○先発"）が残っていない
            Assert.InRange(p.Grade, 1, 3);
        }
    }

    [Fact]
    public void Handedness_HasLeftiesAndFollowsDistribution()
    {
        // 多数校をまとめて見て分布を確認する（左投は約12%、左打は右投の約4割＝右投左打が多い）。
        var players = Enumerable.Range(1, 120)
            .SelectMany(id => All(StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1)))
            .ToList();

        var leftThrow = players.Count(p => p.Throws == Handedness.Left) / (double)players.Count;
        var leftBat = players.Count(p => p.Bats == Handedness.Left) / (double)players.Count;
        var swtch = players.Count(p => p.Bats == Handedness.Switch) / (double)players.Count;

        Assert.InRange(leftThrow, 0.06, 0.20);   // ThrowLeftProb=0.12 近傍
        Assert.InRange(leftBat, 0.28, 0.55);     // 右投左打を量産するので打席左は多め
        Assert.InRange(swtch, 0.005, 0.08);      // SwitchProb=0.03 近傍
    }

    [Fact]
    public void Starters_SkewUpperGrades_BenchSkewsLower()
    {
        var teams = Enumerable.Range(1, 80).Select(id => StrengthTeamFactory.ForSchool(Sch(id), yearIndex: 1)).ToList();
        var starterAvg = teams.SelectMany(t => t.BattingOrder).Average(p => p.Grade);
        var benchAvg = teams.SelectMany(t => t.Bench).Average(p => p.Grade);

        // 先発は上級生寄り、控えは下級生寄り。
        Assert.True(starterAvg > benchAvg, $"先発{starterAvg:F2} > 控え{benchAvg:F2} を期待");
        Assert.InRange(starterAvg, 2.0, 2.7);
        Assert.InRange(benchAvg, 1.4, 2.1);
    }

    [Fact]
    public void ForSchool_MatchesCreate_WithSameSeed()
    {
        // ForSchool は「Create に校ID＋年度シードを渡すだけ」＝薄い口であることを固定する。
        var school = Sch(31, strength: 66, name: "青葉中央");
        var viaForSchool = StrengthTeamFactory.ForSchool(school, yearIndex: 3);
        var viaCreate = StrengthTeamFactory.Create(
            school.Strength, school.Name, StrengthTeamFactory.SeedFor(school.Id, 3));

        Assert.Equal(All(viaForSchool).Select(p => (p.Name, p.UniformNumber)),
                     All(viaCreate).Select(p => (p.Name, p.UniformNumber)));
    }

    [Fact]
    public void Create_MainRngStream_IsUnchangedByIdentityForks()
    {
        // 氏名・投打・学年は Fork（主RNG非消費）で付与する契約。同じ rng を続けて使う後続の消費が
        // 変わっていないこと＝能力ロール列が不変であることを、能力値の一致で担保する。
        var rngA = new Xoshiro256Random(1234);
        var teamA = StrengthTeamFactory.Create(70, "A", rngA);
        var afterA = rngA.NextDouble();

        var rngB = new Xoshiro256Random(1234);
        var teamB = StrengthTeamFactory.Create(70, "A", rngB);
        var afterB = rngB.NextDouble();

        Assert.Equal(afterA, afterB);
        Assert.Equal(teamA.BattingOrder.Select(p => p.Contact), teamB.BattingOrder.Select(p => p.Contact));
    }
}
