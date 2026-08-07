using System;
using System.Threading;
using System.Threading.Tasks;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校敗退直後、残りブラケット（自校非関与の裏カード）を「スライス駆動」でバックグラウンドスレッドで
    /// 消化する（issue #529）。旧 TournamentRunner.FinishBracketInBackground は名前に反して同期の while
    /// ループで、一回戦負け（残り試合数が最大）のとき試合結果画面の「閉じる」を押しても長時間ホームへ
    /// 遷移しなかった（BackgroundGameOp.Run の work がこのループを含んだまま完了まで戻らなかったため）。
    ///
    /// <see cref="NationBackgroundSim"/>（#208）と同型: TournamentRunner.StepBracketResolution を1ラウンド
    /// ずつ呼び、合間で NationBackgroundSim.Throttle（ライブ試合中は Paused）を見て小休止する。エンジンは
    /// Unity 非依存でスレッド安全＝背景スレッドから安全に呼べる。完了したら onComplete をメインスレッドへ
    /// 積んで呼ぶ（FoldTournament の確定は GameSession 側の責務）。
    /// </summary>
    public static class BracketFinishSim
    {
        private const int StepSleepMs = 2;     // ラウンド間でメインへ時間を譲る（#208 と同じ作法）。
        private const int PausedPollMs = 16;

        private static volatile Task _task;
        private static int _done;            // Interlocked。消化済みラウンド数（進捗バーの分子）。
        private static volatile int _total;  // 敗退時点の残りラウンド数（進捗バーの母数）。

        /// <summary>背景消化がまだ走っているか（進捗オーバーレイの表示判定に使う）。</summary>
        public static bool Running
        {
            get { var t = _task; return t != null && !t.IsCompleted; }
        }

        /// <summary>消化済みラウンド数。</summary>
        public static int Done => Volatile.Read(ref _done);

        /// <summary>敗退時点の残りラウンド数（0＝未開始）。</summary>
        public static int Total => _total;

        /// <summary>進捗（0..1）。未開始・母数0のときは 1（＝完了扱いで表示しない）。</summary>
        public static float Progress
        {
            get { var total = _total; return total <= 0 ? 1f : Math.Min(1f, (float)Done / total); }
        }

        /// <summary>
        /// 自校敗退直後に呼ぶ。<paramref name="runner"/> の残りブラケットをラウンド単位で背景消化し、
        /// 完了したら <paramref name="onComplete"/> をメインスレッドで呼ぶ（<see cref="MainThreadDispatcher"/> 経由）。
        /// 既に消化不要（優勝時・現役時）なら即座に <paramref name="onComplete"/> を呼んで返る。
        /// </summary>
        public static void Start(TournamentRunner runner, Action onComplete)
        {
            if (runner == null || !runner.BracketResolutionPending)
            {
                onComplete?.Invoke();
                return;
            }

            _total = runner.RemainingBracketRounds;
            Volatile.Write(ref _done, 0);
            _task = Task.Run(() => Worker(runner)).ContinueWith(_ =>
                MainThreadDispatcher.Enqueue(() =>
                {
                    _task = null;
                    _total = 0;
                    onComplete?.Invoke();
                }));
        }

        private static void Worker(TournamentRunner runner)
        {
            while (runner.BracketResolutionPending)
            {
                // ライブ試合中（他画面で練習試合の観戦等）は新しいラウンドへ進まず待つ（#208 と同じ作法）。
                while (NationBackgroundSim.Throttle == NationBackgroundSim.SimThrottle.Paused)
                    Thread.Sleep(PausedPollMs);

                runner.StepBracketResolution();
                Interlocked.Increment(ref _done);
                Thread.Sleep(StepSleepMs);   // 区画間で必ずメインスレッドへ時間を譲る。
            }
        }

        /// <summary>
        /// 残りブラケット消化を完了まで待つ（年度またぎ・AI校の代替わり等、ロスター変更の前に必ず呼ぶ。
        /// 背景消化とロスター変更が重ならないようにするため）。未開始なら即返る。
        /// </summary>
        public static void EnsureCompleted()
        {
            var t = _task;
            if (t == null) return;
            try { t.Wait(); }
            catch (AggregateException) { /* 個々の失敗は握り潰す（大会全体は継続。詳細はログ層で） */ }
            _task = null;
            _total = 0;
        }
    }
}
