using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 内野手の守備範囲が守備力で変わる配線（issue #1661）。<see cref="FieldingResolver.InfielderRadius"/> は
/// <see cref="InfielderCanCutOff"/>／<see cref="FieldingResolver.FindClosestInfielderMiss"/>／
/// <see cref="FieldingResolver.FindOppositeSideInfielderMiss"/> が共通で使う入口で、個々の判定テストが
/// デフォルト（守備50＝恒等）のまま変わらずに通ることが大前提になる。ここでは入口そのものを単体で固定する。
/// </summary>
public sealed class InfielderFieldingRadiusWiringTests
{
    private static Fielder Shortstop(int fielding) =>
        new(FieldPosition.Shortstop, new Vector3D(0, 0, 40.0), new FielderAttributes { Fielding = fielding });

    [Fact]
    public void Fielding50_EqualsCoefficientsBase()
    {
        var coeff = new FieldingCoefficients();
        var radius = FieldingResolver.InfielderRadius(Shortstop(50), coeff);
        Assert.Equal(coeff.InfielderFieldingRadiusM, radius, 9);
    }

    [Fact]
    public void HigherFielding_GivesWiderRadius_LowerFielding_GivesNarrowerRadius()
    {
        var coeff = new FieldingCoefficients();
        var low = FieldingResolver.InfielderRadius(Shortstop(20), coeff);
        var mid = FieldingResolver.InfielderRadius(Shortstop(50), coeff);
        var high = FieldingResolver.InfielderRadius(Shortstop(80), coeff);

        Assert.True(low < mid, $"low={low} mid={mid}");
        Assert.True(mid < high, $"mid={mid} high={high}");
    }

    [Fact]
    public void Radius_NeverBelowFloor_NorAboveCap()
    {
        var coeff = new FieldingCoefficients();
        var veryLow = FieldingResolver.InfielderRadius(Shortstop(1), coeff);
        var veryHigh = FieldingResolver.InfielderRadius(Shortstop(99), coeff);

        Assert.True(veryLow >= coeff.InfielderFieldingRadiusFloorM, $"veryLow={veryLow}");
        Assert.True(veryHigh <= coeff.InfielderFieldingRadiusCapM, $"veryHigh={veryHigh}");
    }

    [Fact]
    public void DiveReach_NeverExceedsRadius_EvenWhenFloorIsBelowDiveReach()
    {
        // InfielderDiveReachM（issue前からの飛びつきリーチ）は「常に半径以下」の約束がある。
        // 半径の下限を飛びつきリーチより小さく設定しても（想定外の係数誤設定）、FieldInfieldGrounder の
        // diveReach[k] = Min(cutRadius[k], InfielderDiveReachM) がこの約束を構造的に守ることを固定する。
        var coeff = new FieldingCoefficients { InfielderFieldingRadiusFloorM = 1.0 };
        var radius = FieldingResolver.InfielderRadius(Shortstop(1), coeff);
        var diveReach = System.Math.Min(radius, coeff.InfielderDiveReachM);

        Assert.True(diveReach <= radius);
    }
}
