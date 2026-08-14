using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Home;
using KokoSim.Unity.Match;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校の次戦（大会戦／練習試合）のライブ観戦開始（issue #325）。呼び出し元の画面（対戦カード＝
    /// MatchPreview）に留めたまま、重い準備（大会戦の <see cref="HomeState.BeginMatch"/>＝プレフェッチ未完了時に
    /// 約25秒かかる）を <see cref="BackgroundGameOp"/> でバックグラウンドへ逃がし、完了後に MatchLive へ遷移する。
    /// もとは HomeDashboardController.StartLiveMatch/StartLivePracticeMatch（ホーム画面が中継役だった実装）を
    /// 切り出したもの。ホームは中継しない（対戦カード画面の「試合開始」から直接ここを呼ぶ）。
    /// </summary>
    public static class MatchStartFlow
    {
        /// <summary>
        /// <see cref="GameSession.AwaitingMatchStart"/> を消費し、自校の次戦（大会戦 or 練習試合）の準備を始める。
        /// 大会戦は <see cref="BackgroundGameOp"/> 経由で非同期に走り、完了後に MatchLive へ自動遷移する
        /// （呼び出し元は結果を待つ必要が無い）。既に他のバックグラウンド処理が走っていて開始できなかった場合
        /// （二重起動）は AwaitingMatchStart を戻して false を返す＝呼び出し元は詰まらず入力を再開してよい。
        /// </summary>
        public static bool Begin()
        {
            var session = GameSession.Current;
            session.AwaitingMatchStart = false;
            var started = session.PendingMatchIsPractice ? BeginPractice(session) : BeginTournament(session);
            if (!started) session.AwaitingMatchStart = true;   // 開始できなかった＝取りこぼし対策で元に戻す
            return started;
        }

        private static bool BeginTournament(GameSession session)
        {
            var homeState = HomeState.Current;

            // issue #290: BeginMatch は自県裏カードのプレフェッチ未完了時に JoinPrefetch の Task.Wait で
            // メインスレッドを最大約25秒固める。Task.Run で別スレッドへ載せ替える（engine は単一スレッド逐次の
            // まま＝決定論は不変）。二重起動は BackgroundGameOp.Running が弾く（Begin が false を返す）。
            LivePlayerMatch live = null;
            return BackgroundGameOp.Run(
                () => live = homeState.BeginMatch(),
                () =>
                {
                    MatchLiveController.Pending = new MatchLiveController.LiveMatchRequest
                    {
                        Progression = live.Progression,
                        ManagerIsAway = live.ManagerIsAway,
                        AwayName = live.ManagerIsAway ? NationService.ManagerSchoolName : live.OpponentName,
                        HomeName = live.ManagerIsAway ? live.OpponentName : NationService.ManagerSchoolName,
                        AwayColors = live.ManagerIsAway
                            ? ManagerColors() : OpponentColorsOf(live.OpponentId),
                        HomeColors = live.ManagerIsAway
                            ? OpponentColorsOf(live.OpponentId) : ManagerColors(),
                        // 成績の帰属キーの校側（issue #634）。配色と同じ静的経路で渡す。
                        AwaySchoolId = live.ManagerIsAway ? NationService.ManagerSchoolId : live.OpponentId,
                        HomeSchoolId = live.ManagerIsAway ? live.OpponentId : NationService.ManagerSchoolId,
                        ManagerTacticalSense = ManagerService.TacticalSenseForAi,
                        OnComplete = result =>
                        {
                            // issue #138: CompleteMatch は残りブラケットのフルシム＋全国背景シムへの join を含み重い。
                            // 同じくバックグラウンドへ載せ替え、完了後にホームへ遷移する
                            // （OnEnable が ResultPending を見て大会結果モーダルを出す）。
                            BackgroundGameOp.Run(
                                () => homeState.CompleteMatch(result),
                                () => ScreenRouter.Instance?.ShowDeferred("HomeDashboard"));
                        },
                    };
                    // 呼び出し元（MatchPreview の Show の内側）から同期 Show するとネストして
                    // 全画面が非アクティブに落ちるため、次フレームへ遅延して配信外で切り替える。
                    ScreenRouter.Instance?.ShowDeferred("MatchLive");
                });
        }

        /// <summary>
        /// 練習試合をライブ観戦で開始する（issue #269）。<see cref="GameSession.BeginPracticeMatch"/> は
        /// プレフェッチのような重い待ちを持たないため、大会戦と違い同期のままでよい。
        /// </summary>
        private static bool BeginPractice(GameSession session)
        {
            var opponentName = session.PracticeOpponent?.Name ?? "相手校";
            var outcome = session.BeginPracticeMatch();
            if (outcome == null || !outcome.Started || outcome.Live == null)
            {
                // 受諾済みのはずなので通常は来ない（週送り等で状況が変わった場合の保険）。導線を閉じてホームへ戻す。
                session.ClearPracticeMatchFlow();
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
                return true;
            }

            var live = outcome.Live;
            var ownName = NationService.ManagerSchoolName;
            var opponentId = session.PracticeOpponent?.Id ?? -1;
            MatchLiveController.Pending = new MatchLiveController.LiveMatchRequest
            {
                Progression = live.Progression,
                ManagerIsAway = live.ManagerIsAway,
                AwayName = live.ManagerIsAway ? ownName : opponentName,
                HomeName = live.ManagerIsAway ? opponentName : ownName,
                AwayColors = live.ManagerIsAway
                    ? ManagerColors() : OpponentColorsOf(opponentId),
                HomeColors = live.ManagerIsAway
                    ? OpponentColorsOf(opponentId) : ManagerColors(),
                // 成績の帰属キーの校側（issue #634）。練習試合の相手校も全国ストア側で引く。
                AwaySchoolId = live.ManagerIsAway ? NationService.ManagerSchoolId : opponentId,
                HomeSchoolId = live.ManagerIsAway ? opponentId : NationService.ManagerSchoolId,
                ManagerTacticalSense = ManagerService.TacticalSenseForAi,
                RematchOnTie = false,   // 練習試合は引き分けで終わる（再試合はない）
                OnComplete = result =>
                {
                    // 大会戦（CompleteMatch）と違いブラケットのフルシムを伴わないが、実戦成長・怪我・成績畳み込みは
                    // 同じ重さなので同じ退避経路に載せる（メインスレッドを止めない, issue #138 と同型）。
                    BackgroundGameOp.Run(
                        () => GameSession.Current.CompletePracticeMatch(result),
                        () => ScreenRouter.Instance?.ShowDeferred("HomeDashboard"));
                },
            };
            ScreenRouter.Instance?.ShowDeferred("MatchLive");
            return true;
        }

        // issue #618: 旧 TeamColorsOf(int) は `schoolId < 0 ? null : ...` で弾いていたが、自校ID
        // （NationService.ManagerSchoolId）は -1 ＝「生成校と衝突しない専用ID」であって「ID不明」ではない。
        // 番兵 -1 が2つの意味に多重定義されていたため、自校の配色は常に null で返り、
        // 消費側が「両方揃わないと塗らない」設計だったので相手校の配色まで巻き添えで捨てられていた
        // （＝対決ビューが常に既定色）。呼び出し側で自校/相手校を明示的に分けて番兵の多重定義を解く。

        /// <summary>自校のユニフォーム配色（設計書18 §3.5.1）。プレイヤーの上書きがあればそれ、
        /// 無ければ校IDからの決定論割り当て。<b>必ず非nullを返す</b>。</summary>
        private static TeamColors ManagerColors() => TeamColorProvider.ManagerColors;

        /// <summary>相手校のユニフォーム配色。ID不明のときだけ null＝対決ビューがその側を既定色で描く
        /// （練習試合で相手が未確定の保険経路。大会戦の OpponentId は必ず実在する）。</summary>
        private static TeamColors OpponentColorsOf(int schoolId)
            => schoolId < 0 ? null : TeamColorProvider.For(schoolId);
    }
}
