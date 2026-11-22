using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 監督傾向（<see cref="ManagerTrait"/>）→ 係数変換（issue #55）。<see cref="AiTacticsBrain.ApplyStyle"/> と
/// 同型で、采配系は <see cref="TacticsCoefficients"/> に重ね（<see cref="ApplyTactics"/>）、継投系は
/// チームの継投の余裕の倍率を載せる（<see cref="Apply"/>, 決定4: B-1）。
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
                    // 校風 SmallBall（AiTacticsBrain.ApplyStyle）と同じく三盗も同じ重みで緩める（下限は三盗側の 0.30）。
                    StealThirdProb = Clamp01(b.StealThirdProb * ai.RunAndGunStealFactor),
                    StealThirdMinSuccess = MathUtil.Clamp(
                        b.StealThirdMinSuccess - ai.RunAndGunStealMinSuccessRelax, 0.30, 0.99),
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
                    // 基準の IntentionalWalkProb と大きい方を採る。既定の基準は 0.6（cautious_intentional_walk_prob の既定 0.55 より
                    // 大きい）ので現行の既定値では確率は動かず、効くのは MinPower の緩和（打者の閾値を下げる）の方。
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
    /// 継投系の傾向から、継投の余裕（<see cref="FatigueCoefficients.RelievePitchMargin"/>）に掛ける倍率を返す
    /// （決定4: B-1）。エース酷使/継投早めのどちらも無ければ 1.0（＝<c>ctx.Fatigue</c> そのまま・帯不変）。
    /// 両方は生成側で排他にするが、万一重なった場合は順に掛ける（相殺）。
    /// 倍率だけを返し係数を丸ごと作らないのは、試合の係数（天候で消耗の勾配が変わる）を潰さないため（issue #1047）。
    /// チームへの載せ方は <see cref="Apply"/> に1か所（組み方が2系統あっても同じ関数を通す）。
    /// </summary>
    public static double RelieveMarginFactor(IReadOnlyList<ManagerTrait>? traits, EnemyAiCoefficients ai)
    {
        if (traits is null) return 1.0;
        // ハードキャップ撤廃（issue #356）に伴い、エース酷使は RelievePitchMargin 側だけで表現する
        // （margin を伸ばす＝より引っ張る）。
        var f = 1.0;
        if (traits.Contains(ManagerTrait.AceOveruse)) f *= ai.AceOveruseRelieveMarginFactor;
        if (traits.Contains(ManagerTrait.QuickHook)) f *= ai.QuickHookRelieveMarginFactor;
        return f;
    }

    /// <summary>
    /// 組み上がったチームへ「チーム単位で効く」監督傾向（継投の余裕）を載せる（issue #1047）。
    /// 使い捨て生成（<c>StrengthTeamFactory</c>）と永続ロスター（<c>AiTeamBuilder</c>）の両方がここを通る
    /// ＝片方の組み方だけ傾向が抜ける事故（#80 で継投系が実ゲームから消えていた）を防ぐ。
    /// </summary>
    public static Team Apply(Team team, IReadOnlyList<ManagerTrait>? traits, EnemyAiCoefficients ai)
        => team with { RelieveMarginFactor = RelieveMarginFactor(traits, ai) };
}
