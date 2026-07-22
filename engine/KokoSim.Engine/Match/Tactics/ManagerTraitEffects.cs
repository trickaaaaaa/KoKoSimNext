using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 監督傾向（<see cref="ManagerTrait"/>）→ 係数変換（issue #55）。<see cref="AiTacticsBrain.ApplyStyle"/> と
/// 同型で、采配系は <see cref="TacticsCoefficients"/> に重ね（<see cref="ApplyTactics"/>）、継投系は
/// チーム別 <see cref="FatigueCoefficients"/> を作る（<see cref="FatigueOverride"/>, 決定4: B-1）。
/// 傾向なし（空リスト）は恒等＝既存の生成・帯を1ビットも動かさない。校風と別軸なので、校風の
/// <see cref="AiTacticsBrain.ApplyStyle"/> と掛け合わさる（乗算の係数は順不同で合成される）。
/// </summary>
public static class ManagerTraitEffects
{
    private static double Clamp01(double v) => MathUtil.Clamp(v, 0.0, 1.0);

    /// <summary>抜擢型（試合前オーダー編成に効く）を持つか。</summary>
    public static bool HasPromoter(IReadOnlyList<ManagerTrait>? traits)
        => traits is not null && traits.Contains(ManagerTrait.Promoter);

    /// <summary>
    /// 采配系の傾向を <see cref="TacticsCoefficients"/> に重ねる（バント/盗塁/代打/スクイズ/ギア/敬遠）。
    /// 継投系（エース酷使・継投早め）のうち「守備固めを早める」だけはここ（TacticsCoefficients）に効く。
    /// 空リストは恒等。
    /// </summary>
    public static TacticsCoefficients ApplyTactics(
        TacticsCoefficients b, IReadOnlyList<ManagerTrait>? traits, EnemyAiCoefficients ai)
    {
        if (traits is null || traits.Count == 0) return b;
        foreach (var t in traits)
        {
            b = t switch
            {
                ManagerTrait.BuntHeavy => b with
                {
                    SacBuntProb = Clamp01(b.SacBuntProb * ai.BuntHeavyBuntFactor),
                    SacBuntFromInning = Math.Max(1, b.SacBuntFromInning - ai.BuntHeavyBuntInningEarlier),
                },
                ManagerTrait.RunAndGun => b with
                {
                    StealProb = Clamp01(b.StealProb * ai.RunAndGunStealFactor),
                    HitAndRunProb = Clamp01(b.HitAndRunProb * ai.RunAndGunHitAndRunFactor),
                    StealMinSuccess = MathUtil.Clamp(
                        b.StealMinSuccess - ai.RunAndGunStealMinSuccessRelax, 0.4, 0.99),
                },
                ManagerTrait.AggressivePinchHit => b with
                {
                    PinchHitFromInning = Math.Max(1, b.PinchHitFromInning - ai.AggressivePinchHitInningEarlier),
                    PinchHitContactCeiling = b.PinchHitContactCeiling + ai.AggressivePinchHitCeilingRelax,
                    PinchHitImprovement = Math.Max(1, b.PinchHitImprovement - ai.AggressivePinchHitImprovementRelax),
                },
                ManagerTrait.SqueezeLover => b with
                {
                    SqueezeProb = Clamp01(b.SqueezeProb * ai.SqueezeLoverSqueezeFactor),
                },
                ManagerTrait.AggressiveGear => b with
                {
                    GearPushInningsLeft = b.GearPushInningsLeft + ai.AggressiveGearPushInningsMore,
                    GearPushMaxDiffAbs = b.GearPushMaxDiffAbs + ai.AggressiveGearPushDiffMore,
                    GearCoastMinLead = b.GearCoastMinLead + ai.AggressiveGearCoastLeadLater,
                },
                ManagerTrait.Cautious => b with
                {
                    // 既定 0（無効）を正の確率へ。既に他要因で正なら大きい方を採る。
                    IntentionalWalkProb = Math.Max(b.IntentionalWalkProb, ai.CautiousIntentionalWalkProb),
                    IntentionalWalkMinPower = Math.Max(1, b.IntentionalWalkMinPower - ai.CautiousIntentionalWalkMinPowerRelax),
                },
                // 継投早め: 守備固めを1イニング早める（継投しきい値は FatigueOverride 側）。
                ManagerTrait.QuickHook => b with
                {
                    DefensiveSubFromInning = Math.Max(1, b.DefensiveSubFromInning - ai.QuickHookDefensiveSubInningEarlier),
                },
                // エース酷使・抜擢型は TacticsCoefficients には効かない（それぞれ FatigueOverride / オーダー編成）。
                _ => b,
            };
        }
        return b;
    }

    /// <summary>
    /// 継投系の傾向からチーム別 <see cref="FatigueCoefficients"/> を作る（決定4: B-1）。エース酷使/継投早めの
    /// どちらも無ければ <c>null</c>＝呼び出し側は <c>ctx.Fatigue</c> をそのまま使う（＝帯不変）。両方は生成側で
    /// 排他にするが、万一重なった場合はより保守的（引っ張る側）を優先せず順に適用する（掛け算なので相殺）。
    /// 疲労の減衰カーブ（VelocityDrop/ControlDrop）は物理として不変＝監督は継投「時期」だけを変える。
    /// </summary>
    public static FatigueCoefficients? FatigueOverride(
        FatigueCoefficients baseFatigue, IReadOnlyList<ManagerTrait>? traits, EnemyAiCoefficients ai)
    {
        if (traits is null) return null;
        var overuse = traits.Contains(ManagerTrait.AceOveruse);
        var quick = traits.Contains(ManagerTrait.QuickHook);
        if (!overuse && !quick) return null;

        var margin = baseFatigue.RelievePitchMargin;
        var hardCap = baseFatigue.HardCapPitches;
        if (overuse)
        {
            margin *= ai.AceOveruseRelieveMarginFactor;
            hardCap += ai.AceOveruseHardCapAdd;
        }
        if (quick)
        {
            margin *= ai.QuickHookRelieveMarginFactor;
        }
        return baseFatigue with { RelievePitchMargin = margin, HardCapPitches = hardCap };
    }
}
