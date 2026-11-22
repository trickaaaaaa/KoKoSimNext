using System;
using System.Linq;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>副効果の倍率をメニュー×能力ごとに持てること／守備練習→捕球だけ 0.7 であることの固定（issue #1409）。</summary>
public sealed class SubFactorOverrideTests
{
    private static DevelopingPlayer MakePlayer(params AbilityKind[] kinds)
    {
        var p = new DevelopingPlayer();
        foreach (var k in kinds)
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }
        return p;
    }

    [Fact]
    public void Defense_Catching_UsesOverride_OtherPairsUseSubFactor()
    {
        var c = new TrainingCoefficients();
        Assert.Equal(0.7, c.SubFactorFor(TrainingMenu.Defense, AbilityKind.Catching));
        Assert.Equal(0.4, c.SubFactor);

        // 守備練習→捕球以外の副効果は、実在するメニュー×副効果のすべてで sub_factor のまま。
        foreach (var menu in Enum.GetValues<TrainingMenu>())
            foreach (var sub in TrainingMenus.Effects(menu).Subs)
            {
                if (menu == TrainingMenu.Defense && sub == AbilityKind.Catching) continue;
                Assert.Equal(c.SubFactor, c.SubFactorFor(menu, sub));
            }
    }

    [Fact]
    public void Defense_GrowsCatching_At0_7_AndFieldingUnchanged()
    {
        var stages = new GrowthStageTable();
        var before = new TrainingCoefficients { SubFactorOverrides = Array.Empty<SubFactorOverride>() };
        var after = new TrainingCoefficients();

        var pBefore = MakePlayer(AbilityKind.Fielding, AbilityKind.Catching);
        var pAfter = MakePlayer(AbilityKind.Fielding, AbilityKind.Catching);
        DevelopmentModel.TrainWeek(pBefore, TrainingMenu.Defense, 0, 1.0, stages, before);
        DevelopmentModel.TrainWeek(pAfter, TrainingMenu.Defense, 0, 1.0, stages, after);

        Assert.Equal(pBefore.Exp(AbilityKind.Fielding), pAfter.Exp(AbilityKind.Fielding));
        Assert.Equal(0.7 / 0.4, pAfter.Exp(AbilityKind.Catching) / pBefore.Exp(AbilityKind.Catching), 9);
    }

    [Fact]
    public void OtherMenus_SubEffects_AreUnchangedByOverrideTable()
    {
        var stages = new GrowthStageTable();
        var none = new TrainingCoefficients { SubFactorOverrides = Array.Empty<SubFactorOverride>() };
        var def = new TrainingCoefficients();
        var all = Enum.GetValues<AbilityKind>();

        foreach (var menu in Enum.GetValues<TrainingMenu>().Where(m => m != TrainingMenu.Defense))
        {
            var a = MakePlayer(all);
            var b = MakePlayer(all);
            DevelopmentModel.TrainWeek(a, menu, 0, 1.0, stages, none);
            DevelopmentModel.TrainWeek(b, menu, 0, 1.0, stages, def);
            foreach (var k in all) Assert.Equal(a.Exp(k), b.Exp(k));
        }
    }

    [Fact]
    public void Defense_OneYear_MeasuredGrowth_FieldingSameCatchingAbout1_75x()
    {
        var stages = new GrowthStageTable();
        var before = new TrainingCoefficients { SubFactorOverrides = Array.Empty<SubFactorOverride>() };
        var after = new TrainingCoefficients();

        (int field, int catching) Run(TrainingCoefficients c)
        {
            var p = MakePlayer(AbilityKind.Fielding, AbilityKind.Catching);
            for (var w = 0; w < 52; w++) DevelopmentModel.TrainWeek(p, TrainingMenu.Defense, 1, 1.0, stages, c);
            return (p.Level(AbilityKind.Fielding), p.Level(AbilityKind.Catching));
        }

        var (fB, cB) = Run(before);
        var (fA, cA) = Run(after);
        Console.WriteLine($"1年専念(守備練習): 守備 {fB - 30:+0}→{fA - 30:+0} / 捕球 {cB - 30:+0}→{cA - 30:+0}");
        Assert.Equal(fB, fA);
        Assert.True(cA > cB);
    }
}
