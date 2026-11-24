using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 裏の仕事（全国の裏試合・成績の打ち直し・先読み・弾道表の準備）を回す唯一の入口（Web 体験版 #1323）。
    ///
    /// <para>Windows/Mac/Editor では従来どおり別スレッド（<see cref="Task.Run(Action)"/>）。
    /// ブラウザ版はスレッドが無く、<c>Task.Run</c> に渡した仕事が<b>いつまでも始まらない</b>
    /// （2026-11-21 の Web お試しビルドで「他会場の結果を集計中」のバーが 0 のまま止まった）。
    /// 区画は独立に Fork した乱数で回すので、並列でも逐次でも、どこで区切っても結果は同じ（決定論は変わらない）。</para>
    ///
    /// <para><b>ブラウザ版は重い仕事を1コマで回し切らない（#1588）</b>。ブラウザ版の Unity は GC を
    /// コマの切れ目（スタックが空のとき）にしか回せず、処理の途中の <c>GC.Collect()</c> も効かない。
    /// 数百試合を1コマで回すと確保が回収されないまま積み上がり、メモリの上限（2GB）に届いて落ちた
    /// （夏の県大会で負けた後の［次へ］→甲子園代表48校の県予選の打ち直し）。そこで重い裏の仕事は
    /// <see cref="RunSteps"/>（1歩＝1区画）で渡し、ブラウザ版では <see cref="Pump"/> が毎フレーム時間を区切って回す。
    /// 画面の操作の起点（<see cref="BackgroundGameOp"/>・週送り）は <see cref="WhenIdle"/> で回し切るまで待ってから走る
    /// ＝その先の「終わるまで待つ」（<see cref="Wait"/>）がその場で大量に回すことを避ける。</para>
    ///
    /// <para>ブラウザ版では待つ相手がいないので、休止（<see cref="Sleep"/>）と一時停止の待ち（<see cref="Threaded"/> を
    /// 条件に足す）は素通りさせる。そうしないと自分自身を待って永久に止まる。</para>
    /// </summary>
    public static class BackgroundWork
    {
        /// <summary>ふだんのコマで回す時間（ミリ秒）。画面を動かしながら裏で進める。</summary>
        public const double FrameBudgetMs = 12.0;

        /// <summary>操作が終わりを待っているときのコマで回す時間（ミリ秒）。待ち画面を出しているので多めに回す。
        /// それでもコマは返す＝コマの切れ目で GC が走る。</summary>
        public const double WaitingBudgetMs = 50.0;

#if UNITY_EDITOR
        /// <summary>Editor のテストでブラウザ版の回し方を真似る（スレッドを使わず、<see cref="Pump"/> で回す）。</summary>
        internal static bool SimulateBrowser;
#endif

        private sealed class SteppedJob
        {
            public Func<bool> Step;
            public readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>();
        }

        // ブラウザ版の歩み待ちの仕事（メインスレッドだけが触る）。
        private static readonly List<SteppedJob> Jobs = new List<SteppedJob>();
        // 回し切った後に走らせる操作（メインスレッドだけが触る）。
        private static readonly Queue<Action> IdleActions = new Queue<Action>();

        /// <summary>別スレッドで回せるか（ブラウザ版だけ false）。</summary>
        public static bool Threaded
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            get { return false; }
#elif UNITY_EDITOR
            get { return !SimulateBrowser; }
#else
            get { return true; }
#endif
        }

        /// <summary>ブラウザ版で、まだ回し切っていない裏の仕事があるか（スレッドで回す版は常に false）。</summary>
        public static bool Busy => !Threaded && Jobs.Count > 0;

        /// <summary>仕事を回す。ブラウザ版はその場で最後まで回し、終わった Task を返す（例外は Task に包む）。
        /// 何百試合も回す重い仕事には使わない（<see cref="RunSteps"/> を使う）。</summary>
        public static Task Run(Action work)
        {
            if (Threaded) return Task.Run(work);
            try
            {
                work();
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        /// <summary>
        /// 1歩ずつ進む仕事を回す。<paramref name="step"/> は1区画ぶん進めて、まだ続きがあれば true を返す。
        /// スレッドで回す版は別スレッドで false まで回す。ブラウザ版は <see cref="Pump"/> が毎フレーム少しずつ回す。
        /// </summary>
        public static Task RunSteps(Func<bool> step)
        {
            if (Threaded) return Task.Run(() => { while (step()) { } });
            var job = new SteppedJob { Step = step };
            Jobs.Add(job);
            return job.Done.Task;
        }

        /// <summary>
        /// ブラウザ版の歩み待ちの仕事を、<paramref name="budgetMs"/> だけ回す（ScreenRouter.Update から毎フレーム呼ぶ）。
        /// ライブ観戦中（Paused）は回さない＝観戦の滑らかさを守る。ただし操作が終わりを待っているときは回す。
        /// 回し切ったら <see cref="WhenIdle"/> で待たせていた操作を走らせる。
        /// </summary>
        public static void Pump(double? budgetMs = null)
        {
            if (Threaded) return;
            var waiting = IdleActions.Count > 0;
            if (Jobs.Count > 0 && (waiting || NationBackgroundSim.Throttle != NationBackgroundSim.SimThrottle.Paused))
            {
                var budget = budgetMs ?? (waiting ? WaitingBudgetMs : FrameBudgetMs);
                var sw = Stopwatch.StartNew();
                do StepOnce(Jobs[0]);
                while (Jobs.Count > 0 && sw.Elapsed.TotalMilliseconds < budget);
            }
            while (Jobs.Count == 0 && IdleActions.Count > 0)
            {
                var action = IdleActions.Dequeue();
                try { action(); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
        }

        /// <summary>
        /// 裏の仕事を回し切ってから <paramref name="action"/> を走らせる。スレッドで回す版・待つ仕事が無いときはその場で走らせる。
        /// ブラウザ版で待つ仕事があるときは積んでおき、<see cref="Pump"/> が回し切った後に走らせる。
        /// </summary>
        public static void WhenIdle(Action action)
        {
            if (action == null) return;
            if (!Busy && IdleActions.Count == 0) { action(); return; }
            IdleActions.Enqueue(action);
        }

        /// <summary>回し切るのを待っている操作があるか（待ち画面を出す合図）。</summary>
        public static bool HasWaiters => IdleActions.Count > 0;

        /// <summary>
        /// 画面の操作（週送り等）の入口で呼ぶ。ブラウザ版で裏の仕事が残っていれば <paramref name="action"/> を
        /// 回し切った後へ回して true を返す（呼び元はそのまま戻る）。同じ操作は二重に積まない（連打しても1回）。
        /// 待つ仕事が無ければ false（呼び元はそのまま続ける）。
        /// </summary>
        public static bool DeferUntilIdle(Action action)
        {
            if (!Busy || action == null) return false;
            if (!IdleActions.Contains(action)) IdleActions.Enqueue(action);
            return true;
        }

        /// <summary>
        /// <paramref name="task"/> が終わるまで待つ。スレッドで回す版は普通に待つ。ブラウザ版は待つ相手がいないので、
        /// 終わるまで<b>その場で</b>歩みを回す（その間 GC は回らない＝なるべく <see cref="WhenIdle"/> で手前に回し切る）。
        /// 例外は <see cref="Task.Wait()"/> と同じく <see cref="AggregateException"/> で投げる。
        /// </summary>
        public static void Wait(Task task)
        {
            if (task == null) return;
            if (!Threaded)
                while (!task.IsCompleted && Jobs.Count > 0) StepOnce(Jobs[0]);
            task.Wait();
        }

        private static void StepOnce(SteppedJob job)
        {
            bool more;
            try { more = job.Step(); }
            catch (Exception e)
            {
                Jobs.Remove(job);
                job.Done.TrySetException(e);
                return;
            }
            if (more) return;
            Jobs.Remove(job);
            job.Done.TrySetResult(true);
        }

        /// <summary>裏の区切りで休む。ブラウザ版は休まない（待つ相手がいない）。</summary>
        public static void Sleep(int milliseconds)
        {
            if (Threaded) Thread.Sleep(milliseconds);
        }

        /// <summary>同時に回す本数。ブラウザ版は1本。</summary>
        public static int Parallelism(int wanted) => Threaded ? Math.Max(1, wanted) : 1;

#if UNITY_EDITOR
        /// <summary>テストの後始末。残った歩み・操作を捨てる。</summary>
        internal static void ResetForTests()
        {
            Jobs.Clear();
            IdleActions.Clear();
        }
#endif
    }
}
