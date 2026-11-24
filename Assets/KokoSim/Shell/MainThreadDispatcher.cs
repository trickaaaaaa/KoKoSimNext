using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// 実行中は <see cref="Running"/> が立ち（スロットの Task が未完了の間＝<c>Task.IsCompleted</c> 由来）、二重起動と「メインスレッドからの GameClock/ロスター変更」の
    /// 割り込みを弾く（背景処理と同じ静的状態を同時に触らせない＝決定論・整合性を守る）。スロットの完了は
    /// メインスレッドの継続（Drain）内で行うため、UI が処理完了を反映するまで Running は立ったままになる。
    ///
    /// <para>見張り（Q30・issue #1317）: <paramref name="work"/> が戻らない（無限ループ・デッドロック）と Running が
    /// 立ちっぱなしになる。<see cref="StallLimitSeconds"/> を超えたら <see cref="Stalled"/> が立ち、
    /// <see cref="CheckStall"/> がログを1度だけ出す（画面は <c>WaitOverlayController</c> が案内する）。
    /// <b>スロットは強制解放しない</b>＝裏で走り続ける work と次の処理が同じ静的状態を触るのを避けるため
    /// （オーナー判断・案B：知らせるだけ）。</para>
    /// </summary>
    public static class BackgroundGameOp
    {
        /// <summary>これを超えて戻らなければ固まりとみなす秒数。最悪の既知の1本は試合開始前の自県フルシム約25秒
        /// （Mono 実測・先読み導入前）で、その約5倍。大会日の全国裏試合は別系統（NationBackgroundSim）なのでここに載らない。</summary>
        public const double StallLimitSeconds = 120.0;

        // スロット。Running は NationBackgroundSim / BracketFinishSim と同じく Task.IsCompleted 由来にして、
        // 解除をフラグの書き込みではなく Task の完了に一本化する（Q30 の3）。
        private static readonly object SlotGate = new object();
        private static TaskCompletionSource<bool> _slot;
        private static double _startedAt;
        private static bool _stallLogged;

        /// <summary>現在の秒。既定は単調増加の実時間（Unity 非依存）。テストが差し替える。</summary>
        internal static Func<double> Clock = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>重い試合後処理が進行中か（メインスレッドの週送り等が割り込まないよう参照する）。</summary>
        public static bool Running
        {
            get { var slot = Volatile.Read(ref _slot); return slot != null && !slot.Task.IsCompleted; }
        }

        /// <summary>実行中の work が <see cref="StallLimitSeconds"/> を超えて戻っていないか。</summary>
        public static bool Stalled => Running && Clock() - _startedAt > StallLimitSeconds;

        /// <summary>固まりを初めて検知した瞬間だけログを出して true を返す（ScreenRouter.Update から毎フレーム呼ぶ）。</summary>
        public static bool CheckStall()
        {
            if (_stallLogged || !Stalled) return false;
            _stallLogged = true;
            UnityEngine.Debug.LogError(
                "BackgroundGameOp が " + StallLimitSeconds + " 秒を超えて戻りません（固まり）。スロットは解放しません。セーブして再起動してください。");
            return true;
        }

        /// <summary>
        /// <paramref name="work"/>（engine の逐次処理）を別スレッドで実行し、完了後 <paramref name="onDone"/> を
        /// メインスレッドで呼ぶ。既に実行中なら false を返して何もしない（二重起動防止）。
        /// </summary>
        public static bool Run(Action work, Action onDone)
        {
            var slot = new TaskCompletionSource<bool>();
            lock (SlotGate)
            {
                if (Running) return false;
                Volatile.Write(ref _slot, slot);
            }
            _startedAt = Clock();
            _stallLogged = false;
            if (BackgroundWork.Busy)
            {
                // ブラウザ版で裏の仕事が残っている（#1588）。スロットは先に取る＝二度押し・週送りを弾き、待ち画面を出す。
                // work は裏の仕事をコマをまたいで回し切ってから始める＝work の中の「終わるまで待つ」が1コマで
                // 数百試合を回してメモリを使い切るのを避ける。
                BackgroundWork.WhenIdle(() =>
                {
                    _startedAt = Clock();   // 見張りの秒は work を始めてから数える
                    Start(slot, work, onDone);
                });
                return true;
            }
            Start(slot, work, onDone);
            return true;
        }

        private static void Start(TaskCompletionSource<bool> slot, Action work, Action onDone)
        {
            try
            {
                BackgroundWork.Run(work).ContinueWith(t =>
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        slot.TrySetResult(true);   // onDone より前に解く＝onDone の中から次の Run を始められる
                        if (t.Exception != null) UnityEngine.Debug.LogException(t.Exception.GetBaseException());
                        onDone?.Invoke();
                    }), TaskContinuationOptions.ExecuteSynchronously);   // ブラウザ版は Run がその場で終わる＝継続もその場で（スレッドプールは回らない）
            }
            catch
            {
                slot.TrySetResult(true);
                throw;
            }
        }
    }
}
