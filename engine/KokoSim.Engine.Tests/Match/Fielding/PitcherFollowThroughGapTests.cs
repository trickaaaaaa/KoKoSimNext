using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 二遊間を抜けるセンター前ヒットがほとんど出ない件（issue #1789）。投手が「投げ終わりで体勢が崩れている」
/// 追加の反応遅延（<see cref="FieldingCoefficients.PitcherFieldingReactionPenaltySeconds"/>）を、処理野手の
/// 判定（<c>gloveTime</c>）には払っていたのに、内野を抜けるかの判定（<see cref="FieldingResolver.InfielderCanCutOff"/>）
/// には払っていなかった＝投手だけが内野手と同じ守備範囲を遅れなしで持っていた。
///
/// <para>投手しか居ない内野（他の野手を除いた経路）で、マウンドのすぐ脇を抜けて二塁方向へ転がる打球を作り、
/// 投手の守備範囲（半径4.2m）には収まるが追加の反応遅延ぶん間に合わない、という境界の当たりを固定する。
/// 修正前はこの当たりを投手が「捕れる」と誤判定していた（<see cref="FieldingCoefficients.PitcherFieldingReactionPenaltySeconds"/>
/// を0にすると実際に捕れる判定へ反転することも併せて固定し、この境界が確かに追加反応遅延で決まっていることを示す）。</para>
/// </summary>
public sealed class PitcherFollowThroughGapTests
{
    private static Fielder Pitcher() =>
        new(FieldPosition.Pitcher, new Vector3D(0, 0, 18.44), new FielderAttributes { Fielding = 50 });

    /// <summary>
    /// マウンドの右脇（x≈1.5〜1.8m）を通って二塁方向へ強く転がる経路。投手の守備位置からの距離は
    /// t=0.25〜0.48s の間だけ守備範囲（4.2m）に収まるが、追加反応遅延（0.35s）を払うと
    /// その窓（最大0.48s）に収まらない＝捕球不可（境界の当たり）。
    /// </summary>
    private static BouncePath PastTheMoundPath(FieldingCoefficients coeff)
    {
        var landing = new Vector3D(1.5, 0, 15.0);
        var landingVelocity = new Vector3D(0.3, -3.0, 34.0);
        return BouncePath.Compute(landing, landingVelocity, 0.25, coeff, IrregularBounce.None);
    }

    [Fact]
    public void InfielderCanCutOff_PitcherAlone_FalseWithFollowThroughPenalty()
    {
        var coeff = new FieldingCoefficients();
        var path = PastTheMoundPath(coeff);
        var fielders = new[] { Pitcher() };

        // 投手しか居ない＝抜けるかどうかは投手の守備範囲＋追加反応遅延だけで決まる。
        Assert.False(FieldingResolver.InfielderCanCutOff(fielders, path, coeff));
    }

    [Fact]
    public void InfielderCanCutOff_PitcherAlone_TrueWithoutFollowThroughPenalty()
    {
        // 追加反応遅延を0にすると、同じ境界の当たりが「捕れる」へ反転する＝上のFalseが
        // 守備範囲そのものでなく追加反応遅延で決まっていることの確認（回帰: この項を抜くと常にTrueへ戻る）。
        var coeff = new FieldingCoefficients { PitcherFieldingReactionPenaltySeconds = 0.0 };
        var path = PastTheMoundPath(coeff);
        var fielders = new[] { Pitcher() };

        Assert.True(FieldingResolver.InfielderCanCutOff(fielders, path, coeff));
    }
}
