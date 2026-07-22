using System;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 監督傾向→係数変換（issue #55, <see cref="ManagerTraitEffects"/>）の単体テスト。9傾向それぞれが
/// 意図した係数を動かすこと、傾向なしが恒等（＝帯不変の土台）であること、掛け合わせが乗算合成される
/// ことを検証する。ゲーム全体での観測差（采配活動量）は <see cref="Nation.ManagerTraitInjectionTests"/>。
/// </summary>
public sealed class ManagerTraitEffectsTests
{
    private static readonly EnemyAiCoefficients Ai = new();

    [Fact]
    public void NoTraits_IsIdentity_ForTacticsAndFatigue()
    {
        var baseC = new TacticsCoefficients();
        Assert.Equal(baseC, ManagerTraitEffects.ApplyTactics(baseC, Array.Empty<ManagerTrait>(), Ai));
        Assert.Equal(baseC, ManagerTraitEffects.ApplyTactics(baseC, null, Ai));
        Assert.Null(ManagerTraitEffects.FatigueOverride(new FatigueCoefficients(), Array.Empty<ManagerTrait>(), Ai));
        Assert.Null(ManagerTraitEffects.FatigueOverride(new FatigueCoefficients(), null, Ai));
    }

    [Fact]
    public void BuntHeavy_RaisesBuntProb_AndStartsEarlier()
    {
        var b = new TacticsCoefficients();
        var t = ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.BuntHeavy }, Ai);
        Assert.True(t.SacBuntProb > b.SacBuntProb);
        Assert.True(t.SacBuntFromInning < b.SacBuntFromInning);
    }

    [Fact]
    public void RunAndGun_RaisesStealAndHitAndRun_RelaxesSuccessBar()
    {
        var b = new TacticsCoefficients();
        var t = ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.RunAndGun }, Ai);
        Assert.True(t.StealProb > b.StealProb);
        Assert.True(t.HitAndRunProb > b.HitAndRunProb);
        Assert.True(t.StealMinSuccess < b.StealMinSuccess);
    }

    [Fact]
    public void SqueezeLover_RaisesSqueezeProb()
    {
        var b = new TacticsCoefficients();
        var t = ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.SqueezeLover }, Ai);
        Assert.True(t.SqueezeProb > b.SqueezeProb);
    }

    [Fact]
    public void AggressivePinchHit_StartsEarlier_AndLowersBar()
    {
        var b = new TacticsCoefficients();
        var t = ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.AggressivePinchHit }, Ai);
        Assert.True(t.PinchHitFromInning < b.PinchHitFromInning);
        Assert.True(t.PinchHitContactCeiling > b.PinchHitContactCeiling);
        Assert.True(t.PinchHitImprovement < b.PinchHitImprovement);
    }

    [Fact]
    public void AggressiveGear_WidensPush_AndDelaysCoast()
    {
        var b = new TacticsCoefficients();
        var t = ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.AggressiveGear }, Ai);
        Assert.True(t.GearPushInningsLeft > b.GearPushInningsLeft);
        Assert.True(t.GearPushMaxDiffAbs > b.GearPushMaxDiffAbs);
        Assert.True(t.GearCoastMinLead > b.GearCoastMinLead);
    }

    [Fact]
    public void Cautious_EnablesIntentionalWalk()
    {
        var b = new TacticsCoefficients();
        Assert.Equal(0.0, b.IntentionalWalkProb);   // 既定は無効
        var t = ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.Cautious }, Ai);
        Assert.True(t.IntentionalWalkProb > 0.0);
        Assert.True(t.IntentionalWalkMinPower < b.IntentionalWalkMinPower);
    }

    [Fact]
    public void QuickHook_MovesDefensiveSubEarlier()
    {
        var b = new TacticsCoefficients();
        var t = ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.QuickHook }, Ai);
        Assert.True(t.DefensiveSubFromInning < b.DefensiveSubFromInning);
    }

    [Fact]
    public void AceOveruseAndPromoter_DoNotTouchTacticsCoefficients()
    {
        var b = new TacticsCoefficients();
        Assert.Equal(b, ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.AceOveruse }, Ai));
        Assert.Equal(b, ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.Promoter }, Ai));
    }

    [Fact]
    public void AceOveruse_RaisesRelieveMargin_KeepsDecayCurve()
    {
        var baseF = new FatigueCoefficients();
        var f = ManagerTraitEffects.FatigueOverride(baseF, new[] { ManagerTrait.AceOveruse }, Ai);
        Assert.NotNull(f);
        Assert.True(f!.RelievePitchMargin > baseF.RelievePitchMargin);   // エースを引っ張る
        Assert.True(f.HardCapPitches > baseF.HardCapPitches);
        // 疲労の減衰カーブは物理として不変＝諸刃（引っ張ると被打率上昇）が既存モデルで自然に出る。
        Assert.Equal(baseF.VelocityDropPerOverPitch, f.VelocityDropPerOverPitch);
        Assert.Equal(baseF.ControlDropPerOverPitch, f.ControlDropPerOverPitch);
    }

    [Fact]
    public void QuickHook_LowersRelieveMargin()
    {
        var baseF = new FatigueCoefficients();
        var f = ManagerTraitEffects.FatigueOverride(baseF, new[] { ManagerTrait.QuickHook }, Ai);
        Assert.NotNull(f);
        Assert.True(f!.RelievePitchMargin < baseF.RelievePitchMargin);
    }

    [Fact]
    public void Traits_StackMultiplicatively()
    {
        var b = new TacticsCoefficients();
        var t = ManagerTraitEffects.ApplyTactics(b, new[] { ManagerTrait.BuntHeavy, ManagerTrait.RunAndGun }, Ai);
        Assert.Equal(b.SacBuntProb * Ai.BuntHeavyBuntFactor, t.SacBuntProb, 6);
        Assert.Equal(b.StealProb * Ai.RunAndGunStealFactor, t.StealProb, 6);
    }

    [Fact]
    public void HasPromoter_DetectsTrait()
    {
        Assert.True(ManagerTraitEffects.HasPromoter(new[] { ManagerTrait.Promoter }));
        Assert.True(ManagerTraitEffects.HasPromoter(new[] { ManagerTrait.BuntHeavy, ManagerTrait.Promoter }));
        Assert.False(ManagerTraitEffects.HasPromoter(new[] { ManagerTrait.BuntHeavy }));
        Assert.False(ManagerTraitEffects.HasPromoter(null));
    }
}
