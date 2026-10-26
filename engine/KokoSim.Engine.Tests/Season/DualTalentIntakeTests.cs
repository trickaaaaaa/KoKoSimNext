using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Season;

/// <summary>#684: 新入生の投打両立素質。二刀流（投打とも高い）が分布の裾に出て、それ以外の分布は動かないこと。</summary>
public sealed class DualTalentIntakeTests
{
    private readonly ITestOutputHelper _out;
    public DualTalentIntakeTests(ITestOutputHelper o) => _out = o;

    private static List<DevelopingPlayer> Cohort(RosterCoefficients c, double center, int seeds)
    {
        var all = new List<DevelopingPlayer>();
        for (var s = 0; s < seeds; s++)
            all.AddRange(ProspectGenerator.Intake(1, c, new Xoshiro256Random((ulong)(5000 + s)), talentCenter: center));
        return all;
    }

    private static double Bat(DevelopingPlayer p) =>
        (p.Level(AbilityKind.Contact) + p.Level(AbilityKind.Power) + p.Level(AbilityKind.Discipline)) / 3.0;

    private static double Pit(DevelopingPlayer p) =>
        (p.Level(AbilityKind.Velocity) + p.Level(AbilityKind.Control) + p.Level(AbilityKind.PitchRank)
         + p.Level(AbilityKind.Stamina)) / 4.0;

    private double BothTopShare(RosterCoefficients c, double center)
    {
        var cohort = Cohort(c, center, 1500);
        var bt = cohort.Select(Bat).OrderBy(x => x).ToArray()[(int)(0.9 * cohort.Count)];
        var pt = cohort.Select(Pit).OrderBy(x => x).ToArray()[(int)(0.9 * cohort.Count)];
        var share = (double)cohort.Count(p => Bat(p) >= bt && Pit(p) >= pt) / cohort.Count;
        _out.WriteLine($"center={center} slope={c.DualTalentRatioSlope}: 投打とも上位10% = {100 * share:F3}% (独立なら1%)");
        return share;
    }

    [Theory]
    [InlineData(32.0)]
    [InlineData(50.0)]
    public void DualTalent_RaisesTwoWayShare_AtLeastDoubleVsOff(double center)
    {
        var off = BothTopShare(new RosterCoefficients { DualTalentRatioSlope = 0.0 }, center);
        var on = BothTopShare(new RosterCoefficients(), center);
        Assert.True(on >= off * 1.8, $"on={on} off={off}");
    }

    [Fact]
    public void DualTalent_DoesNotMoveCohortMeansMuch()
    {
        var off = Cohort(new RosterCoefficients { DualTalentRatioSlope = 0.0 }, 40, 800);
        var on = Cohort(new RosterCoefficients(), 40, 800);
        Assert.InRange(on.Average(Bat) - off.Average(Bat), -0.1, 1.5);
        Assert.InRange(on.Average(Pit) - off.Average(Pit), -0.1, 1.5);
    }

    [Fact]
    public void DualTalent_ZeroSlope_IgnoresOtherKnobs()
    {
        // slope=0 なら専門外係数は常に 0.55＝ゲートに関わらず同じ。乱数は専用Forkなので主列は不変。
        var a = ProspectGenerator.Intake(1, new RosterCoefficients { DualTalentRatioSlope = 0.0 }, new Xoshiro256Random(9));
        var b = ProspectGenerator.Intake(1, new RosterCoefficients { DualTalentRatioSlope = 0.0, DualTalentGateZ = 3.0 }, new Xoshiro256Random(9));
        for (var i = 0; i < a.Count; i++)
            foreach (var k in AbilityKinds.All)
                Assert.Equal(a[i].Level(k), b[i].Level(k));
    }
}
