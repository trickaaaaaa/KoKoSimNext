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
        private static readonly TeamStrengthCoefficients Coeff = TeamStrengthCoeff.Default;

        /// <summary>ロスターの総合力（0〜100, 較正済み）。空なら 0。</summary>
        public static double Of(IReadOnlyList<DevelopingPlayer> roster)
            => roster == null || roster.Count == 0 ? 0.0 : TeamStrengthProfile.Compute(roster, Coeff).Overall;

        /// <summary>
        /// ロスターの総合ランク（S〜G）。<see cref="TeamStrengthCoefficients.OverallTierBoost"/>（issue #1080）で
        /// 補正済み＝<see cref="Of"/> から素で再計算しない（見た目の総合ランクを変えない単一入口）。
        /// </summary>
        public static Tier TierOf(IReadOnlyList<DevelopingPlayer> roster)
            => roster == null || roster.Count == 0 ? Tier.G : TeamStrengthProfile.Compute(roster, Coeff).Tier;

        /// <summary>ロスターの総合ランク（S〜G, 較正済み）。</summary>
        public static string GradeOf(IReadOnlyList<DevelopingPlayer> roster)
            => TierOf(roster).ToString();
    }
}
