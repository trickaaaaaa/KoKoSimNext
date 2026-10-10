using System;
using System.Collections.Generic;
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
    /// 打者の反応に使う球速[km/h]（設計書21・<see cref="Players.AbilityEffectCoefficients.PitchingKmh"/>）。
    /// 実球速 <see cref="VelocityKmh"/> は表示・弾道（飛翔時間・変化量）にそのまま使い、球威・タイミング・打球質・バント難度は
    /// こちらを読む。null＝実球速と同じ（テスト等で直接組んだ PitchPlan の後方互換）。
    /// </summary>
    public double? ReactionVelocityKmh { get; init; }

    /// <summary>反応球速（未設定なら実球速）。</summary>
    public double ReactionKmh => ReactionVelocityKmh ?? VelocityKmh;

    /// <summary>
    /// この球を投げた投手のコントロールLevel。打球質（芯を外させる度合い）の算出に使う
    /// （<see cref="Batting.ContactModel.PitcherContactQualityBias"/>）。既定50＝リーグ平均で恒等。
    /// </summary>
    public double ControlLevel { get; init; } = 50.0;
}

/// <summary>
/// 自動配球（設計書01 §2①）。Phase 簡略版: レパートリーから球種を選び、
/// 最速を天井に毎球の球速をサンプリングし（§1.1d）、球速＋球種2軸ランクから球威を算出（§2.2）。
/// 球種選択は捕手リードが50超のぶんだけ投手の強い軸（ストレート=球威／変化球=キレ）へ寄る
/// （設計書01 §2①「捕手リード…から球種・コースを選択」）。
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
        // 制球の高い投手ほど隅を狙う（issue walk-rate-floor-vs-elite-pitcher）: 狙い散布を制球に応じて広げる。
        // 制球の低い投手は逆に真ん中を狙う（隅を狙う余裕が無い＝倍率<1・下限0.5）。50 で恒等。
        // 乱数の消費は従来と同じ（同じガウス2発に倍率を掛けるだけ）。
        var spread = Math.Max(0.5, 1.0 + coeff.AimSpreadControlSlopePerPoint * (pitcher.Control - 50));
        var aimX = rng.NextGaussian(0.0, coeff.AimSigmaXMeters * d.AimSigmaFactor * spread) + d.AimXOffsetM;
        var aimY = zone.CenterY + rng.NextGaussian(0.0, coeff.AimSigmaYMeters * d.AimSigmaFactor * spread) + d.AimYOffsetM;

        // ① 球種選択（Phase 簡略: ストレート基礎シェア±方針の重み、残りを変化球で等分。
        //    捕手リードが50超のぶんだけ投手の強い軸へ寄せる＝設計書01 §2①）。
        var slot = ChoosePitch(pitcher, pitching, rng, d.StraightShareDelta, catcherLead);

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
        // 打者の反応に使う球速（設計書21）: 効きが焼いた反応オフセット（速い投手ほど負）を足す＝ストレートの速さの差だけを
        // Lv50=132km/h 軸で寝かせ、消耗・ギア・ドロップ・球種の緩急はそのまま。オフセット0（効き1.0／平均投手）で rawVelo と同値。
        var reactionRaw = rawVelo + pitcher.ReactionVelocityOffsetKmh;

        // ③ 球威 = 腕の振り（生球速）成分 ＋ 捕手リード成分。
        // 球種ランク（球威×キレ）の効きは設計書15 Phase E-2 で弾道由来の変化量へ完全移管した
        // （Sharpness→rpm→変化量の経路で既に織り込み済みのため、ここでの直参照は廃止＝二重計上回避）。
        // 捕手リード（設計書01 §2①）: 良い配球ほど球威が引き立つ。50=平均で恒等（帯不変）。
        var stuff = (reactionRaw - coeff.StuffBaseVelocityKmh) * coeff.StuffPerKmh
                    + (catcherLead - 50) * pitching.CatcherLeadStuffPerPoint;

        return new PitchPlan
        {
            Type = slot.Type,
            AimX = aimX,
            AimY = aimY,
            VelocityKmh = pitchVelo,
            ReactionVelocityKmh = reactionRaw * slot.SpeedRatio,
            Stuff = stuff,
            ControlLevel = pitcher.Control,
        };
    }

    private static PitchSlot ChoosePitch(
        PitcherAttributes pitcher, PitchingCoefficients c, IRandomSource rng, double straightShareDelta, int catcherLead)
    {
        var rep = pitcher.EffectiveRepertoire;
        if (rep.Count == 1) return rep[0];

        // ストレートを基礎シェア±配球方針で選択、残りを変化球で等分。
        var share = MathUtil.Clamp(c.StraightShare + straightShareDelta, 0.05, 0.95);
        var breaking = rep.Count - CountFastballs(rep);

        // 捕手リード（設計書01 §2①）: Lead が50から離れたぶんだけ、投手の中で有効な軸
        // （ストレート=球威／変化球=キレ平均）が強い側へ配球を寄せる（50超）／弱い側へ寄る（50未満）。
        // 既存の CatcherLeadStuffPerPoint と同じ対称設計（(Lead−50)に符号あり）。50で恒等・軸互角も恒等。
        var leadFactor = MathUtil.Clamp((catcherLead - 50) / 50.0, -1.0, 1.0);
        if (leadFactor != 0.0 && breaking > 0)
        {
            var rankGap = MathUtil.Clamp((FastballRank(rep) - AverageBreakingRank(rep)) / 99.0, -1.0, 1.0);
            share = MathUtil.Clamp(share + leadFactor * rankGap * c.LeadStraightShareMaxShift, 0.05, 0.95);
        }

        if (rng.NextDouble() < share)
        {
            for (var i = 0; i < rep.Count; i++)
            {
                if (rep[i].Type == PitchType.Fastball) return rep[i];
            }
        }
        if (breaking <= 0) return rep[0];

        // 変化球が複数あり、リードが平均から離れているときだけキレ(Rank)の高い側（50未満は低い側）へ
        // 重み付け抽選。ちょうど50・変化球1本以下は従来どおり一様抽選（RNG消費列を変えない＝決定論baseline保持）。
        if (leadFactor != 0.0 && breaking > 1)
        {
            return ChooseWeightedBreaking(rep, leadFactor * c.LeadPitchWeightMaxExponent, rng);
        }

        var pick = rng.NextInt(0, breaking);
        for (var i = 0; i < rep.Count; i++)
        {
            if (rep[i].Type == PitchType.Fastball) continue;
            if (pick-- == 0) return rep[i];
        }
        return rep[0];
    }

    private static int FastballRank(IReadOnlyList<PitchSlot> rep)
    {
        for (var i = 0; i < rep.Count; i++)
        {
            if (rep[i].Type == PitchType.Fastball) return rep[i].Rank;
        }
        return 50;
    }

    private static double AverageBreakingRank(IReadOnlyList<PitchSlot> rep)
    {
        double sum = 0.0;
        var n = 0;
        for (var i = 0; i < rep.Count; i++)
        {
            if (rep[i].Type == PitchType.Fastball) continue;
            sum += rep[i].Rank;
            n++;
        }
        return n > 0 ? sum / n : 50.0;
    }

    /// <summary>変化球群から weight=Rank^exponent の重み付け抽選（exponent=0で一様と等価）。</summary>
    private static PitchSlot ChooseWeightedBreaking(IReadOnlyList<PitchSlot> rep, double exponent, IRandomSource rng)
    {
        var total = 0.0;
        for (var i = 0; i < rep.Count; i++)
        {
            if (rep[i].Type == PitchType.Fastball) continue;
            total += Math.Pow(rep[i].Rank, exponent);
        }
        var r = rng.NextDouble() * total;
        var fallback = rep[0];
        for (var i = 0; i < rep.Count; i++)
        {
            if (rep[i].Type == PitchType.Fastball) continue;
            fallback = rep[i];
            var w = Math.Pow(rep[i].Rank, exponent);
            if (r < w) return rep[i];
            r -= w;
        }
        return fallback;
    }

    private static int CountFastballs(IReadOnlyList<PitchSlot> rep)
    {
        var n = 0;
        for (var i = 0; i < rep.Count; i++)
        {
            if (rep[i].Type == PitchType.Fastball) n++;
        }
        return n;
    }
}
