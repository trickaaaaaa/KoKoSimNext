using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// チーム総合力（6指標のリーグ標準化総合, ③ 2026-07-18）。全画面の「総合ランク」表示・
    /// 自校 School.Strength の単一入口。旧 <c>roster.Average(AverageLevel)</c> をこれに置換する。
    /// 係数は既定（data/coefficients.yaml team_strength: と同値。scale/offset で旧・AI尺度へ較正済み）。
    /// </summary>
    public static class TeamOverall
    {
        private static readonly TeamStrengthCoefficients Coeff = new TeamStrengthCoefficients();

        /// <summary>ロスターの総合力（0〜100, 較正済み）。空なら 0。</summary>
        public static double Of(IReadOnlyList<DevelopingPlayer> roster)
            => roster == null || roster.Count == 0 ? 0.0 : TeamStrengthProfile.Compute(roster, Coeff).Overall;

        /// <summary>ロスターの総合ランク（S〜G, 較正済み）。</summary>
        public static string GradeOf(IReadOnlyList<DevelopingPlayer> roster)
            => Tiers.FromStrength(Of(roster)).ToString();
    }
}
