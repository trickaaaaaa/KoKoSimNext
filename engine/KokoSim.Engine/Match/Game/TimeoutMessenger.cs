using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 伝令役の人選（issue #716・設計書09 §3）。誰を伝令に走らせるかで「落ち着き」の効き目が変わる。
/// 純関数・乱数不使用。伝令役なし（控え0人＝記録員が走る／旧経路）は係数1.0＝#714 と同じ効き目。
/// </summary>
public static class TimeoutMessenger
{
    /// <summary>
    /// 伝令役の係数 = clamp(Min, Max, 1 + 統率傾向 + 学年 + 同学年 + 性格の得意場面)。
    /// receiver＝伝令を受ける選手（守備伝令なら投手、攻撃伝令ならその打席の打者）。学年0（未設定）は学年の項を足さない。
    /// captain＝チームの主将（§8）。messenger が主将本人なら統率力に比例した上乗せを足し、
    /// 通常の上限（<see cref="TacticsCoefficients.MessengerFactorMax"/>＝1.4）を超えて
    /// <see cref="TacticsCoefficients.CaptainMessengerFactorMax"/>まで伸びる（issue #860 B・
    /// ベンチの主将を伝令で使う「ここ一番」の得＝Aの常時効果とは別枠）。
    /// </summary>
    public static double Factor(Player? messenger, Player receiver, bool toPitcher,
        TacticsCoefficients c, PersonalityCoefficients pc, Player? captain = null)
    {
        if (messenger is null) return 1.0;
        var isCaptain = captain is not null && ReferenceEquals(messenger, captain);
        return FactorOf(messenger.Leadership, messenger.Mental, messenger.Grade, messenger.Selflessness,
            receiver.Grade, toPitcher, isCaptain, c, pc);
    }

    /// <summary>
    /// <see cref="Factor"/> の式の本体（選手の能力値だけを受ける）。試合層の <see cref="Player"/> を持たない
    /// 育成層の選手（主将指名画面の段階表示など）も同じ式で引けるよう、式はここ一箇所にだけ置く。
    /// </summary>
    public static double FactorOf(int leadership, int mental, int grade, int selflessness,
        int receiverGrade, bool toPitcher, bool isCaptain, TacticsCoefficients c, PersonalityCoefficients pc)
    {
        var f = 1.0 + (leadership - c.MessengerLeadershipNeutral) * c.MessengerLeadershipSlope;
        if (grade == 3) f += c.MessengerSeniorBonus;
        else if (grade == 1) f -= c.MessengerFreshmanPenalty;
        if (grade > 0 && grade == receiverGrade) f += c.MessengerSameGradeBonus;
        // 性格④の得意場面（設計書23 §1.2）: 献身は投手へ（寄り添う）、目立ちたがりは打者へ（盛り上げる）。
        // 統率の分は上の Leadership の項が見ているので、ここでは④だけを見る。
        f += pc.MessengerBonus(selflessness, toPitcher);
        if (isCaptain)
        {
            f += leadership * mental / 100.0 * c.CaptainMessengerBonusPerPower;
            return Math.Clamp(f, c.MessengerFactorMin, c.CaptainMessengerFactorMax);
        }
        return Math.Clamp(f, c.MessengerFactorMin, c.MessengerFactorMax);
    }

    /// <summary>
    /// AI監督の人選。采配能力が <see cref="TacticsCoefficients.AiMessengerPickMinSense"/> 以上なら係数が最大の控え
    /// （同値は先頭）、未満ならベンチの先頭（手近な控え）。控えが居なければ null（記録員が走る＝係数1.0）。
    /// captain を渡すと <see cref="Factor"/> の主将ボーナス（issue #860 B）込みで比較するので、
    /// ベンチの主将は自然に最有力候補になる＝AI監督の人選を別途変える必要はない。
    /// </summary>
    public static Player? AiPick(IReadOnlyList<Player> reserves, Player receiver, bool toPitcher, int tacticalSense,
        TacticsCoefficients c, PersonalityCoefficients pc, Player? captain = null)
    {
        if (reserves.Count == 0) return null;
        if (tacticalSense < c.AiMessengerPickMinSense) return reserves[0];
        var best = reserves[0];
        var bestF = Factor(best, receiver, toPitcher, c, pc, captain);
        for (var i = 1; i < reserves.Count; i++)
        {
            var f = Factor(reserves[i], receiver, toPitcher, c, pc, captain);
            if (f > bestF) { best = reserves[i]; bestF = f; }
        }
        return best;
    }
}
