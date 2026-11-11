using System;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 主将指名画面の「スタメン」「ベンチ」の段階（issue #861・設計書09 §8.1）。候補を主将にしたとき、
/// スタメンで使うならチーム全員への底上げがどれだけ、ベンチで伝令に走らせるなら伝令がどれだけ効くかを、
/// 数字を出さずに G〜S の段階で見せる。境界は #860 の式（<see cref="TimeoutMessenger.FactorOf"/>・
/// <c>CaptainPresencePerPower</c>）から引くので、UI 側に式を書かない。純関数・乱数不使用。
/// </summary>
public static class CaptainEffectTiers
{
    /// <summary>
    /// スタメン段階: 統率力（統率傾向×精神力/100）×「精神的支柱」の拡大を 0〜100 に丸めた値。
    /// 全員への底上げ倍率は統率力に比例するので、統率力そのものが「上限（統率力100）に対する割合」になる。
    /// </summary>
    public static double StarterScore(DevelopingPlayer p, SkillCoefficients? skills = null)
    {
        var pillar = p.Skills.Has(Skill.SpiritualPillar)
            ? (skills ?? new SkillCoefficients()).SpiritualPillarCaptainFactor
            : 1.0;
        return Math.Clamp(CaptainSelector.LeadershipPower(p) * pillar, 0.0, 100.0);
    }

    /// <summary>
    /// ベンチ段階: 主将として伝令に走らせたときの係数（<see cref="TimeoutMessenger.FactorOf"/>）を、
    /// 下限〜主将専用上限（<c>CaptainMessengerFactorMax</c>）の間で 0〜100 に写した値。
    /// 受け手は決まっていないので、受け手の学年一致は見ず、投手向き・打者向きの平均で見る。
    /// </summary>
    public static double BenchScore(DevelopingPlayer p, TacticsCoefficients? tactics = null,
        PersonalityCoefficients? personalities = null)
    {
        var c = tactics ?? new TacticsCoefficients();
        var pc = personalities ?? new PersonalityCoefficients();
        var toPitcher = TimeoutMessenger.FactorOf(p.Leadership, p.Mental, p.Grade, p.Personality, 0, true, true, c, pc);
        var toBatter = TimeoutMessenger.FactorOf(p.Leadership, p.Mental, p.Grade, p.Personality, 0, false, true, c, pc);
        var f = (toPitcher + toBatter) / 2.0;
        var span = c.CaptainMessengerFactorMax - c.MessengerFactorMin;
        return span <= 0 ? 0.0 : Math.Clamp((f - c.MessengerFactorMin) / span * 100.0, 0.0, 100.0);
    }

    public static Tier Starter(DevelopingPlayer p, SkillCoefficients? skills = null)
        => Tiers.FromStrength(StarterScore(p, skills));

    public static Tier Bench(DevelopingPlayer p, TacticsCoefficients? tactics = null,
        PersonalityCoefficients? personalities = null)
        => Tiers.FromStrength(BenchScore(p, tactics, personalities));
}
