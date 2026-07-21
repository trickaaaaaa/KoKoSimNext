using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Pitching;

/// <summary>1球の配球結果。球種・狙い位置・実測球速・球威（stuff）を持つ。</summary>
public sealed record PitchPlan
{
    /// <summary>選択した球種。</summary>
    public required PitchType Type { get; init; }

    /// <summary>狙い位置（本塁面 X, Y）[m]。</summary>
    public required double AimX { get; init; }
    public required double AimY { get; init; }

    /// <summary>この1球の実測球速[km/h]（最速を天井にサンプリング×球種の球速比, 設計書02 §1.1d）。</summary>
    public required double VelocityKmh { get; init; }

    /// <summary>球威スコア（打者のミートと突き合わせて空振り率を決める無次元量）。</summary>
    public required double Stuff { get; init; }

    /// <summary>
    /// この球を投げた投手のコントロールLevel。打球質（芯を外させる度合い）の算出に使う
    /// （<see cref="Batting.ContactModel.PitcherContactQualityBias"/>）。既定50＝リーグ平均で恒等。
    /// </summary>
    public double ControlLevel { get; init; } = 50.0;
}

/// <summary>
/// 自動配球（設計書01 §2①）。Phase 簡略版: レパートリーから球種を選び、
/// 最速を天井に毎球の球速をサンプリングし（§1.1d）、球速＋球種2軸ランクから球威を算出（§2.2）。
/// 采配接続（狙い球・配球方針・慣れ）は設計書09/§2.3b で拡張する。
/// </summary>
public static class PitchSelection
{
    public static PitchPlan Select(
        PitcherAttributes pitcher,
        Field.StrikeZone zone,
        BattingCoefficients coeff,
        PitchingCoefficients pitching,
        IRandomSource rng,
        PitcherGear gear = PitcherGear.Normal,
        Tactics.PitchDirective? directive = null,
        int catcherLead = 50)
    {
        // 配球方針（設計書09 §2.2）。無指示なら恒等＝従来と完全一致。
        var d = directive ?? Tactics.PitchDirective.Identity;

        // ゾーン中心を狙い、意図的なばらつき（AimSigma）を加える。実際の散布は後段のコントロールσで別途加算。
        // 方針は狙い位置（低め/内角）と散らし幅（コントロール重視）に効く＝効果は物理から出る。
        var aimX = rng.NextGaussian(0.0, coeff.AimSigmaXMeters * d.AimSigmaFactor) + d.AimXOffsetM;
        var aimY = zone.CenterY + rng.NextGaussian(0.0, coeff.AimSigmaYMeters * d.AimSigmaFactor) + d.AimYOffsetM;

        // ① 球種選択（Phase 簡略: ストレート基礎シェア±方針の重み、残りを変化球で等分）。
        var slot = ChoosePitch(pitcher, pitching, rng, d.StraightShareDelta);

        // ② 毎球の球速サンプリング（§1.1d）: 最速（ギア補正込み）を天井に、平均−3〜5km/hの分布。
        var ceiling = pitcher.MaxVelocityKmh + gear switch
        {
            PitcherGear.Push => pitching.GearPushVelocityBonusKmh,
            PitcherGear.Coast => -pitching.GearCoastVelocityPenaltyKmh,
            _ => 0.0,
        };
        var drop = MathUtil.Clamp(
            rng.NextGaussian(pitching.VelocityDropMeanKmh, pitching.VelocityDropSigmaKmh),
            0.0, pitching.VelocityDropMaxKmh);
        var rawVelo = ceiling - drop;

        // 球種の球速比を掛けた実投球速度（表示・実況・タイムライン用）。
        var pitchVelo = rawVelo * slot.SpeedRatio;

        // ③ 球威 = 腕の振り（生球速）成分 ＋ 捕手リード成分。
        // 球種ランク（球威×キレ）の効きは設計書15 Phase E-2 で弾道由来の変化量へ完全移管した
        // （Sharpness→rpm→変化量の経路で既に織り込み済みのため、ここでの直参照は廃止＝二重計上回避）。
        // 捕手リード（設計書01 §2①）: 良い配球ほど球威が引き立つ。50=平均で恒等（帯不変）。
        var stuff = (rawVelo - coeff.StuffBaseVelocityKmh) * coeff.StuffPerKmh
                    + (catcherLead - 50) * pitching.CatcherLeadStuffPerPoint;

        return new PitchPlan
        {
            Type = slot.Type,
            AimX = aimX,
            AimY = aimY,
            VelocityKmh = pitchVelo,
            Stuff = stuff,
            ControlLevel = pitcher.Control,
        };
    }

    private static PitchSlot ChoosePitch(
        PitcherAttributes pitcher, PitchingCoefficients c, IRandomSource rng, double straightShareDelta = 0.0)
    {
        var rep = pitcher.EffectiveRepertoire;
        if (rep.Count == 1) return rep[0];

        // ストレートを基礎シェア±配球方針で選択、残りを変化球で等分。
        var share = MathUtil.Clamp(c.StraightShare + straightShareDelta, 0.05, 0.95);
        if (rng.NextDouble() < share)
        {
            for (var i = 0; i < rep.Count; i++)
            {
                if (rep[i].Type == PitchType.Fastball) return rep[i];
            }
        }
        var breaking = rep.Count - CountFastballs(rep);
        if (breaking <= 0) return rep[0];
        var pick = rng.NextInt(0, breaking);
        for (var i = 0; i < rep.Count; i++)
        {
            if (rep[i].Type == PitchType.Fastball) continue;
            if (pick-- == 0) return rep[i];
        }
        return rep[0];
    }

    private static int CountFastballs(System.Collections.Generic.IReadOnlyList<PitchSlot> rep)
    {
        var n = 0;
        for (var i = 0; i < rep.Count; i++)
        {
            if (rep[i].Type == PitchType.Fastball) n++;
        }
        return n;
    }
}
