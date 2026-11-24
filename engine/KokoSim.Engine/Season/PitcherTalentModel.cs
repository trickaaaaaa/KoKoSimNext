using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 投手の素質の「見出され」の段（issue #1523）。隠しの投手センスが高いのに、いま見える投手能力が伸びていない選手に、
/// 見立て→外部コーチの2段のイベントで起きる。<b>肩書き（投手か野手か）は見ない</b>＝センスと見える投手能力の差だけで決まる。
/// </summary>
public enum PitcherTalentStage
{
    None,        // 何も起きていない
    Hinted,      // 見立てが出た（「投手に向いているかもしれない」）。見立てだけでは伸びは変わらない
    Discovered,  // 外部コーチに見出された＝覚醒。投手系の伸びの倍率が上がる（TrainingCoefficients.PitcherAwakenedGrowthFactor）
}

/// <summary>投手の素質イベント1件（<paramref name="Stage"/> はこの週に進んだ先の段）。</summary>
public sealed record PitcherTalentNotice(DevelopingPlayer Player, PitcherTalentStage Stage);

/// <summary>
/// 投手の素質の見出し（issue #1523・オーナー判断 2026-11-24）。週次に、対象の選手を見立て→外部コーチの2段で進める。
/// 対象＝<b>隠しの投手センス</b>と<b>いま見えている投手能力（球速・制球・キレの平均）</b>の差が大きい選手。
/// 投手として使っている選手でも、伸び悩んでいれば対象になる（役判定の <c>LeansPitcher</c> は使わない）。
/// 変える状態は <see cref="DevelopingPlayer.PitcherTalent"/> だけ。能力・才能上限は動かさない。
/// </summary>
public static class PitcherTalentModel
{
    /// <summary>見える投手能力（球速・制球・キレの平均・物理Lv）。</summary>
    public static double VisiblePitching(DevelopingPlayer p) =>
        (AbilityScale.Phys(p.Level(AbilityKind.Velocity))
         + AbilityScale.Phys(p.Level(AbilityKind.Control))
         + AbilityScale.Phys(p.Level(AbilityKind.PitchRank))) / 3.0;

    /// <summary>隠しの投手センスが見える投手能力をどれだけ上回るか（物理Lv）。</summary>
    public static double Gap(DevelopingPlayer p) => AbilityScale.Phys(p.PitcherSense) - VisiblePitching(p);

    /// <summary>見立ての対象か（まだ何も起きていない・センスが高い・見える投手能力との差が大きい）。</summary>
    public static bool IsCandidate(DevelopingPlayer p, InsightCoefficients c) =>
        p.PitcherTalent == PitcherTalentStage.None
        && AbilityScale.Phys(p.PitcherSense) >= c.PitcherTalentSenseMin
        && Gap(p) >= c.PitcherTalentGapMin;

    /// <summary>
    /// 外部コーチに見出された（覚醒）ことにする。投手系の伸びの倍率が上がる（<see cref="TrainingCoefficients.PitcherAwakenedGrowthFactor"/>）のに加え、
    /// 投手系能力の才能上限を <see cref="InsightCoefficients.PitcherTalentCapBonus"/> だけ引き上げる（99で止める）。
    /// 倍率だけでは、必要経験値が指数で増えるぶん伸びがほとんど変わらず、生成時の野手並みの才能上限にも当たるため。
    /// </summary>
    public static void Discover(DevelopingPlayer p, InsightCoefficients c)
    {
        p.PitcherTalent = PitcherTalentStage.Discovered;
        foreach (var k in AbilityKinds.All)
        {
            if (!AbilityKinds.IsPitching(k)) continue;
            p.SetCap(k, System.Math.Min(99, p.Cap(k) + c.PitcherTalentCapBonus));
        }
    }

    /// <summary>
    /// 週次の見出し。見立て済みの選手は外部コーチで見出され（覚醒）、候補の選手は見立てが出る。
    /// 1人1週に1段まで＝同じ週に見立てと見出しは重ならない。乱数は対象の選手だけが引く。
    /// </summary>
    public static IReadOnlyList<PitcherTalentNotice> Week(
        IReadOnlyList<DevelopingPlayer> roster, IRandomSource rng, InsightCoefficients c)
    {
        var notices = new List<PitcherTalentNotice>();
        foreach (var p in roster)
        {
            if (p.PitcherTalent == PitcherTalentStage.Hinted)
            {
                if (!MathUtil.Chance(c.PitcherTalentDiscoverProb, rng)) continue;
                Discover(p, c);
                notices.Add(new PitcherTalentNotice(p, PitcherTalentStage.Discovered));
            }
            else if (IsCandidate(p, c))
            {
                if (!MathUtil.Chance(c.PitcherTalentHintProb, rng)) continue;
                p.PitcherTalent = PitcherTalentStage.Hinted;
                notices.Add(new PitcherTalentNotice(p, PitcherTalentStage.Hinted));
            }
        }
        return notices;
    }
}
