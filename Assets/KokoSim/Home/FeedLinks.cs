using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 通知フィードの行を押したときの飛び先（設計書06 §4・issue #1474）。
    ///
    /// <para>飛び先が<b>一意に決まる通知だけ</b>に <see cref="FeedItem.Screen"/> を付ける（イベント・新チーム始動・勢いは押せない）。
    /// 選手ひとりの通知は選手一覧をその選手の詳細で開く。選手がもう部にいない（卒業・転任）ときは飛ばない。</para>
    /// </summary>
    public static class FeedLinks
    {
        public const string Players = "PlayerList";
        public const string Scout = "ScoutScreen";
        public const string Tournament = "TournamentPreview";
        public const string Career = "CareerScreen";
        public const string Records = "RecordsScreen";
        public const int RecordsMatchesPage = KokoSim.Unity.Records.RecordsScreenController.MatchesPage;

        /// <summary>押せる行か（飛び先があり、選手の通知ならその選手がまだ部にいる）。</summary>
        public static bool CanOpen(FeedItem f)
            => f != null && !string.IsNullOrEmpty(f.Screen) && (f.PlayerId < 0 || RosterIndexOf(f.PlayerId) >= 0);

        public static void Open(FeedItem f)
        {
            if (!CanOpen(f)) return;
            if (f.PlayerId >= 0)
            {
                KokoSim.Unity.Players.PlayerSelection.Index = RosterIndexOf(f.PlayerId);
                KokoSim.Unity.Players.PlayerSelection.Pinned = true;
            }
            if (f.RecordsPage >= 0) KokoSim.Unity.Records.RecordsScreenController.ReturnPage = f.RecordsPage;
            ScreenRouter.Instance?.ShowDeferred(f.Screen);
        }

        /// <summary>選手詳細の Index は <see cref="RosterService.Roster"/> の並び。</summary>
        private static int RosterIndexOf(int playerId)
        {
            var r = RosterService.Roster;
            for (var i = 0; i < r.Count; i++)
                if (r[i].Id == playerId && !r[i].IsRetired) return i;
            return -1;
        }
    }
}
