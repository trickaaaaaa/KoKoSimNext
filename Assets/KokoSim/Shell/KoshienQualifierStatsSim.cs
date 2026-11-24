using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 甲子園代表校の「県予選成績」を背景スレッドで作り直す（設計書05 §1.4b）。
    /// <see cref="NationBackgroundSim"/> と同型のスライス駆動＋スロットルで、1スライス＝代表1校。
    ///
    /// <para><b>なぜ要るか</b>: 他県の地方大会は <see cref="SummerResolution.ChampionsOnly"/>（集計モデル）で
    /// 優勝校だけを決めており、選手個人の成績を1つも積んでいない（全県フルシムは背景約5分＝#208 で削った負荷）。
    /// 打ち直すのは<b>代表が実際に勝ち上がった道のりだけ</b>なので、49校×約6試合＝約300試合で済む
    /// （全カード約4,000試合の 1/13）。</para>
    ///
    /// <para>判定は engine の <see cref="QualifierStatsSim"/> にあり、ここは「いつ・どの順で・どれだけの
    /// ペースで回すか」だけを持つ（不変条件#3 エンジン純度）。乱数は専用 Fork＝本編の乱数列は不変。</para>
    /// </summary>
    public static class KoshienQualifierStatsSim
    {
        private const int StepSleepMs = 2;      // 校の合間でメインへ時間を譲る（#208 と同じ作法）。
        private const int PausedPollMs = 16;
        // 何試合ごとに GC を挟むか（#208 真因対策）。1試合あたり約550KB churn するので、放置すると
        // Mono(Boehm) のヒープが膨らんで以後の GC が重くなる。約300試合なので刻みは粗くてよい。
        private const int CollectEveryMatches = 48;

        // 静止画面で回す並列ワーカー数。校ごとに独立 Fork なので並列でも決定論不変（#208 と同じ根拠）。
        private static readonly int MaxWorkers = BackgroundWork.Parallelism(Math.Max(1, Math.Min(3, Environment.ProcessorCount - 2)));

        private static volatile Task _task;
        private static int _done;            // Interlocked。打ち直し済みの校数（進捗の分子）。
        private static int _nextJob;         // Interlocked。次に取る校の添字（ワーカーが奪い合う）。
        private static volatile int _total;  // 対象の校数（進捗の母数）。
        private static int _matchesResolved; // Interlocked。解決した累計試合数（定期GCの分母）。

        /// <summary>背景の打ち直しがまだ走っているか。</summary>
        public static bool Running
        {
            get { var t = _task; return t != null && !t.IsCompleted; }
        }

        /// <summary>打ち直し済みの校数。</summary>
        public static int Done => Volatile.Read(ref _done);

        /// <summary>対象の校数（0＝未開始）。</summary>
        public static int Total => _total;

        /// <summary>進捗（0..1）。未開始・母数0のときは 1（＝完了扱いで表示しない）。</summary>
        public static float Progress
        {
            get { var total = _total; return total <= 0 ? 1f : Math.Min(1f, (float)Done / total); }
        }

        /// <summary>
        /// 代表校の県予選成績の打ち直しを開始する（甲子園に入った直後に1回）。
        /// 既に実測がある校（自県）は <see cref="QualifierStatsSim.PendingRepresentatives"/> が除く。
        /// 対象が無ければ何もしない。
        /// </summary>
        public static void Start(IEnumerable<PrefectureResult> results, int yearIndex, ulong seed)
        {
            // ブラウザ版は打ち直さない（約300試合＝1コアでは夏の敗退後が長く止まる・#1588 オーナー判断）。
            // 読み物（大会展望の他県代表の県予選成績）が空欄になるだけで、進行・結果は変わらない。
            if (!BackgroundWork.RunsReplays) return;
            EnsureCompleted();   // 前年ぶんが残っていたら先に畳む（同じ帳簿を2本の走りが触らない）

            var stats = NationService.TournamentStats;
            var pending = QualifierStatsSim.PendingRepresentatives(results, stats);
            if (pending.Count == 0) return;

            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            var rng = new Xoshiro256Random(seed ^ QualifierStatsSim.StreamId);
            var brains = new EnemyAiBrainFactory();

            _total = pending.Count;
            Volatile.Write(ref _done, 0);
            Volatile.Write(ref _nextJob, 0);
            Volatile.Write(ref _matchesResolved, 0);

            var workers = new Task[MaxWorkers];
            for (var w = 0; w < workers.Length; w++)
            {
                // 解決器はワーカーごとに新しく作る＝BackgroundMatchResolver は状態を持たないが、
                // 同じ実体を複数スレッドで共有する理由も無いので取り違えの芽を残さない。
                // 成績は QualifierStatsSim が県予選スコープへ積む＝解決器には積ませない（stats: null）。
                var resolver = new BackgroundMatchResolver(
                    NationService.Rosters, new GameContext(), yearIndex, stats: null,
                    modernRules: null, calendarYear: calendarYear, brains: brains);
                workers[w] = BackgroundWork.RunSteps(() => Step(pending, stats, rng, yearIndex, resolver));
            }
            _task = Task.WhenAll(workers);
        }

        /// <summary>
        /// 1歩＝1校。校の添字を奪い合いながら1校ぶん打ち直し、まだ校が残っていれば true を返す
        /// （スレッド版は false まで回し、ブラウザ版はコマごとに少しずつ回す。#1588）。
        /// </summary>
        private static bool Step(
            IReadOnlyList<PrefectureResult> pending, NationTournamentStats stats, IRandomSource rng,
            int yearIndex, BackgroundMatchResolver resolver)
        {
            PauseGate();

            var idx = Interlocked.Increment(ref _nextJob) - 1;
            if (idx >= pending.Count) return false;

            var result = pending[idx];
            QualifierStatsSim.ReplayChampionPath(
                result, NationService.SchoolById, resolver, stats, TournamentEntry.Schedule,
                QualifierStatsSim.RoundsFor(result.Entrants), yearIndex, rng, afterMatch: PauseGate);

            Interlocked.Increment(ref _done);
            BackgroundWork.Sleep(StepSleepMs);
            return true;
        }

        /// <summary>
        /// スロットルの停止点（#208 と同じ作法）。ライブ観戦中（Paused）はスピン待機して確保を止める。
        /// スロットルは <see cref="NationBackgroundSim"/> が持つものを共有する＝画面が2つの背景処理を
        /// 別々に止め分けなくてよい。
        /// </summary>
        private static void PauseGate()
        {
            var m = Interlocked.Increment(ref _matchesResolved);
            if (m % CollectEveryMatches == 0) GC.Collect();

            while (BackgroundWork.Threaded && NationBackgroundSim.Throttle == NationBackgroundSim.SimThrottle.Paused)
                BackgroundWork.Sleep(PausedPollMs);
        }

        /// <summary>
        /// 打ち直しを完了まで待つ（ロスター変更＝年度替わり・代替わりの前に必ず呼ぶ。
        /// 背景スレッドと同じ <c>NationRosters</c> を同時に触らないため）。未開始なら即返る。
        /// </summary>
        public static void EnsureCompleted()
        {
            var t = _task;
            if (t == null) return;
            try { BackgroundWork.Wait(t); }
            catch (AggregateException e) { UnityEngine.Debug.LogException(e.GetBaseException()); }   // 個々の校の失敗は握り潰す（読み物なので進行は止めない）が、ログは出す（issue #1226）
            _task = null;
            _total = 0;
            Volatile.Write(ref _done, 0);
        }
    }
}
