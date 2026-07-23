using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// 相手校AIの先発選択（エース温存, issue #42）。ティア/相手との強さ差・大会の残りラウンド・
/// エースの消耗状態（#41 台帳）・校風から温存スコアを算出し、決定論抽選で先発を差し替えるかを決める。
/// rng を注入せず (校ID, 試合日) から専用ストリームを起こす＝<see cref="AiTeamBuilder"/> 同様の純関数（不変条件#2）。
/// </summary>
public static class AceRestSelector
{
    /// <summary>ロスター上の「真のエース」に常に割り当てる背番号（<see cref="AiTeamBuilder"/> と合わせる）。
    /// 温存で先発を外れても背番号は変わらない＝大会を跨いだ同一投手の同定（台帳キー）が安定する。</summary>
    public const int AceUniformNumber = 1;

    /// <summary>この試合、エースを外して次点投手を先発させるべきか。</summary>
    public static bool ShouldRestAce(School school, AceRestContext context, EnemyAiCoefficients ai)
    {
        var ownTier = (int)school.Tier;
        if (ownTier < ai.AceRestMinTier) return false;   // ローテ運用自体の引き出しがないティア

        // この試合の後に残るラウンド数（決勝=0＝もう温存する先がない）。
        var roundsAfter = Math.Max(0, context.RoundsRemaining - 1);
        if (roundsAfter <= 0) return false;

        var tierGap = Math.Max(0, ownTier - (int)context.OpponentTier);

        var key = PitcherLedgerKey.ForOpponent(school.Id, AceUniformNumber);
        var recentPitches = context.Ledger?.PitchesWithin(key, context.MatchDay, ai.AceRestFatigueWindowDays) ?? 0;
        var fatigueLoad = Math.Clamp(recentPitches / ai.AceRestFatigueReferencePitches, 0.0, 1.0);

        var score = ai.AceRestBase
            + ai.AceRestTierGapWeight * tierGap
            + ai.AceRestRoundsRemainingWeight * roundsAfter
            + ai.AceRestFatigueWeight * fatigueLoad;
        score *= StyleFactor(school.Style, ai);

        var prob = Math.Clamp(score, ai.AceRestFloor, ai.AceRestCap);

        var roll = new Xoshiro256Random(
            0xACE5_0000UL ^ (ulong)(uint)school.Id ^ ((ulong)(uint)context.MatchDay << 24)).NextDouble();
        return roll < prob;
    }

    /// <summary>校風による温存傾向の重み（設計書11 §3。豪腕依存＝ほぼ常にエース、守り勝つ・全員野球＝積極的）。</summary>
    private static double StyleFactor(SchoolStyle style, EnemyAiCoefficients ai) => style switch
    {
        SchoolStyle.AceDependent => ai.AceRestAceDependentFactor,
        SchoolStyle.DefensiveMinded => ai.AceRestDefensiveMindedFactor,
        SchoolStyle.TotalBaseball => ai.AceRestTotalBaseballFactor,
        _ => 1.0,
    };
}
