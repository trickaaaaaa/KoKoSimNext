using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 待つしかない操作（セーブ・ロード・タイトルへ戻る・転任・重い週送り）を、ポップアップを出したまま
    /// <b>コマをまたいで</b>段階的に進める（旧 ManualWait の置き換え。旧来は1コマ描いてからメインスレッドを丸ごと
    /// 止めていたので、点も棒も止まった絵だった）。
    ///
    /// <para>手順は <see cref="IEnumerator"/> で書く。<c>yield return</c> に渡せるもの:
    /// <list type="bullet">
    /// <item><c>null</c> — 次のコマまで待つ</item>
    /// <item><see cref="Func{Boolean}"/> — true を返すまで待つ（<see cref="WaitBackgroundIdle"/> がこれ）</item>
    /// <item><see cref="Task"/> — 終わるまで待つ（<see cref="OnWorker"/> がこれ）</item>
    /// </list>
    /// 手順の本文（yield の間）はメインスレッドで走る＝状態を集める・戻す処理はここに置く。別スレッドへ出すのは
    /// 共有状態に触らない純粋な処理（セーブの変換・圧縮・ファイルの読み書き）だけ。</para>
    ///
    /// <para>進めるのは <see cref="ScreenRouter"/> の Update（画面を切り替えても止まらない）。実行中は二重に始めない。
    /// 幕が下の操作を塞ぎ、<see cref="LoadingPopup"/> がフォーカスも外す。</para>
    /// </summary>
    public static class LoadingJob
    {
        private static IEnumerator _routine;
        private static Func<bool> _waitUntil;
        private static int _skipFrames;
        private static readonly List<(int Frames, Action Run)> Delayed = new();

        /// <summary>実行中か（二度押しや週送りの割り込みを弾くのに使う）。</summary>
        public static bool Running => _routine != null;

        /// <summary>
        /// ポップアップ（<paramref name="title"/>・<paramref name="message"/>）を出して手順を始める。既に何か走っていれば
        /// 何もせず false（手順は呼ばない）。最初の1歩は、ポップアップを描いた次のコマから進める。
        /// </summary>
        /// <param name="immediate">ポップアップを遅延（<see cref="LoadingPopup.ShowDelaySeconds"/>）を待たずに出すか。
        /// 段の途中でメインスレッドを止める操作（セーブ・ロード・重い節目の週送り）は true（オーナー確認 2026-11-28）。
        /// すぐ終わり得る操作（止まるまで進めるで1歩で止まる等）は false にしてちらつきを防ぐ。</param>
        public static bool Run(string title, string message, IEnumerator routine, bool immediate = true)
        {
            if (Running || BackgroundGameOp.Running || routine == null) return false;
            _routine = routine;
            _waitUntil = null;
            _skipFrames = 2;   // 押したコマと描画のコマを越えてから始める＝止まる段に入る前に絵が出ている
            LoadingPopup.Show(title, message, immediate);
            return true;
        }

        /// <summary>裏で走る全シム（<see cref="BackgroundSims"/>）が止まるまで待つ条件。待つ間はコマが回る＝絵が動く。
        /// 全国裏試合は待っている間だけ全開で消化させる。ブラウザ版は回し切るまで裏の仕事を多めに回す。</summary>
        public static Func<bool> WaitBackgroundIdle()
        {
            if (!BackgroundWork.Threaded)
            {
                // ブラウザ版: スレッドが無い＝ BackgroundWork.Pump が回し切った時点で合図する。
                var done = false;
                BackgroundWork.WhenIdle(() => done = true);
                return () => done;
            }
            return () =>
            {
                if (NationBackgroundSim.Running) NationBackgroundSim.SetThrottle(NationBackgroundSim.SimThrottle.Boost);
                return !BackgroundSims.AnyRunning;
            };
        }

        /// <summary>
        /// <paramref name="work"/> を別スレッドで走らせる Task（ブラウザ版はその場で走って終わった Task）。
        /// 共有状態に触らない純粋な処理だけを渡すこと。例外は手順の側で <c>Task.Exception</c> を見る。
        /// </summary>
        public static Task OnWorker(Action work) => BackgroundWork.Run(work);

        /// <summary><paramref name="frames"/> コマ後に <paramref name="action"/> をメインスレッドで走らせる
        /// （ポップアップを1度描いてからメインスレッドを止める処理へ入るのに使う）。</summary>
        public static void AfterFrames(int frames, Action action)
        {
            if (action != null) Delayed.Add((Math.Max(1, frames), action));
        }

        /// <summary>1コマ進める（<see cref="ScreenRouter"/> の Update から毎コマ呼ぶ）。</summary>
        public static void Tick()
        {
            TickDelayed();
            if (_routine == null) return;
            if (_skipFrames > 0) { _skipFrames--; return; }
            if (_waitUntil != null)
            {
                if (!_waitUntil()) return;
                _waitUntil = null;
            }

            bool more;
            try { more = _routine.MoveNext(); }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                more = false;
            }
            if (!more)
            {
                _routine = null;
                LoadingPopup.Hide();
                return;
            }
            _waitUntil = _routine.Current switch
            {
                Func<bool> f => f,
                Task t => () => t.IsCompleted,
                _ => null,
            };
        }

        private static void TickDelayed()
        {
            for (var i = Delayed.Count - 1; i >= 0; i--)
            {
                var (frames, run) = Delayed[i];
                if (frames > 1) { Delayed[i] = (frames - 1, run); continue; }
                Delayed.RemoveAt(i);
                try { run(); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
        }

        /// <summary>テスト用: 走っている手順を捨てる。</summary>
        internal static void ResetForTests()
        {
            _routine = null;
            _waitUntil = null;
            _skipFrames = 0;
            Delayed.Clear();
            LoadingPopup.ResetForTests();
        }
    }
}
