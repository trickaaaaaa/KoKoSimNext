using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 効き先④（issue #1601・オーナー決定2026-11-25）: チーム状態（結束）の倍率が練習の伸びに掛かる配線。
/// <see cref="Cohesion.TrainingMultiplier"/> は <c>CohesionTests</c> で純関数として確認済みなので、
/// ここは <see cref="DevelopmentModel.TrainWeekPlan"/> への配線（既定1.0＝従来一致・1.05で実際に伸びが増える）だけを見る。
/// </summary>
public sealed class CohesionTrainingWiringTests
{
    private const int Ref = 300;

    private static DevelopingPlayer NewPlayer()
    {
        var p = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(p, false);
        foreach (var k in AbilityKinds.All)
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }
        return p;
    }

    [Fact]
    public void DefaultCohesionMultiplier_MatchesLegacyExactly()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var alloc = new[] { new MenuAllocation(TrainingMenu.Batting, Ref) };

        var legacy = NewPlayer();
        var explicitOne = NewPlayer();
        for (var i = 0; i < 12; i++)
        {
            DevelopmentModel.TrainWeekPlan(legacy, alloc, Ref, 0, 1.0, stages, c);
            DevelopmentModel.TrainWeekPlan(explicitOne, alloc, Ref, 0, 1.0, stages, c,
                cohesionMultiplier: 1.0);
        }

        foreach (var k in AbilityKinds.All) Assert.Equal(legacy.Exp(k), explicitOne.Exp(k), 9);
    }

    [Fact]
    public void U85Multiplier_GrowsFasterThanU15_AndScalesLikeAllOtherMenus()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var alloc = new[] { new MenuAllocation(TrainingMenu.Batting, Ref) };
        var coeff = new CohesionCoefficients();

        var good = NewPlayer();   // U=85相当
        var bad = NewPlayer();    // U=15相当
        var goodMult = Cohesion.TrainingMultiplier(85, coeff);
        var badMult = Cohesion.TrainingMultiplier(15, coeff);
        Assert.Equal(1.05, goodMult, 9);

        for (var i = 0; i < 12; i++)
        {
            DevelopmentModel.TrainWeekPlan(good, alloc, Ref, 0, 1.0, stages, c, cohesionMultiplier: goodMult);
            DevelopmentModel.TrainWeekPlan(bad, alloc, Ref, 0, 1.0, stages, c, cohesionMultiplier: badMult);
        }

        Assert.True(TotalExp(good) > TotalExp(bad));

        // 道具・配分スケールと同じ位置（EquipmentFactor_IsEquivalentToScalingMinutes と同じ検証形）。
        var byMultiplier = NewPlayer();
        var byMinutes = NewPlayer();
        DevelopmentModel.TrainWeekPlan(byMultiplier, alloc, Ref, 0, 1.0, stages, c, cohesionMultiplier: 1.05);
        DevelopmentModel.TrainWeekPlan(byMinutes,
            new[] { new MenuAllocation(TrainingMenu.Batting, (int)(Ref * 1.05)) }, Ref, 0, 1.0, stages, c);
        foreach (var k in AbilityKinds.All) Assert.Equal(byMinutes.Exp(k), byMultiplier.Exp(k), 6);
    }

    private static double TotalExp(DevelopingPlayer p)
    {
        double sum = 0;
        foreach (var k in AbilityKinds.All) sum += p.Level(k) * 1000.0 + p.Exp(k);
        return sum;
    }
}
