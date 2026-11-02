using System.Collections.Generic;
using KokoSim.Engine.Nation;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 相手校のランクと6指標（issue #737）。ホームの次戦表示・練習試合の申込先一覧と詳細は、**実際に戦うチーム**
    /// （<see cref="PlayerMatchResolver.PeekOpponentTeam"/>＝試合前画面の <see cref="PlayerMatchResolver.BuildOpponentTeam(School)"/>
    /// と同じチーム）の総合から同じ式で出す。学校に付いた抽象的な強さ（<see cref="School.Tier"/>）は実ロスターと
    /// 別に動くので相手のランクには使わない。表示専用＝乱数を引かず、ロスターも保存しない。
    /// </summary>
    public static class OpponentRank
    {
        // 画面は何度も描き直すので、年・週・大会日が変わるまで学校ごとに控える。
        private static readonly Dictionary<int, TeamStrength> Cache = new Dictionary<int, TeamStrength>();
        private static (int Year, int Week, int Day) _cachedAt = (-1, -1, -1);

        public static TeamStrength ProfileOf(School school)
        {
            var s = GameSession.Current;
            var at = (s.Year, GameClock.Week, s.TournamentDay);
            if (at != _cachedAt) { Cache.Clear(); _cachedAt = at; }
            if (Cache.TryGetValue(school.Id, out var cached)) return cached;
            var strength = ScoutedTeamProfile.Compute(PlayerMatchResolver.PeekOpponentTeam(school), TeamStrengthCoeff.Default);
            Cache[school.Id] = strength;
            return strength;
        }

        public static Tier Of(School school) => Tiers.FromStrength(ProfileOf(school).Overall);
    }
}
