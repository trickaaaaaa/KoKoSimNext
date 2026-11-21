using System;
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
    /// そこでブラウザ版では<b>呼んだその場で最後まで回す</b>（その間は画面が止まる＝体験版の最低限）。
    /// 区画は独立に Fork した乱数で回すので、並列でも逐次でも結果は同じ（決定論は変わらない）。</para>
    ///
    /// <para>ブラウザ版では待つ相手がいないので、休止（<see cref="Sleep"/>）と一時停止の待ち（<see cref="Threaded"/> を
    /// 条件に足す）は素通りさせる。そうしないと自分自身を待って永久に止まる。</para>
    /// </summary>
    public static class BackgroundWork
    {
        /// <summary>別スレッドで回せるか（ブラウザ版だけ false）。</summary>
        public static bool Threaded
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            get { return false; }
#else
            get { return true; }
#endif
        }

        /// <summary>仕事を回す。ブラウザ版はその場で最後まで回し、終わった Task を返す（例外は Task に包む）。</summary>
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

        /// <summary>裏の区切りで休む。ブラウザ版は休まない（待つ相手がいない）。</summary>
        public static void Sleep(int milliseconds)
        {
            if (Threaded) Thread.Sleep(milliseconds);
        }

        /// <summary>同時に回す本数。ブラウザ版は1本。</summary>
        public static int Parallelism(int wanted) => Threaded ? Math.Max(1, wanted) : 1;
    }
}
