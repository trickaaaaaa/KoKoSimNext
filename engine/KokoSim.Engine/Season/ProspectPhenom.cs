using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 自校の怪物（Phenom）（issue #1468・OPEN-QUESTIONS Q20 決定6 改訂・design-04 §2.1）。
/// AI校の怪物（<see cref="PhenomPackages"/>）と同じ型・同じ帯（<see cref="PhenomCoefficients"/>）を、
/// 自校の選手（<see cref="DevelopingPlayer"/>・能力はレベル＋才能上限）へ載せる。
///
/// <para>出る道は2つで、どちらも<b>名声と関係ない</b>（2026-11-23 オーナー判断）:
/// ①たまに無条件で新入生に混じる（<see cref="PhenomCoefficients.OwnIntakeRatePerYear"/>・<see cref="RollIntake"/>）
/// ②スカウトで触れる中学生に混じり、勧誘に成功すると入部する
/// （<see cref="PhenomCoefficients.ScoutCandidateRate"/>・<see cref="ScoutModel.BuildCandidates"/>）。</para>
///
/// <para>乱数はすべて呼び出し側が Fork した専用ストリーム＝受動枠・候補の他の抽選を1ビットも動かさない（不変条件#2）。
/// 投手系の型は投手役に読める選手（<see cref="PitcherRoleResolver.LeansPitcher(DevelopingPlayer, RosterCoefficients)"/>・
/// 創発）にだけ載せる（Q20 追記「投手枠/野手枠で分けて抽選しない・適性から導出する」）。</para>
/// </summary>
public static class ProspectPhenom
{
    /// <summary>
    /// 受動枠の新入生コホートに、確率 <see cref="PhenomCoefficients.OwnIntakeRatePerYear"/> で怪物を1人混ぜる。
    /// 型は AI校と同じ比（尖り型5種の重み・総合型は出現率の比）で引き、型に合う成員から1人選ぶ。
    /// 怪物にした選手は列の先頭へ移す（スカウトの成功は受動枠の末尾と差し替わる＝怪物が押し出されない）。
    /// </summary>
    /// <returns>怪物にした選手（いなければ null）。</returns>
    public static DevelopingPlayer? RollIntake(List<DevelopingPlayer> cohort, RosterCoefficients c, IRandomSource rng)
    {
        var pc = c.Phenom;
        if (cohort.Count == 0 || !(rng.NextDouble() < pc.OwnIntakeRatePerYear)) return null;
        var type = RollType(pc, rng);
        var candidates = new List<int>();
        for (var i = 0; i < cohort.Count; i++)
            if (Fits(cohort[i], type, c)) candidates.Add(i);
        if (candidates.Count == 0) return null;
        var idx = candidates[rng.NextInt(0, candidates.Count)];
        var p = cohort[idx];
        Apply(p, type, pc, rng);
        cohort.RemoveAt(idx);
        cohort.Insert(0, p);
        return p;
    }

    /// <summary>
    /// 怪物の型を引く（怪物が出ると決まった後の条件付き）。総合型 : 尖り型 ＝ AI校の出現率の比、尖り型の中は重み。
    /// </summary>
    public static PhenomType RollType(PhenomCoefficients c, IRandomSource rng)
    {
        var total = c.AllRoundRatePerSchoolYear + c.SpikeRatePerSchoolYear;
        var allRoundShare = total > 0 ? c.AllRoundRatePerSchoolYear / total : 0.0;
        if (rng.NextDouble() < allRoundShare) return PhenomType.AllRound;
        return PickWeighted(new[]
        {
            (PhenomType.Ace, c.AceWeight), (PhenomType.Finesse, c.FinesseWeight),
            (PhenomType.Slugger, c.SluggerWeight), (PhenomType.Speedster, c.SpeedsterWeight),
            (PhenomType.StrongArm, c.StrongArmWeight),
        }, rng);
    }

    /// <summary>
    /// この選手に合う型を引く（スカウト候補用＝選手が先に決まっている）。総合型は全員に、投手系は投手役に読める選手、
    /// 打撃系は読めない選手、鉄砲肩は最良の守備位置が捕手・外野の選手に。重みは <see cref="RollType"/> と同じ。
    /// </summary>
    public static PhenomType RollTypeFor(DevelopingPlayer p, RosterCoefficients c, IRandomSource rng)
    {
        var pc = c.Phenom;
        var total = pc.AllRoundRatePerSchoolYear + pc.SpikeRatePerSchoolYear;
        var allRoundShare = total > 0 ? pc.AllRoundRatePerSchoolYear / total : 0.0;
        if (rng.NextDouble() < allRoundShare) return PhenomType.AllRound;
        var options = new List<(PhenomType, double)>();
        foreach (var (t, w) in new[]
                 {
                     (PhenomType.Ace, pc.AceWeight), (PhenomType.Finesse, pc.FinesseWeight),
                     (PhenomType.Slugger, pc.SluggerWeight), (PhenomType.Speedster, pc.SpeedsterWeight),
                     (PhenomType.StrongArm, pc.StrongArmWeight),
                 })
        {
            if (Fits(p, t, c)) options.Add((t, w));
        }
        return options.Count == 0 ? PhenomType.AllRound : PickWeighted(options.ToArray(), rng);
    }

