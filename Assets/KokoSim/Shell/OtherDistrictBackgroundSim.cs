using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校が居ない地区の秋季地区予選を、自校の地区予選を遊んでいる間に背景スレッドで1球フルシムする
    /// （issue #1106）。勝ち上がった相手校の選手成績を全国の成績帳に積むため。
    ///
    /// <para><b>成績は背景で積まない</b>。解いた全試合を溜めておき、地区予選が終わった時点で
    /// <see cref="FoldInto"/> がメインスレッドで1回だけ積む。結果はセーブに載らず、同じシードで回し直せば
    /// 同じ結果になる＝ロード復帰は <see cref="Start"/> で回し直すだけでよく、積み直しの二重計上が起きない
    /// （成績はセーブから復元済みで、ここで積むのは「まだ積んでいない」ときだけ）。</para>
    /// </summary>
    public static class OtherDistrictBackgroundSim
    {
        private const int PausedPollMs = 16;
        private const int StepSleepMs = 2;
        // 地区ごとに1本（神奈川で6地区・237試合＝直列で約37秒、1地区4〜8秒・エディタ実測）。2本に絞ると全地区が揃うまで
        // 約24秒かかり、自校の地区予選を早く終えると県大会へ入る瞬間にその分止まる。本数は地区数で頭打ち（Start）。
        // ライブ観戦中は PauseGate で止まるので、観戦の滑らかさには響かない。
        private static readonly int MaxWorkers = BackgroundWork.Parallelism(Math.Max(1, Environment.ProcessorCount - 2));

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, OtherDistrictResult> Results = new Dictionary<int, OtherDistrictResult>();
        private static Task _task;
        private static ulong _key;
        private static bool _keySet;
        // Start のたびに進める。古い走りの結果が、新しい走りの結果へ混ざらないようにする。
        private static int _generation;
        private static bool _folded;
        // 待つ側（メインスレッド）が立てる。ライブ観戦の停止指示（Paused）が掛かったまま待つと詰むため。
        private static volatile bool _forceRun;

        /// <summary>この大会（<paramref name="key"/>）の他地区ぶんが手元にある（消化済み or 消化中）か。</summary>
        public static bool IsFor(ulong key)
        {
            lock (Gate) return _keySet && _key == key;
        }

        /// <summary>
        /// 他地区の消化を始める。<paramref name="jobs"/> は地区番号→解く処理。結果の順序に依存しないよう、
        /// 各 job は地区ごとに独立のシードで解くこと。前の走りが残っていても結果は混ざらない。
        /// </summary>
        public static void Start(ulong key, IReadOnlyList<KeyValuePair<int, Func<OtherDistrictResult>>> jobs)
        {
            int gen;
            lock (Gate)
            {
                gen = ++_generation;
                Results.Clear();
                _key = key;
                _keySet = true;
                _folded = false;
                _forceRun = false;
            }

            var next = -1;
            var workers = new Task[Math.Min(MaxWorkers, Math.Max(1, jobs.Count))];
            for (var w = 0; w < workers.Length; w++)
            {
                workers[w] = BackgroundWork.Run(() =>
                {
                    while (true)
                    {
                        PauseGate();
                        var idx = Interlocked.Increment(ref next);
                        if (idx >= jobs.Count) return;
                        lock (Gate) if (gen != _generation) return;

                        var result = jobs[idx].Value();
                        lock (Gate) if (gen == _generation) Results[jobs[idx].Key] = result;
                        BackgroundWork.Sleep(StepSleepMs);
                    }
                });
            }
            lock (Gate) _task = Task.WhenAll(workers);
        }

        /// <summary>試合と試合の合間に呼ぶ。ライブ観戦中（Paused）は止まって2D再生の滑らかさを守る。</summary>
        public static void PauseGate()
        {
            while (BackgroundWork.Threaded && !_forceRun && NationBackgroundSim.Throttle == NationBackgroundSim.SimThrottle.Paused)
                BackgroundWork.Sleep(PausedPollMs);
        }

        /// <summary>消化を完了まで待って、地区番号→結果を返す。手元の結果が <paramref name="key"/> の大会のもの
        /// でなければ（未開始・前年の走りが残っている）空を返す＝前年の結果を今年へ混ぜない。</summary>
        public static IReadOnlyDictionary<int, OtherDistrictResult> WaitForResults(ulong key)
        {
            Task t;
            lock (Gate)
            {
                if (!_keySet || _key != key) return new Dictionary<int, OtherDistrictResult>();
                t = _task;
            }
            if (t != null)
            {
                _forceRun = true;
                try { t.Wait(); }
                catch (AggregateException e) { UnityEngine.Debug.LogException(e.GetBaseException()); }   // 個々の地区の失敗は握り潰す（欠けた地区は呼び出し側が扱う）が、ログは出す（issue #1226）
            }
            lock (Gate) return new Dictionary<int, OtherDistrictResult>(Results);
        }

        /// <summary>
        /// 溜めた全試合を <paramref name="stats"/> へ積む。<b>1回だけ</b>（二度目以降は何もしない）。
        /// 呼ぶ前に <see cref="WaitForResults"/> で全地区が揃っていること。
        /// </summary>
        public static void FoldInto(NationTournamentStats stats, IReadOnlyDictionary<int, OtherDistrictResult> results)
        {
            lock (Gate)
            {
                if (_folded) return;
                _folded = true;
            }
            foreach (var d in results.Keys.OrderBy(k => k))
                foreach (var g in results[d].Games)
                    stats.FoldMatch(g.AwayId, g.HomeId, g.Game);
        }
    }
}
