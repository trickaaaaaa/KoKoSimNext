using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 左のプレミア（設計書01 §1.1c・issue #1475）。強豪・投手ほど左投げが多く、俊足・巧打ほど右投げ左打ちが多い。
/// 投手は創発（<see cref="PitcherRoleResolver"/>）で読む＝「投手」属性は無い。
/// ⚠ 統計帯はロスター生成を測らない＝このクラスが生成の安全網。
/// </summary>
public sealed class HandednessPremiumTests
{
    private static readonly RosterCoefficients C = new();

    [Fact]
    public void LeftThrowProbability_RisesWithPitcherLeanAndSchoolStrength()
    {
        // 野手（投手らしさ0）は学校の強さに依らず素の確率。
        Assert.Equal(C.ThrowLeftProb, HandednessModel.LeftThrowProbability(0.0, 20, C), 12);
        Assert.Equal(C.ThrowLeftProb, HandednessModel.LeftThrowProbability(0.0, 75, C), 12);
        // 投手らしさが高いほど・強いほど上がる。
        Assert.True(HandednessModel.LeftThrowProbability(1.0, 45, C) > HandednessModel.LeftThrowProbability(0.5, 45, C));
        Assert.True(HandednessModel.LeftThrowProbability(1.0, 70, C) > HandednessModel.LeftThrowProbability(1.0, 30, C));
        // 上限で止まる・素の確率を割らない。
        Assert.True(HandednessModel.LeftThrowProbability(1.0, 500, C) <= C.LeftThrowMax);
        Assert.True(HandednessModel.LeftThrowProbability(1.0, -500, C) >= C.ThrowLeftProb);
    }

    [Fact]
    public void LeftBat_RisesForSpeedAndContactTypes()
    {
        var slugger = HandednessModel.BatterTypeScore(contact: 40, power: 70, speed: 40, armStrength: 65);
        var spray = HandednessModel.BatterTypeScore(contact: 70, power: 40, speed: 70, armStrength: 45);
        Assert.True(slugger < 0 && spray > 0);
        Assert.True(HandednessModel.LeftBatGivenRightThrowProbability(spray, C)
                    > HandednessModel.LeftBatGivenRightThrowProbability(slugger, C));
        Assert.Equal(C.BatLeftGivenRightThrow, HandednessModel.LeftBatGivenRightThrowProbability(0, C), 12);
    }

    /// <summary>自校の新入生: 投手役の左投げ率が野手より高く、強い学校ほど高い。右投げの左打ちは俊足巧打型ほど多い。</summary>
    [Fact]
    public void Intake_PitchersAndStrongSchoolsLeanLeft()
    {
        double PitcherLeftRate(double center, out double fielderRate)
        {
            int p = 0, pl = 0, f = 0, fl = 0;
            for (var s = 0; s < 600; s++)
            {
                foreach (var x in ProspectGenerator.Intake(1, C, new Xoshiro256Random(0x1475_0000UL + (ulong)s), talentCenter: center))
                {
                    var left = x.Throws == Handedness.Left;
                    if (PitcherRoleResolver.LeansPitcher(x, C)) { p++; if (left) pl++; }
                    else { f++; if (left) fl++; }
                }
            }
            fielderRate = (double)fl / f;
            return (double)pl / p;
        }

        var weak = PitcherLeftRate(25, out _);
        var strong = PitcherLeftRate(70, out var strongFielders);
        Assert.True(strong > weak + 0.08, $"強豪 {strong:P1} と弱小 {weak:P1} の投手の左投げ率の差が小さい");
        Assert.True(strong > strongFielders + 0.08, $"投手 {strong:P1} と野手 {strongFielders:P1} の差が小さい");
    }

    [Fact]
    public void Intake_SpeedContactTypesBatLeftMoreOften()
    {
        int sn = 0, sl = 0, pn = 0, pl = 0;
        for (var s = 0; s < 600; s++)
        {
            foreach (var x in ProspectGenerator.Intake(1, C, new Xoshiro256Random(0x14B5_0000UL + (ulong)s)))
            {
                if (x.Throws != Handedness.Right || x.Bats == Handedness.Switch) continue;
                var t = HandednessModel.BatterTypeScore(x.Level(AbilityKind.Contact), x.Level(AbilityKind.Power),
                    x.Level(AbilityKind.Speed), x.Level(AbilityKind.ArmStrength));
                if (t > 8) { sn++; if (x.Bats == Handedness.Left) sl++; }
                else if (t < -8) { pn++; if (x.Bats == Handedness.Left) pl++; }
            }
        }
        Assert.True((double)sl / sn > (double)pl / pn + 0.15, $"俊足巧打 {(double)sl / sn:P1} / 長打 {(double)pl / pn:P1}");
    }

    /// <summary>
    /// 決定論（不変条件#2）: 利きの係数を変えても、利き以外（能力・適性・名前・スキル）は1ビットも動かない
    /// ＝乱数は旧実装と同じ位置・回数で引いて、判定だけ後で下している。
    /// </summary>
    [Fact]
    public void HandednessCoefficients_DoNotDisturbOtherRolls()
    {
        var flat = C with { LeftThrowPitcherBonus = 0, LeftThrowStrengthSlope = 0, BatLeftTypeSlope = 0 };
        var a = ProspectGenerator.Intake(3, C, new Xoshiro256Random(77), talentCenter: 60);
        var b = ProspectGenerator.Intake(3, flat, new Xoshiro256Random(77), talentCenter: 60);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Name, b[i].Name);
            foreach (var k in AbilityKinds.All) Assert.Equal(a[i].Level(k), b[i].Level(k));
            Assert.Equal(a[i].Skills.Visible.ToArray(), b[i].Skills.Visible.ToArray());
        }

        var deps = new AiRosterDeps();
        var flatDeps = new AiRosterDeps { Roster = flat };
        var school = new School { Id = 9, Name = "架空", PrefectureId = 13, Strength = 65, Fame = 50 };
        int n1 = 1, n2 = 1;
        var ca = AiRosterFactory.NewcomerCohort(school, 4, ref n1, deps);
        var cb = AiRosterFactory.NewcomerCohort(school, 4, ref n2, flatDeps);
        for (var i = 0; i < ca.Count; i++)
        {
            var x = ca[i].Snapshot;
            var y = cb[i].Snapshot;
            Assert.Equal(x.Name, y.Name);
            Assert.Equal((x.Contact, x.Power, x.Speed, x.ArmStrength, x.Fielding, x.Grade),
                (y.Contact, y.Power, y.Speed, y.ArmStrength, y.Fielding, y.Grade));
            Assert.Equal(x.Pitching.MaxVelocityKmh, y.Pitching.MaxVelocityKmh);
            Assert.Equal(x.Pitching.Control, y.Pitching.Control);
        }
    }
}
