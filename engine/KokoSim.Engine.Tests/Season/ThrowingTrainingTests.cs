using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>遠投・送球（Throwing）の副効果に送球（ThrowAccuracy）が入ることの検証（issue #1167）。</summary>
public sealed class ThrowingTrainingTests
{
    private static DevelopingPlayer MakePlayer()
    {
        var p = new DevelopingPlayer();
        foreach (var k in new[] { AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.ThrowAccuracy })
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }
        return p;
    }

    [Fact]
    public void Throwing_SubEffects_IncludeFieldingAndThrowAccuracy()
    {
        var e = TrainingMenus.Effects(TrainingMenu.Throwing);
        Assert.Equal(AbilityKind.ArmStrength, e.Main);
        Assert.Contains(AbilityKind.Fielding, e.Subs);
        Assert.Contains(AbilityKind.ThrowAccuracy, e.Subs);
    }

    [Fact]
    public void Throwing_GrowsThrowAccuracy_AtSubFactorTimesTrainability()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = MakePlayer();

        DevelopmentModel.TrainWeek(p, TrainingMenu.Throwing, 0, 1.0, stages, c);

        var arm = p.Exp(AbilityKind.ArmStrength);
        var acc = p.Exp(AbilityKind.ThrowAccuracy);
        Assert.True(acc > 0, "遠投で送球が伸びていない");
        var expectedRatio = c.SubFactor * c.Trainability.ThrowAccuracy / c.Trainability.ArmStrength;
        Assert.Equal(expectedRatio, acc / arm, 6);
    }

    [Fact]
    public void Throwing_DoesNotGrowThrowAccuracy_PastCap()
    {
        var c = new TrainingCoefficients();
        var p = MakePlayer();
        p.SetLevel(AbilityKind.ThrowAccuracy, 60);
        p.SetCap(AbilityKind.ThrowAccuracy, 60);

        DevelopmentModel.TrainWeek(p, TrainingMenu.Throwing, 0, 1.0, new GrowthStageTable(), c);

        Assert.Equal(60, p.Level(AbilityKind.ThrowAccuracy));
    }
}
