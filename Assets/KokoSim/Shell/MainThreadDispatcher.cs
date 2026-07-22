using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// バックグラウンド Task の完了を Unity メインスレッドへ戻す唯一のディスパッチャ（issue #138）。
    /// engine は単一スレッド逐次のまま（＝決定論を維持, 不変条件#2）で、重い試合後処理だけを別スレッドで回し、
    /// その完了を受けた UI 反映（画面遷移・再描画）をこのキュー経由でメインスレッドに載せ替える。
    /// <see cref="ScreenRouter"/> の Update が毎フレーム <see cref="Drain"/> する。
    /// </summary>
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        /// <summary>メインスレッドで実行したいアクションを積む（任意スレッドから安全に呼べる）。</summary>
        public static void Enqueue(Action action)
        {
            if (action != null) Queue.Enqueue(action);
        }

        /// <summary>積まれたアクションをメインスレッドで順に実行する（ScreenRouter.Update から毎フレーム呼ぶ）。</summary>
        public static void Drain()
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
        }
    }

    /// <summary>
    /// 重い試合後処理（ブラケットのフルシム＋全国背景シムへの join）をメインスレッドから外して実行する
    /// 小さなヘルパ（issue #138・オーナー判断 Q1(a)）。<paramref name="work"/> を <see cref="Task.Run"/> で
    /// 回し、完了後に <paramref name="onDone"/> を <see cref="MainThreadDispatcher"/> 経由でメインスレッドで呼ぶ。
    ///
    /// 実行中は <see cref="Running"/> を立て、二重起動と「メインスレッドからの GameClock/ロスター変更」の
    /// 割り込みを弾く（背景処理と同じ静的状態を同時に触らせない＝決定論・整合性を守る）。フラグの解除は
    /// メインスレッドの継続（Drain）内で行うため、UI が処理完了を反映するまで Running は立ったままになる。
    /// </summary>
    public static class BackgroundGameOp
    {
        private static int _running;   // 0=idle, 1=running

        /// <summary>重い試合後処理が進行中か（メインスレッドの週送り等が割り込まないよう参照する）。</summary>
        public static bool Running => Volatile.Read(ref _running) != 0;

        /// <summary>
        /// <paramref name="work"/>（engine の逐次処理）を別スレッドで実行し、完了後 <paramref name="onDone"/> を
        /// メインスレッドで呼ぶ。既に実行中なら false を返して何もしない（二重起動防止）。
        /// </summary>
        public static bool Run(Action work, Action onDone)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
            Task.Run(work).ContinueWith(t =>
                MainThreadDispatcher.Enqueue(() =>
                {
                    Volatile.Write(ref _running, 0);
                    if (t.Exception != null) UnityEngine.Debug.LogException(t.Exception.GetBaseException());
                    onDone?.Invoke();
                }));
            return true;
        }
    }
}
