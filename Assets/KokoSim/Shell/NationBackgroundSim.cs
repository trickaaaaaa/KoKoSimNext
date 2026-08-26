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
    /// 全国47県の裏試合フルシム（自県以外）を「スライス駆動＋スロットル」でバックグラウンドスレッドで回す
    /// （設計書05 §1.4 / #43・#208）。1区画（県）ごとに <see cref="NationalTournamentEngine.BeginSummer"/> の
    /// スライスを1件消化し、各スライスの合間で <see cref="Throttle"/> を見て停止／小休止／即継続を切り替える。
    /// これで一括バーストの確保嵐（約2.1GB/37秒＝58MB/秒）を刻んで分散させ、Mono の stop-the-world GC が
    /// メインスレッドを数十秒固める／OOMで落ちる問題を解消する（#208 真因）。
    ///
    /// エンジンは Unity 非依存＝スレッドで安全に回せる。共有する NationRosters / NationTournamentStats は
    /// スレッド安全化済み（自県はメインスレッドの TournamentRunner が同一 stats へ積む・県キーは disjoint）。
    /// UnityEngine API は一切触らない（Task 内でのメインスレッド依存を持ち込まない）。
    /// </summary>
    public static class NationBackgroundSim
    {
        /// <summary>背景シムの消化ペース。画面が切り替える（#208）。</summary>
        public enum SimThrottle
        {
            /// <summary>通常（緩やかにレート抑制しつつ消化）。</summary>
            Normal,
            /// <summary>ほぼ停止（ライブ試合中＝2D再生の滑らかさを守る）。次スライスへ進まない。</summary>
            Paused,
            /// <summary>全開（静止画面での集中消化。1〜2フレームおきにメインへ描画を譲る程度に刻む）。</summary>
            Boost,
        }

        // 区画間の休止（ms）。0 にはせず、メインスレッドが GC stop-the-world で飢餓しない程度に1〜2フレームぶん
        // 譲る（#208 追記＝進捗バーを滑らかに保つ）。ライブ試合以外（Normal/Boost）はほぼ全開で消化して待ち時間を
        // 縮める（ユーザー要望「ロード短く」）＝真のブレーキは Paused（ライブ観戦中）だけ。
        private const int NormalSleepMs = 2;
        private const int BoostSleepMs = 2;
        private const int PausedPollMs = 16;

        // 静止画面で回す並列ワーカー数（#208）。区画は独立 Fork なので並列でも決定論不変。並列本数は
        // 「同時に churn する量＝Mono(Boehm) がヒープを確保する量のピーク」を決める。Boehm は一度確保した
        // ヒープを OS へ返さず、以後どの GC もヒープ全体を走査する（走査時間∝ヒープ）＝ピークを抑えるほど
        // 後続 GC が軽い。速度と確保ピークの折衷で3本（コア数−2 かつ ≤3）に絞る。
        private static readonly int MaxWorkers = Math.Max(1, Math.Min(3, Environment.ProcessorCount - 2));

        private static volatile int _throttle = (int)SimThrottle.Normal;
        private static volatile Task _task;
        private static int _done;         // Interlocked。消化済み区画数（進捗バーの分子）。
        private static int _nextJob;      // Interlocked。次に取るジョブ添字（並列ワーカーが奪い合う）。
        private static volatile int _total;  // シム対象区画数（進捗バーの母数）。
        private static int _matchesResolved;  // Interlocked。裏試合を解決した累計試合数（定期GCの分母）。
        // 手元の結果がどの大会のものか（ResultsGate で保護。未設定＝無し）。年ではなくシードで刻むのは
        // AutumnBackgroundSim と同じ理由＝母種が違えば「同じ年」でも全国の学校そのものが別物になる。
        private static ulong _resultsSeed;
        private static bool _resultsSeedSet;
        // 校別勝数を年間台帳（AI校進化の材料）へ積むか。**ロード復帰の積み直しでは false**＝
        // 勝数はセーブ（nationYearWins）から復元済みで、積むと二重計上になる。
        private static volatile bool _foldLedger = true;
        // 消化中に GC を挟むか。ゲームプレイ中はフレームを守るために挟むが、ロード復帰の
        // 追いつき実行（49区画で実測44ms）では 1 回の Gen2 GC の方が高くつくので挟まない。
        private static volatile bool _collectGarbage = true;

        /// <summary>直近に開始した全国裏試合タスク（次の節目までに完了させるために保持）。</summary>
        public static Task Current => _task;

        /// <summary>現在のスロットル状態を設定する（画面遷移から呼ぶ）。</summary>
        public static void SetThrottle(SimThrottle t) => _throttle = (int)t;

        /// <summary>現在のスロットル状態。</summary>
        public static SimThrottle Throttle => (SimThrottle)_throttle;

        /// <summary>消化済み区画数。</summary>
        public static int Done => Volatile.Read(ref _done);

        /// <summary>シム対象の総区画数（0＝未開始）。</summary>
        public static int Total => _total;

        /// <summary>背景シムがまだ走っているか（進捗オーバーレイの表示判定に使う）。</summary>
        public static bool Running
        {
            get { var t = _task; return t != null && !t.IsCompleted; }
        }

        /// <summary>進捗（0..1）。未開始・母数0のときは 1（＝完了扱いで表示しない）。</summary>
        public static float Progress
        {
            get { var total = _total; return total <= 0 ? 1f : Math.Min(1f, (float)Done / total); }
        }

        // 消化した区画の結果（他県ぶん）。甲子園（49代表）の入力なので**捨てない**。
        // 背景ワーカーが並列に書き、メインスレッド（KoshienEntry）が読むのでロックで守る。
        // 並列消化は順不同なので、読み出し側で区画Id順に整列してから使う（決定論＝並び順に依存させない）。
        private static readonly List<PrefectureResult> Results = new List<PrefectureResult>();
        private static readonly object ResultsGate = new object();

        /// <summary>
        /// 他県の地方大会結果（消化済みぶん・スナップショット）。甲子園の出場校を組むために使う。
        ///
        /// <para>並列消化なので<b>積まれる順は不定</b>。<see cref="EnsureCompleted"/> を先に呼んで
        /// 全区画を確定させ、呼び出し側が区画Id順へ整列すること（並び順が結果に効く場所では
        /// 決定論が壊れるため）。</para>
        /// </summary>
        public static IReadOnlyList<PrefectureResult> PrefectureResults
        {
            get { lock (ResultsGate) return new List<PrefectureResult>(Results); }
        }

        /// <summary>
        /// この大会（<paramref name="seed"/>）の他県ぶんが手元にある（消化済み or 消化中）か。
        /// <see cref="Start"/> を二重に呼ばないための判定（<c>AutumnBackgroundSim.HasResultsFor</c> と同型）。
        /// </summary>
        public static bool HasResultsFor(ulong seed)
        {
            lock (ResultsGate)
            {
                if (!_resultsSeedSet || _resultsSeed != seed) return false;
                return Running || Results.Count > 0;
            }
        }

        /// <summary>
        /// 他県（<paramref name="excludePrefectureId"/> 以外）の夏の地方大会をスライス駆動で開始する。
        /// 自県は呼び出し側（HomeState の TournamentRunner）が対話的に消化して同一 stats へ積むため除外する。
        /// 自県が北海道・東京（分割県）のときは <paramref name="excludeSplit"/> に自校の区画だけを渡す＝
        /// もう片方の区画（南北海道等）はここで通常どおり背景消化され、独自の代表を出す（issue 北海道・東京分割）。
        ///
        /// <para><paramref name="foldLedger"/> false＝校別勝数を年間台帳へ積まない、
        /// <paramref name="collectGarbage"/> false＝消化中に GC を挟まない。どちらもロード復帰の
        /// 積み直し（<c>TournamentEntry.RestoreRunner</c>）用で、通常の大会入場では既定の true。</para>
        /// </summary>
        public static void Start(
            Nation nation, NationRosters rosters, NationTournamentStats stats,
            NationCoefficients coeff, TournamentSchedule schedule, int yearIndex, int calendarYear,
            int excludePrefectureId, int? excludeSplit, ulong seed, IEnemyBrainFactory brains,
            bool foldLedger = true, bool collectGarbage = true)
        {
            var rng = new Xoshiro256Random(seed ^ 0x4E37_1000UL);
            var ctx = new GameContext();
            // 他46〜48県（自校非関与）は優勝校のみ集計モデルで即決する（#試合開始前ロード短縮）。甲子園本戦・全国成績閲覧が
            // 未実装の現状、他県は優勝校しか消費されないため1球フルシムは不要＝約4000試合の裏 churn（約5分・2GB確保）を消す。
            // 自県は別途プレフェッチでフルシムする（TournamentEntry 側）。個別県の詳細が要る場面が来たらオンデマンド Full 化する。
            var run = NationalTournamentEngine.BeginSummer(
                nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
                excludePrefectureId: excludePrefectureId, excludeSplit: excludeSplit, modernRules: null, calendarYear: calendarYear,
                brains: brains, afterMatch: PauseGate, resolution: SummerResolution.ChampionsOnly);

            _throttle = (int)SimThrottle.Normal;
            _foldLedger = foldLedger;
            _collectGarbage = collectGarbage;
            _total = run.Total;
            Volatile.Write(ref _done, 0);
            Volatile.Write(ref _nextJob, 0);
            Volatile.Write(ref _matchesResolved, 0);
            // 今年ぶんだけを持つ（去年の代表を混ぜない）。刻印は Clear と同じロックで立てる
            // ＝HasResultsFor が「空にした直後・刻印前」の隙間を見て取り違えない。
            lock (ResultsGate)
            {
                Results.Clear();
                _resultsSeed = seed;
                _resultsSeedSet = true;
            }

            // 静止画面で待ち時間を縮めるため複数ワーカーで並列消化（区画は独立 Fork ＝決定論不変）。
            // Paused（ライブ試合中）は各ワーカーが手前で停止＝確保が止まり2D再生の滑らかさを守る。
            var jobs = run.Jobs;
            var workers = new Task[MaxWorkers];
            for (var w = 0; w < workers.Length; w++)
                workers[w] = Task.Run(() => Worker(jobs));
            _task = Task.WhenAll(workers);
        }

        /// <summary>
        /// 1ワーカーぶんのループ。ジョブ添字を奪い合いながら1区画ずつフルシムし、各区画の合間で
        /// スロットルに従い休止する（背景スレッド上で走る）。Paused の間はジョブを取らずスピン待機する
        /// ＝確保が止まりライブ試合の滑らかさを守る。ブースト／通常へ切替で再開する。
        /// </summary>
        private static void Worker(IReadOnlyList<Func<PrefectureResult>> jobs)
        {
            while (true)
            {
                // ライブ試合中（Paused）は新しい区画を取らずに待つ（確保を止める）。
                PauseGate();

                var idx = Interlocked.Increment(ref _nextJob) - 1;
                if (idx >= jobs.Count) return;   // 全区画消化済み＝このワーカーは終了。

                // 重い処理: 1区画をフルシム。県内の各試合の合間で PauseGate（engine の afterMatch フック経由）が
                // 走るので、ライブ試合に入った瞬間に県の途中でも即座に手を止められる＝県丸ごと走り切らない。
                var result = jobs[idx]();
                // 他県の校別勝数を年間台帳へ（AI校の年次進化の材料。台帳はロックで守られている）。
                // ロード復帰の積み直しでは積まない＝勝数はセーブ（nationYearWins）から復元済みで、
                // ここで積むと同じ大会を二度数えてAI校の進化が倍加速する。
                if (_foldLedger) NationService.YearLedger.AddWins(result.Wins);
                // 代表校（＋勝ち上がり）は甲子園の入力なので控える。以前はここで捨てていたため
                // 「49代表が集まらない」＝甲子園を組めない状態だった。
                lock (ResultsGate) Results.Add(result);
                Interlocked.Increment(ref _done);

                // 区画間で必ずメインスレッドへ時間を譲る（確保レート抑制＝GC を短く分散させる）。
                Thread.Sleep((SimThrottle)_throttle == SimThrottle.Boost ? BoostSleepMs : NormalSleepMs);
            }
        }

        /// <summary>
        /// スロットルの停止点（#208）。Paused（ライブ観戦中）の間はスピン待機し、それ以外は即座に返る。
        /// ワーカーループの区画間と、engine の <c>afterMatch</c> 経由で県内の試合間の両方から呼ばれる
        /// ＝ライブ試合に入った瞬間、県の途中であっても次の1試合を待たずに確保を止められる。
        /// </summary>
        // 何試合ごとに GC を挟むか（#208 真因対策）。フルシムは1試合あたり約550KB を churn し、放置すると
        // Mono(Boehm) がヒープを2GB超まで確保→以後どの GC も巨大ヒープ走査で数十秒 stop-the-world（＝ライブ
        // 試合のフリーズ・OOM の正体。Boehm はヒープを OS へ返さない）。試合の合間にこまめに GC を挟んで確保
        // ピークを小さく保てば、後続 GC が軽くなる。48試合 ≒ 26MB ごと（確保ピークを低く＝Boehm 拡張を抑える）。
        // _matchesResolved は原子的に一意なので modulo が同値で1回だけ真になる＝多重 GC しない。
        private const int CollectEveryMatches = 48;

        private static void PauseGate()
        {
            var m = Interlocked.Increment(ref _matchesResolved);
            if (_collectGarbage && m % CollectEveryMatches == 0)
                GC.Collect();   // ヒープ肥大を防ぐ短い定期回収（ライブ試合前に溜め込まない）。
            while ((SimThrottle)_throttle == SimThrottle.Paused)
                Thread.Sleep(PausedPollMs);
        }

        /// <summary>
        /// 実行中の全国裏試合を完了まで待つ（成長の節目＝メインスレッドのロスター変更の前に必ず呼ぶ。
        /// 背景タスクと成長のロスター変更が重ならないようにするため）。未開始なら即返る。
        /// Paused のままだと次区画へ進まず終わらないため、待つ前に Boost（全開）へ切り替えて確実に drain する。
        /// </summary>
        public static void EnsureCompleted()
        {
            var t = _task;
            if (t == null) return;
            _throttle = (int)SimThrottle.Boost;   // Paused で止まっていても最後まで消化させる。
            try { t.Wait(); }
            catch (AggregateException) { /* 個々の県の失敗は握り潰す（大会全体は継続。詳細はログ層で） */ }
            _task = null;
            _total = 0;
            Volatile.Write(ref _done, 0);
            _throttle = (int)SimThrottle.Normal;
        }
    }
}
