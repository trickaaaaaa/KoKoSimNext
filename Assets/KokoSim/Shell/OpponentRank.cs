using KokoSim.Engine.Nation;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 次戦の相手校のランク（issue #737）。ホームの次戦表示も試合前画面も、**実際に出場するチーム**
    /// （<see cref="PlayerMatchResolver.BuildOpponentTeam(School)"/>）の総合から同じ式で出す。
    /// 学校に付いた抽象的な強さ（<see cref="School.Tier"/>）は実ロスターと別に動くので相手のランクには使わない。
    /// 表示専用＝乱数を引かず、大会の組み合わせや試合結果に触れない。
    /// </summary>
    public static class OpponentRank
    {
        // ホームは毎フレーム描き直すので、相手校と日付が変わったときだけ組み直す。
        private static int _cachedId = int.MinValue;
        private static (int Year, int Week, int Day) _cachedAt;
        private static Tier _cachedTier;

        public static Tier Of(School opponent)
        {
            var s = GameSession.Current;
            var at = (s.Year, GameClock.Week, s.TournamentDay);
            if (opponent.Id == _cachedId && at == _cachedAt) return _cachedTier;
            var team = PlayerMatchResolver.BuildOpponentTeam(opponent);
            _cachedTier = ScoutedTeamProfile.OverallTier(team, TeamStrengthCoeff.Default);
            _cachedId = opponent.Id;
            _cachedAt = at;
            return _cachedTier;
        }
    }
}
