using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>走塁練習（BaseRunning）の副効果に盗塁（Steal）が入ることの検証（issue #1168）。</summary>
public sealed class BaseRunningTrainingTests
{
    private static DevelopingPlayer MakePlayer()
    {
        var p = new DevelopingPlayer();
        foreach (var k in new[] { AbilityKind.Speed, AbilityKind.Steal })
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }
        return p;
    }

    [Fact]
    public void BaseRunning_MainIsSpeed_SubIsSteal()
    {
        var e = TrainingMenus.Effects(TrainingMenu.BaseRunning);
        Assert.Equal(AbilityKind.Speed, e.Main);
        Assert.Contains(AbilityKind.Steal, e.Subs);
    }

    [Fact]
    public void BaseRunning_GrowsSteal_AtSubFactorTimesTrainability()
    {
        var c = new TrainingCoefficients();
        var p = MakePlayer();

        DevelopmentModel.TrainWeek(p, TrainingMenu.BaseRunning, 0, 1.0, new GrowthStageTable(), c);

        var speed = p.Exp(AbilityKind.Speed);
        var steal = p.Exp(AbilityKind.Steal);
        Assert.True(steal > 0, "走塁練習で盗塁が伸びていない");
        var expectedRatio = c.SubFactor * c.Trainability.Steal / c.Trainability.Speed;
        Assert.Equal(expectedRatio, steal / speed, 6);
    }

    [Fact]
    public void BaseRunning_DoesNotGrowSteal_PastCap()
    {
        var c = new TrainingCoefficients();
        var p = MakePlayer();
        p.SetLevel(AbilityKind.Steal, 60);
        p.SetCap(AbilityKind.Steal, 60);

        DevelopmentModel.TrainWeek(p, TrainingMenu.BaseRunning, 0, 1.0, new GrowthStageTable(), c);

        Assert.Equal(60, p.Level(AbilityKind.Steal));
    }
}