    /// <summary>型がこの選手に合うか（AI校の <c>AiRosterFactory.PickPhenomMember</c> と同じ読み・適性から導出）。</summary>
    public static bool Fits(DevelopingPlayer p, PhenomType type, RosterCoefficients c)
    {
        var leans = PitcherRoleResolver.LeansPitcher(p, c);
        return type switch
        {
            PhenomType.Ace or PhenomType.Finesse => leans,
            PhenomType.StrongArm => !leans && BestFieldPosition(p)
                is FieldPosition.Catcher or FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField,
            PhenomType.Slugger or PhenomType.Speedster => !leans,
            PhenomType.AllRound => true,
            _ => false,
        };
    }

    /// <summary>
    /// 型のパッケージを載せる（主軸 S帯＋同系統の支持能力の底上げ。帯は <see cref="PhenomCoefficients"/>・AI校と共有）。
    /// 能力は「今より低ければ帯から引いた値へ上げる」（AI校と同じ）。才能上限は元の伸びしろ（上限−現在値）を保って押し上げる
    /// ＝怪物になっても育つ余地は普通の子と同じだけ残る。
    /// </summary>
    public static void Apply(DevelopingPlayer p, PhenomType type, PhenomCoefficients c, IRandomSource rng)
    {
        if (type == PhenomType.None) return;
        p.Phenom = type;
        void Main(AbilityKind k) => Raise(p, k, rng.NextInt(c.MainMin, c.MainMax + 1));
        void Support(AbilityKind k) => Raise(p, k, rng.NextInt(c.SupportMin, c.SupportMax + 1));
        void Hi(AbilityKind k) => Raise(p, k, rng.NextInt(c.AllRoundMin, c.AllRoundMax + 1));
        switch (type)
        {
            case PhenomType.Ace:   // 剛腕: 球速S＋キレ・スタミナ（制球は据え置き＝ノーコン剛腕の怪物味）
                Main(AbilityKind.Velocity); Support(AbilityKind.PitchRank); Support(AbilityKind.Stamina);
                break;
            case PhenomType.Finesse:   // 超技巧: 制球S＋キレ
                Main(AbilityKind.Control); Support(AbilityKind.PitchRank);
                break;
            case PhenomType.Slugger:   // スラッガー: パワーS＋ミート・弾道高め
                Main(AbilityKind.Power); Support(AbilityKind.Contact); Support(AbilityKind.LaunchTendency);
                break;
            case PhenomType.Speedster:   // 韋駄天: 走力S＋ミート・盗塁・走塁
                Main(AbilityKind.Speed); Support(AbilityKind.Contact); Support(AbilityKind.Steal); Support(AbilityKind.Baserunning);
                break;
            case PhenomType.StrongArm:   // 鉄砲肩: 肩S＋送球精度・守備・捕球
                Main(AbilityKind.ArmStrength); Support(AbilityKind.ThrowAccuracy); Support(AbilityKind.Fielding); Support(AbilityKind.Catching);
                break;
            case PhenomType.AllRound:   // 総合型: 投打の主要能力を高帯へ（#377・二刀流の怪物）
                foreach (var k in AllRoundKinds) Hi(k);
                break;
        }
    }

    private static readonly AbilityKind[] AllRoundKinds =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.Speed, AbilityKind.ArmStrength,
        AbilityKind.Fielding, AbilityKind.Discipline,
        AbilityKind.Velocity, AbilityKind.Control, AbilityKind.PitchRank, AbilityKind.Stamina,
    };

    private static void Raise(DevelopingPlayer p, AbilityKind k, int target)
    {
        var cur = p.Level(k);
        if (target <= cur) return;
        var headroom = Math.Max(2, p.Cap(k) - cur);
        p.SetLevel(k, target);
        // 今の物差し（物理Lv）で当てる（issue #1080）＝天井は 99+Δ。
        var cap = k == AbilityKind.Stamina ? target + headroom : Math.Min(Players.AbilityScale.PhysMax99, target + headroom);
        if (p.Cap(k) < cap) p.SetCap(k, cap);
    }

    private static FieldPosition BestFieldPosition(DevelopingPlayer p)
    {
        var best = FieldPosition.Catcher;
        var bestApt = int.MinValue;
        foreach (var pos in AptitudeRoller.AllPositions)
        {
            if (pos == FieldPosition.Pitcher) continue;
            var a = p.Aptitude(pos);
            if (a > bestApt) { best = pos; bestApt = a; }
        }
        return best;
    }

    private static PhenomType PickWeighted((PhenomType Type, double Weight)[] items, IRandomSource rng)
    {
        var total = 0.0;
        foreach (var (_, w) in items) total += Math.Max(0.0, w);
        var pick = rng.NextDouble() * (total <= 0 ? 1.0 : total);
        var acc = 0.0;
        foreach (var (t, w) in items)
        {
            acc += Math.Max(0.0, w);
            if (pick < acc) return t;
        }
        return items[items.Length - 1].Type;
    }
}
