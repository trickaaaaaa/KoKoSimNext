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
        // 手元の結果がどの大会のものか（ResultsGate で保護。未設定＝無し）。
        // **年ではなくシードで刻む**＝シードは母種と年から導出されるので、去年の結果だけでなく
        // 「別スロットの同じ年」の結果も弾ける（母種が違えば全国の学校そのものが別物になる）。
        private static ulong _resultsSeed;
        private static bool _resultsSeedSet;
        // 消化中に GC を挟むか。ゲームプレイ中の背景消化ではフレームを守るために挟むが、
        // ロード復帰の追いつき実行（47県で実測80ms）では 1 回の Gen2 GC の方が高くつくので挟まない。
        private static volatile bool _collectGarbage = true;

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
        //
        // ⚠ セーブには載らない（47県ぶんの順位は大きい）。かわりに**同じシードで回し直せば同じ結果が出る**
        //   ことに依存する（IRandomSource.Fork は元の乱数源を進めず、各県は県IDから純粋に導出される
        //   ＝3スレッドが県を奪い合っても消化順に依存しない）。ロード復帰で積み直すのは
        //   TournamentEntry.RestoreRunner の役目で、これを怠ると地区大会が自県ぶんだけの
        //   「2校の九州大会」になる（2040年秋に実機発症・issue autumn-regional-field-collapses-after-load）。
        private static readonly List<AutumnPrefectureResult> Results = new List<AutumnPrefectureResult>();
        private static readonly object ResultsGate = new object();

        /// <summary>他県の秋季県大会結果（消化済みぶん・スナップショット）。並列消化なので積まれる順は不定
        /// ＝<see cref="EnsureCompleted"/> を先に呼んで全県を確定させ、呼び出し側が県Id順へ整列すること。</summary>
        public static IReadOnlyList<AutumnPrefectureResult> PrefectureResults
        {
            get { lock (ResultsGate) return new List<AutumnPrefectureResult>(Results); }
        }

        /// <summary>
        /// この大会（<paramref name="seed"/>）の他県ぶんが手元にある（消化済み or 消化中）か。
        /// <see cref="Start"/> を二重に呼ばないための判定。
        /// <b>シードで判定する</b>のが要点＝結果はセーブに載らず static に残り続けるので、件数だけを見ると
        /// 去年の結果や別スロットの結果を今年の地区大会へそのまま流し込んでしまう。
        /// </summary>
        public static bool HasResultsFor(ulong seed)
        {
            lock (ResultsGate)
            {
                if (!_resultsSeedSet || _resultsSeed != seed) return false;
                return Running || Results.Count > 0;
            }
        }

        /// <summary>他県（<paramref name="excludePrefectureId"/> 以外）の秋季県大会をスライス駆動で開始する。
        /// 自県は呼び出し側（RegionalEntry の TournamentRunner）が対話的に消化する。
        /// <paramref name="collectGarbage"/> false＝消化中に GC を挟まない（ロード復帰の追いつき実行）。</summary>
        public static void Start(
            Nation nation, NationRosters rosters, NationTournamentStats stats,
            NationCoefficients coeff, TournamentSchedule schedule, int yearIndex, int calendarYear,
            int excludePrefectureId, ulong seed, IEnemyBrainFactory brains, bool collectGarbage = true)
        {
            var rng = new Xoshiro256Random(seed ^ 0x41A2_0000UL);
            var ctx = new GameContext();
            // 他46県（自校非関与）は優勝校＋順位のみ集計モデルで即決する（夏の背景シムと同じ理由）。
            var run = AutumnNationEngine.BeginAutumn(
                nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
                excludePrefectureId: excludePrefectureId, modernRules: null, calendarYear: calendarYear,
                brains: brains, afterMatch: PauseGate, resolution: SummerResolution.ChampionsOnly,
                // 県大会の汎用ティア球場（issue #636）。いまは集計モデル（ChampionsOnly）なので球場は使われないが、
                // 1球フルシムへ切り替えたとき自県の裏試合・自校の試合と同じ球場になるよう立てておく。
                useRegionalStadiumTier: true);

            _throttle = (int)NationBackgroundSim.SimThrottle.Normal;
            _collectGarbage = collectGarbage;
            _total = run.Total;
            Volatile.Write(ref _done, 0);
            Volatile.Write(ref _nextJob, 0);
            Volatile.Write(ref _matchesResolved, 0);
            // 「この大会ぶんを今から積む」刻印。Clear と同じロックで立てる＝HasResultsFor が
            // 「空にした直後・刻印前」の隙間を見て取り違えることが無い。
            lock (ResultsGate)
            {
                Results.Clear();
                _resultsSeed = seed;
                _resultsSeedSet = true;
            }

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
            if (_collectGarbage && m % CollectEveryMatches == 0)
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
