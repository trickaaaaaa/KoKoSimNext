#if KOKOSIM_BENCHMARK || KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Home;
using KokoSim.Unity.Match;
using KokoSim.Unity.Shell;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KokoSim.Unity.Benchmark
{
    /// <summary>
    /// ベンチマークモード（<c>-benchmark</c>）の本体（issue #1314・docs/release/steam-roadmap.md 段階1）。
    ///
    /// 固定の母種で「新規開始 → 週送り → 大会日（自校の試合を実フローと同じ経路で消化）→ 甲子園の試合（満員・ライブ観戦）」
    /// を人手なしで回し、起動・ロード・週送り・大会日の待ち・fps の分布・メモリの最大値を CSV に出して終了する。
    /// 同じ手順を最低機と普通の機で回して比べ、推奨スペックを決めるための道具。
    ///
    /// <para>計測は<b>実際の画面フロー</b>の上で行う（ScreenRouter・HomeState・BackgroundGameOp・MatchLive を通る）。
    /// 画面を飛ばして engine だけ回すと、描画とメインスレッドの取り合い（＝実機で一番差が出る所）が測れない。</para>
    ///
    /// <para>甲子園の試合は大会を勝ち上がった先ではなく、固定の母種から組んだ試合（<see cref="DebugMatchFactory"/>）を
    /// 甲子園の球場・満員・ブラスバンド付きで映す。自校が勝ち上がれるかに左右されず、2台で同じ絵を測れる。</para>
    ///
    /// <para>製品版には入らない（<c>KOKOSIM_BENCHMARK</c>＝<c>tools/build-player.sh bench</c> の計測版／開発ビルド／Editor だけ）。
    /// 母種の固定（<see cref="GameSeed.Reset"/>）は設計書17 §8 のとおりリリースへ出さないため。</para>
    /// </summary>
    public sealed class BenchmarkRunner : MonoBehaviour
    {
        private const string Title = "TitleScreen";
        private const string Home = "HomeDashboard";
        private const string MatchLive = "MatchLive";

        private BenchmarkOptions _options;

        // ---- 結果 ----
        private readonly List<string[]> _rows = new List<string[]>();   // summary.csv の行（item, value, unit）
        private string _error;

        // ---- フレーム採取 ----
        private readonly Dictionary<string, List<float>> _frames = new Dictionary<string, List<float>>();
        private readonly List<string> _frameOrder = new List<string>();
        private string _phase;
        private MatchLiveController _matchCtrl;
        private int _dropSamples;

        // ---- 区間ごとの記録 ----
        private Stopwatch _phaseWatch;
        private int _phaseGc0;

        // ---- メモリ ----
        private long _gcPeakBytes;
        private long _workingSetPeakBytes;
        private float _nextWorkingSetAt;

        /// <summary>起動引数に <c>-benchmark</c> があれば計測を始める。無ければ何もしない（常駐もしない）。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (!BenchmarkOptions.TryParse(Environment.GetCommandLineArgs(), out var options)) return;
            Begin(options);
        }

        /// <summary>計測を始める（Editor の検証からも呼べる）。</summary>
        public static BenchmarkRunner Begin(BenchmarkOptions options)
        {
            var go = new GameObject("BenchmarkRunner");
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<BenchmarkRunner>();
            runner._options = options ?? new BenchmarkOptions();
            return runner;
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            AddRow("startup_to_runner", Num(Time.realtimeSinceStartupAsDouble), "s");
            yield return RunSafe(Run());
        }

        // 例外で止まっても、そこまでの結果は必ず書いて終わる（無人で回すため、CSV が無いのが一番困る）。
        // 入れ子のコルーチンは使わない（Unity は入れ子の中の例外をここへ返さない）。
        private IEnumerator RunSafe(IEnumerator inner)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext()) break;
                    current = inner.Current;
                }
                catch (Exception e)
                {
                    _error = e.ToString();
                    Debug.LogException(e);
                    break;
                }
                yield return current;
            }
            Finish();
        }

        private void Update()
        {
            SampleMemory();
            if (_dropSamples > 0) { _dropSamples--; return; }
            if (_phase == null) return;

            // 試合は対決ビュー（投手と打者）と打球の俯瞰（守備の場面）で重さが違うので別々に積む。
            var label = _phase;
            if (_phase == "match" && _matchCtrl != null)
                label = _matchCtrl.DuelViewOnForBenchmark ? "match_duel" : "match_field";

            if (!_frames.TryGetValue(label, out var list))
            {
                list = new List<float>(4096);
                _frames[label] = list;
                _frameOrder.Add(label);
            }
            list.Add(Time.unscaledDeltaTime);
        }

        // ---- 本編 ----

        private IEnumerator Run()
        {
            var router = ScreenRouter.Instance;
            var waited = 0f;
            while ((ScreenRouter.Instance == null || ScreenRouter.Instance.CurrentScreen != Title) && waited < 60f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            router = ScreenRouter.Instance;
            if (router == null || router.CurrentScreen != Title) throw new InvalidOperationException("タイトル画面が出なかった");

            AddRow("startup_to_title", Num(Time.realtimeSinceStartupAsDouble), "s");
            var sinceProcess = SecondsSinceProcessStart();
            if (sinceProcess >= 0) AddRow("startup_to_title_since_process_start", Num(sinceProcess), "s");

            // タイトル
            SetPhase("title");
            yield return IdleFor(_options.IdleSeconds);

            // 新規開始 → ホーム
            SetPhase("new_game");
            var sw = Stopwatch.StartNew();
            SaveGameIO.StartNewSchool(NationService.DefaultSchoolName, NationService.DefaultPrefectureId, null, _options.Seed);
            HomeState.Reset();
            AddRow("new_game_call", Num(sw.Elapsed.TotalMilliseconds), "ms");
            router.ShowDeferred(Home);
            waited = 0f;
            while (router.CurrentScreen != Home && waited < 120f) { waited += Time.unscaledDeltaTime; yield return null; }
            yield return null;
            AddRow("new_game_to_home_first_frame", Num(sw.Elapsed.TotalMilliseconds), "ms");

            SetPhase("home");
            yield return IdleFor(_options.IdleSeconds);

            // 週送り（大会に入るまで）
            SetPhase("weeks");
            var weeks = 0;
            var weekMs = new List<double>();
            while (!GameSession.Current.InTournament && weeks < _options.MaxWeeks)
            {
                sw.Restart();
                HomeState.Current.AdvanceWeek();
                var ms = sw.Elapsed.TotalMilliseconds;
                weekMs.Add(ms);
                weeks++;
                yield return null;
                while (BackgroundGameOp.Running) yield return null;
            }
            AddRow("weeks_advanced", weeks.ToString(), "weeks");
            AddRow("reached_tournament", GameSession.Current.InTournament ? "1" : "0", "bool");
            if (weekMs.Count > 0)
            {
                var sorted = weekMs.ToArray();
                double sum = 0;
                foreach (var v in sorted) sum += v;
                Array.Sort(sorted);
                AddRow("week_advance_avg", Num(sum / sorted.Length), "ms");
                AddRow("week_advance_worst", Num(sorted[sorted.Length - 1]), "ms");
                for (var i = 0; i < weekMs.Count; i++) AddRow("week_advance_" + (i + 1), Num(weekMs[i]), "ms");
            }

            // 大会日（自校の試合を実フローと同じ経路で消化する）
            SetPhase("tournament");
            if (GameSession.Current.InTournament) HomeState.Current.ConsumeBanner();
            var played = 0;
            while (played < _options.TournamentMatches
                   && GameSession.Current.InTournament && !GameSession.Current.Runner.Finished)
            {
                var n = played + 1;

                // 試合日まで日を送る（7日を跨ぐと練習1週ぶんが回る）
                sw.Restart();
                var days = 0;
                while (!GameSession.Current.ReachedMatchDay && days < 60) { HomeState.Current.AdvanceDay(); days++; }
                yield return null;
                AddRow("tournament_" + n + "_advance_days", Num(sw.Elapsed.TotalMilliseconds), "ms");

                // 試合開始の準備（本番は MatchStartFlow が背景で回して待ち画面を出す区間）
                while (BackgroundGameOp.Running) yield return null;
                LivePlayerMatch live = null;
                var begun = false;
                sw.Restart();
                if (!BackgroundGameOp.Run(() => live = HomeState.Current.BeginMatch(), () => begun = true))
                    throw new InvalidOperationException("試合開始の準備を始められなかった");
                while (!begun) yield return null;
                AddRow("tournament_" + n + "_begin_match_wait", Num(sw.Elapsed.TotalMilliseconds), "ms");
                if (live == null) throw new InvalidOperationException("試合開始の準備に失敗した（ログの例外を参照）");

                // 試合そのものは委任で最後まで解く（観戦の時間は甲子園の段で測る）。主スレッドを止めるので採取を止める。
                var savedPhase = _phase;
                _phase = null;
                sw.Restart();
                var result = live.Progression.SkipDelegateToAi(live.ManagerIsAway, ManagerService.TacticalSenseForAi);
                AddRow("tournament_" + n + "_game_sim", Num(sw.Elapsed.TotalMilliseconds), "ms");
                _dropSamples = 2;
                _phase = savedPhase;
                yield return null;

                // 試合後処理（本番は結果画面で背景に回す。残りブラケットのフルシム＋全国背景シムへの join）
                var completed = false;
                sw.Restart();
                if (!BackgroundGameOp.Run(() => HomeState.Current.CompleteMatch(result), () => completed = true))
                    throw new InvalidOperationException("試合後処理を始められなかった");
                while (!completed) yield return null;
                AddRow("tournament_" + n + "_complete_match_wait", Num(sw.Elapsed.TotalMilliseconds), "ms");

                // 全国の背景シムが落ち着くまで（次の日送りで待たされうる時間）
                var idleWaited = 0f;
                while ((BackgroundGameOp.Running || NationBackgroundSim.Running) && idleWaited < 600f)
                {
                    idleWaited += Time.unscaledDeltaTime;
                    yield return null;
                }
                AddRow("tournament_" + n + "_until_background_idle", Num(sw.Elapsed.TotalMilliseconds), "ms");

                GameSession.Current.ConsumeResult();
                played++;
            }
            AddRow("tournament_matches_played", played.ToString(), "matches");

            // 甲子園の試合（満員・ブラスバンド・ライブ観戦）
            SetPhase("match_open");
            var built = DebugMatchFactory.Build(Stadium.Koshien, new DebugMatchOptions { Seed = _options.Seed });
            var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
            var matchEnded = false;
            MatchLiveController.Pending = new MatchLiveController.LiveMatchRequest
            {
                Progression = prog,
                ManagerIsAway = false,
                AwayName = built.Away.Name,
                HomeName = built.Home.Name,
                AwayColors = TeamColorProvider.Palette.ColorsFor(9051),
                HomeColors = TeamColorProvider.Palette.ColorsFor(9052),
                AwaySchoolId = int.MinValue,
                HomeSchoolId = int.MinValue,
                RematchOnTie = false,
                Crowd = CrowdTurnout.Full,
                BrassBand = true,
                OnComplete = _ => matchEnded = true,
            };
            sw.Restart();
            router.ShowDeferred(MatchLive);
            waited = 0f;
            while ((router.CurrentScreen != MatchLive || _matchCtrl == null) && waited < 120f)
            {
                waited += Time.unscaledDeltaTime;
                if (router.CurrentScreen == MatchLive && _matchCtrl == null)
                    _matchCtrl = FindFirstObjectByType<MatchLiveController>();
                yield return null;
            }
            if (_matchCtrl == null) throw new InvalidOperationException("試合の画面が出なかった");
            for (var i = 0; i < 3; i++) yield return null;
            AddRow("koshien_match_open", Num(sw.Elapsed.TotalMilliseconds), "ms");

            if (Math.Abs(_options.MatchSpeed - 1f) > 0.001f) _matchCtrl.SetSpeedForCapture(_options.MatchSpeed);
            SetPhase("match");
            var matchStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - matchStart < _options.MatchSeconds
                   && !_matchCtrl.IsGameOver && !matchEnded)
                yield return null;
            AddRow("koshien_match_game_over", _matchCtrl.IsGameOver ? "1" : "0", "bool");
            _matchCtrl = null;
        }

        private IEnumerator IdleFor(float seconds)
        {
            var t = 0f;
            while (t < seconds) { t += Time.unscaledDeltaTime; yield return null; }
        }

        // ---- 区間 ----

        private void SetPhase(string name)
        {
            EndPhase();
            _phase = name;
            if (name == null) return;
            _phaseWatch = Stopwatch.StartNew();
            _phaseGc0 = GC.CollectionCount(0);
        }

        private void EndPhase()
        {
            if (_phase == null || _phaseWatch == null) return;
            AddRow("phase_" + _phase + "_seconds", Num(_phaseWatch.Elapsed.TotalSeconds), "s");
            AddRow("phase_" + _phase + "_gc_gen0_collections", (GC.CollectionCount(0) - _phaseGc0).ToString(), "count");
            AddRow("phase_" + _phase + "_end_gc_heap", Num(GC.GetTotalMemory(false) / 1048576.0, 1), "MB");
            _phaseWatch = null;
        }

        private void SampleMemory()
        {
            var gc = GC.GetTotalMemory(false);
            if (gc > _gcPeakBytes) _gcPeakBytes = gc;
            if (Time.unscaledTime < _nextWorkingSetAt) return;
            _nextWorkingSetAt = Time.unscaledTime + 1f;
            try
            {
                using (var p = Process.GetCurrentProcess())
                    if (p.WorkingSet64 > _workingSetPeakBytes) _workingSetPeakBytes = p.WorkingSet64;
            }
            catch (Exception) { /* 取れない環境は欄を空で出す */ }
        }

        private static double SecondsSinceProcessStart()
        {
            try
            {
                using (var p = Process.GetCurrentProcess())
                    return (DateTime.Now - p.StartTime).TotalSeconds;
            }
            catch (Exception) { return -1; }
        }

        // ---- 出力 ----

        private void Finish()
        {
            SetPhase(null);
            try
            {
                var dir = string.IsNullOrEmpty(_options.OutDir)
                    ? Path.Combine(Application.persistentDataPath, "benchmark")
                    : _options.OutDir;
                Directory.CreateDirectory(dir);
                var stem = "benchmark_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var summaryPath = Path.Combine(dir, stem + "_summary.csv");
                var framesPath = Path.Combine(dir, stem + "_frames.csv");
                File.WriteAllText(summaryPath, BuildSummary(), new UTF8Encoding(true));
                File.WriteAllText(framesPath, BuildFrames(), new UTF8Encoding(true));
                Debug.Log("[Benchmark] DONE " + (_error == null ? "ok" : "ERROR") + " summary=" + summaryPath + " frames=" + framesPath);
            }
            catch (Exception e)
            {
                _error = (_error ?? "") + "\n" + e;
                Debug.LogException(e);
            }

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(_error == null ? 0 : 1);
#endif
        }

        private string BuildSummary()
        {
            var sb = new StringBuilder();
            sb.Append("item,value,unit\n");

            void Row(string item, string value, string unit = "")
                => sb.Append(BenchmarkStats.CsvField(item)).Append(',')
                    .Append(BenchmarkStats.CsvField(value)).Append(',')
                    .Append(BenchmarkStats.CsvField(unit)).Append('\n');

            // 機体と設定（2台の CSV を並べたとき何が違うかを一目で見るため）
            Row("result", _error == null ? "ok" : "error");
            if (_error != null) Row("error", _error.Replace("\r", " ").Replace("\n", " "));
            Row("date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Row("app_version", Application.version);
            Row("unity_version", Application.unityVersion);
            Row("development_build", UnityEngine.Debug.isDebugBuild ? "1" : "0", "bool");
            Row("platform", Application.platform.ToString());
            Row("os", SystemInfo.operatingSystem);
            Row("cpu", SystemInfo.processorType);
            Row("cpu_cores", SystemInfo.processorCount.ToString());
            Row("cpu_mhz", SystemInfo.processorFrequency.ToString(), "MHz");
            Row("ram", SystemInfo.systemMemorySize.ToString(), "MB");
            Row("gpu", SystemInfo.graphicsDeviceName);
            Row("gpu_vram", SystemInfo.graphicsMemorySize.ToString(), "MB");
            Row("graphics_api", SystemInfo.graphicsDeviceType.ToString());
            Row("screen", Screen.width + "x" + Screen.height);
            Row("fullscreen_mode", Screen.fullScreenMode.ToString());
            Row("quality", QualitySettings.names[QualitySettings.GetQualityLevel()]);
            Row("fps_limit", KokoSim.Bootstrap.FrameRateGuard.FpsLimit.ToString(), "fps");
            Row("seed", "0x" + _options.Seed.ToString("x16"));

            // 計測した時間・件数
            foreach (var r in _rows) Row(r[0], r[1], r[2]);

            // fps の分布（区間ごと）
            var all = new List<float>();
            foreach (var label in _frameOrder)
            {
                var list = _frames[label];
                FrameRows(Row, label, BenchmarkStats.Summarize(list));
                if (label.StartsWith("match_")) all.AddRange(list);
            }
            if (all.Count > 0) FrameRows(Row, "match_all", BenchmarkStats.Summarize(all));

            // メモリ
            Row("gc_heap_peak", Num(_gcPeakBytes / 1048576.0, 1), "MB");
            Row("working_set_peak_sampled", _workingSetPeakBytes > 0 ? Num(_workingSetPeakBytes / 1048576.0, 1) : "", "MB");
            Row("gc_gen0_collections_total", GC.CollectionCount(0).ToString(), "count");
            Row("gc_gen2_collections_total", GC.CollectionCount(2).ToString(), "count");
            Row("unity_total_allocated", Num(UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0, 1), "MB");
            Row("unity_total_reserved", Num(UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576.0, 1), "MB");
            return sb.ToString();
        }

        private static void FrameRows(Action<string, string, string> row, string label, FrameSummary s)
        {
            row("fps_" + label + "_frames", s.Count.ToString(), "frames");
            row("fps_" + label + "_avg", Num(s.AvgFps, 1), "fps");
            row("fps_" + label + "_1pct_low", Num(s.Fps1PctLow, 1), "fps");
            row("frame_" + label + "_p50", Num(s.P50Ms), "ms");
            row("frame_" + label + "_p95", Num(s.P95Ms), "ms");
            row("frame_" + label + "_p99", Num(s.P99Ms), "ms");
            row("frame_" + label + "_worst", Num(s.WorstMs), "ms");
        }

        private string BuildFrames()
        {
            var sb = new StringBuilder();
            sb.Append("phase,frame,frame_ms\n");
            foreach (var label in _frameOrder)
            {
                var list = _frames[label];
                for (var i = 0; i < list.Count; i++)
                    sb.Append(label).Append(',').Append(i).Append(',').Append(Num(list[i] * 1000.0)).Append('\n');
            }
            return sb.ToString();
        }

        private void AddRow(string item, string value, string unit) => _rows.Add(new[] { item, value, unit });

        private void AddRow(string item, string value) => AddRow(item, value, "");

        private static string Num(double v, int decimals = 2) => BenchmarkStats.Num(v, decimals);
    }
}
#endif
