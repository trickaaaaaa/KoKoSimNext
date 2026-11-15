using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 入学時の下げ（issue #865・<see cref="RosterCoefficients.IntakeDrop"/>）。練習で伸びる能力だけを下げ、
/// お任せで伸びない能力は下げない（下げると卒業まで戻らず、定常のチーム総合だけが落ちる）。
/// </summary>
public sealed class IntakeDropTests
{
    private static readonly RosterCoefficients C = new();

    private static readonly AbilityKind[] NotGrownByDefault =
    {
        AbilityKind.Steal, AbilityKind.ThrowAccuracy, AbilityKind.Baserunning, AbilityKind.Bunt,
        AbilityKind.Velocity, AbilityKind.PitchRank, AbilityKind.Stuff,
    };

    private static readonly AbilityKind[] GrownByDefault =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline,
        AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.Control,
    };

    [Fact]
    public void DefaultPlan_GrowsExactlyTheExpectedAbilities()
    {
        var grown = TrainingPresets.AbilitiesGrownBy(TrainingPlan.Default.Preset);
        Assert.All(GrownByDefault, k => Assert.Contains(k, grown));
        Assert.All(NotGrownByDefault, k => Assert.DoesNotContain(k, grown));
    }

    [Fact]
    public void DropFor_IsFullAtDefault_ZeroAtFameCeiling_LinearBetween()
    {
        // issue #1031: 名声→talentCenter写像を再校正した後も、下げ幅の物差し（下端Default・上端Cap）は
        // そのまま＝参照先を FameTalentCenterSpan（写像の頭打ち幅）から FameTalentCenterCap（上端そのもの）へ。
        var mid = (C.TalentCenterDefault + C.FameTalentCenterCap) / 2;
        Assert.Equal(C.IntakeDrop, C.IntakeDropFor(C.TalentCenterDefault), 9);
        Assert.Equal(C.IntakeDrop, C.IntakeDropFor(C.TalentCenterDefault - 5), 9);
        Assert.Equal(0.0, C.IntakeDropFor(C.FameTalentCenterCap), 9);
        Assert.Equal(0.0, C.IntakeDropFor(C.FameTalentCenterCap + 10), 9);
        Assert.Equal(C.IntakeDrop / 2, C.IntakeDropFor(mid), 9);
    }

    [Fact]
    public void Drop_LowersOnlyAbilitiesTheDefaultPlanGrows()
    {
        var noDrop = C with { IntakeDrop = 0.0 };
        var with = Enumerable.Range(0, 200)
            .SelectMany(s => ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)(86500 + s)))).ToList();
        var without = Enumerable.Range(0, 200)
            .SelectMany(s => ProspectGenerator.Intake(1, noDrop, new Xoshiro256Random((ulong)(86500 + s)))).ToList();
        Assert.Equal(without.Count, with.Count);   // 乱数消費列は不変（人数・氏名が揃う）
        Assert.Equal(without.Select(p => p.Name), with.Select(p => p.Name));

        foreach (var k in NotGrownByDefault)
        {
            var diff = with.Zip(without, (a, b) => a.Level(k) - b.Level(k)).Average();
            // 下げない。差は総合力の下限クランプ（TalentMin）と丸めの分だけ（実測 最大+0.36）。
            Assert.True(diff > -0.5 && diff < 0.5, $"{k} が動いた（平均差 {diff:F2}）");
        }
        foreach (var k in GrownByDefault)
        {
            var diff = with.Zip(without, (a, b) => a.Level(k) - b.Level(k)).Average();
            Assert.True(diff < -5.0, $"{k} が下がっていない（平均差 {diff:F2}）");
        }
        // 身体系（名声中心から作る）は下げの対象外。
        foreach (var k in new[] { AbilityKind.Speed, AbilityKind.ArmStrength })
            Assert.Equal(without.Select(p => p.Level(k)), with.Select(p => p.Level(k)));
    }
}
