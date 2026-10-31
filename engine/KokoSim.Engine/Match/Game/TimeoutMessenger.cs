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
    /// </summary>
    public static double Factor(Player? messenger, Player receiver, bool toPitcher,
        TacticsCoefficients c, PersonalityCoefficients pc)
    {
        if (messenger is null) return 1.0;
        var f = 1.0 + (messenger.Leadership - c.MessengerLeadershipNeutral) * c.MessengerLeadershipSlope;
        if (messenger.Grade == 3) f += c.MessengerSeniorBonus;
        else if (messenger.Grade == 1) f -= c.MessengerFreshmanPenalty;
        if (messenger.Grade > 0 && messenger.Grade == receiver.Grade) f += c.MessengerSameGradeBonus;
        var profile = pc.Profile(messenger.Personality);
        f += toPitcher ? profile.MessengerToPitcher : profile.MessengerToBatter;
        return Math.Clamp(f, c.MessengerFactorMin, c.MessengerFactorMax);
    }

    /// <summary>
    /// AI監督の人選。采配能力が <see cref="TacticsCoefficients.AiMessengerPickMinSense"/> 以上なら係数が最大の控え
    /// （同値は先頭）、未満ならベンチの先頭（手近な控え）。控えが居なければ null（記録員が走る＝係数1.0）。
    /// </summary>
    public static Player? AiPick(IReadOnlyList<Player> reserves, Player receiver, bool toPitcher, int tacticalSense,
        TacticsCoefficients c, PersonalityCoefficients pc)
    {
        if (reserves.Count == 0) return null;
        if (tacticalSense < c.AiMessengerPickMinSense) return reserves[0];
        var best = reserves[0];
        var bestF = Factor(best, receiver, toPitcher, c, pc);
        for (var i = 1; i < reserves.Count; i++)
        {
            var f = Factor(reserves[i], receiver, toPitcher, c, pc);
            if (f > bestF) { best = reserves[i]; bestF = f; }
        }
        return best;
    }
}
