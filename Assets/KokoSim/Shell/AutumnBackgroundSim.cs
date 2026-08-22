using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 全国47県の秋季県大会（自県以外）を「スライス駆動＋スロットル」でバックグラウンドスレッドで回す
    /// （<see cref="NationBackgroundSim"/> の秋版・設計書05 §1.5b）。夏と違い47県そのまま（分割なし）で、
    /// 各県は<b>優勝校だけでなく順位付きプレースメント</b>（<see cref="AutumnPrefectureResult"/>）を返す＝
    /// 秋季地区大会の出場枠（<see cref="RegionalFormat.BerthsFor"/>）が県内順位で決まるため。
    /// 実装は <see cref="NationBackgroundSim"/> と同型（コメントは差分のみ）。
    /// </summary>
    public static class AutumnBackgroundSim
    {
        private const int NormalSleepMs = 2;
        private const int BoostSleepMs = 2;
        private const int PausedPollMs = 16;
        private static readonly int MaxWorkers = Math.Max(1, Math.Min(3, Environment.ProcessorCount - 2));

        private static volatile int _throttle = (int)NationBackgroundSim.SimThrottle.Normal;
        private static volatile Task _task;
        private static int _done;
        private static int _nextJob;
        private static volatile int _total;
        private static int _matchesResolved;

        public static Task Current => _task;
        public static void SetThrottle(NationBackgroundSim.SimThrottle t) => _throttle = (int)t;
        public static NationBackgroundSim.SimThrottle Throttle => (NationBackgroundSim.SimThrottle)_throttle;
        public static int Done => Volatile.Read(ref _done);
        public static int Total => _total;

        public static bool Running
        {
            get { var t = _task; return t != null && !t.IsCompleted; }
        }

        public static float Progress
        {
            get { var total = _total; return total <= 0 ? 1f : Math.Min(1f, (float)Done / total); }
        }

        // 消化した県の結果（他県ぶん・自県除く）。地区大会の入力なので捨てない。
        private static readonly List<AutumnPrefectureResult> Results = new List<AutumnPrefectureResult>();
        private static readonly object ResultsGate = new object();

        /// <summary>他県の秋季県大会結果（消化済みぶん・スナップショット）。並列消化なので積まれる順は不定
        /// ＝<see cref="EnsureCompleted"/> を先に呼んで全県を確定させ、呼び出し側が県Id順へ整列すること。</summary>
        public static IReadOnlyList<AutumnPrefectureResult> PrefectureResults
        {
            get { lock (ResultsGate) return new List<AutumnPrefectureResult>(Results); }
        }

        /// <summary>他県（<paramref name="excludePrefectureId"/> 以外）の秋季県大会をスライス駆動で開始する。
        /// 自県は呼び出し側（RegionalEntry の TournamentRunner）が対話的に消化する。</summary>
        public static void Start(
            Nation nation, NationRosters rosters, NationTournamentStats stats,
            NationCoefficients coeff, TournamentSchedule schedule, int yearIndex, int calendarYear,
            int excludePrefectureId, ulong seed, IEnemyBrainFactory brains)
        {
            var rng = new Xoshiro256Random(seed ^ 0x41A2_0000UL);
            var ctx = new GameContext();
            // 他46県（自校非関与）は優勝校＋順位のみ集計モデルで即決する（夏の背景シムと同じ理由）。
            var run = AutumnNationEngine.BeginAutumn(
                nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
                excludePrefectureId: excludePrefectureId, modernRules: null, calendarYear: calendarYear,
                brains: brains, afterMatch: PauseGate, resolution: SummerResolution.ChampionsOnly);

            _throttle = (int)NationBackgroundSim.SimThrottle.Normal;
            _total = run.Total;
            Volatile.Write(ref _done, 0);
            Volatile.Write(ref _nextJob, 0);
            Volatile.Write(ref _matchesResolved, 0);
            lock (ResultsGate) Results.Clear();

            var jobs = run.Jobs;
            var workers = new Task[MaxWorkers];
            for (var w = 0; w < workers.Length; w++)
                workers[w] = Task.Run(() => Worker(jobs));
            _task = Task.WhenAll(workers);
        }

        private static void Worker(IReadOnlyList<Func<AutumnPrefectureResult>> jobs)
        {
            while (true)
            {
                PauseGate();

                var idx = Interlocked.Increment(ref _nextJob) - 1;
                if (idx >= jobs.Count) return;

                var result = jobs[idx]();
                lock (ResultsGate) Results.Add(result);
                Interlocked.Increment(ref _done);

                Thread.Sleep((NationBackgroundSim.SimThrottle)_throttle == NationBackgroundSim.SimThrottle.Boost
                    ? BoostSleepMs : NormalSleepMs);
            }
        }

        private const int CollectEveryMatches = 48;

        private static void PauseGate()
        {
            var m = Interlocked.Increment(ref _matchesResolved);
            if (m % CollectEveryMatches == 0)
                GC.Collect();
            while ((NationBackgroundSim.SimThrottle)_throttle == NationBackgroundSim.SimThrottle.Paused)
                Thread.Sleep(PausedPollMs);
        }

        /// <summary>実行中の秋季全国裏試合を完了まで待つ（自県の地区大会集約前に必ず呼ぶ）。未開始なら即返る。</summary>
        public static void EnsureCompleted()
        {
            var t = _task;
            if (t == null) return;
            _throttle = (int)NationBackgroundSim.SimThrottle.Boost;
            try { t.Wait(); }
            catch (AggregateException) { /* 個々の県の失敗は握り潰す */ }
            _task = null;
            _total = 0;
            Volatile.Write(ref _done, 0);
            _throttle = (int)NationBackgroundSim.SimThrottle.Normal;
        }
    }
}
