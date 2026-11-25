#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Unity.Match;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// 撮影レシピ（<see cref="CaptureRecipe"/>）を Play 中に実行する共通の撮影係。
    /// これまで1件ごとに書いていた *Shooter の共通部分（MatchLive だけ点ける・コントローラ探し・盤面の切り抜き撮影・
    /// 固定刻みで時計を進めて撮る・毎球のサイン入力・シードを変えた撮り直し）をここに1つだけ持つ。
    ///
    /// 起動: <c>CaptureRunner.Run("id", …)</c>（Editor）が <see cref="Arm"/> して Play に入る →
    /// <see cref="AutoSpawn"/> がこのコンポーネントを足す → 全レシピを順に撮って Play を抜ける。
    /// 出力: <c>screenshots/&lt;id&gt;/</c> に *.png・probes.jsonl・summary.json と、成否の目印 DONE / FAILED。
    /// 設定の綴り違い（実測・切り替えの名前）は黙って飛ばさず FAILED で止める。
    /// </summary>
    public sealed class RecipeShooter : MonoBehaviour
    {
        private const string ArmKey = "KokoSim.CaptureRecipes.Armed";
        public const string OutRoot = "screenshots";

        private string[] _ids;

        public static void Arm(string[] ids) => UnityEditor.SessionState.SetString(ArmKey, string.Join(",", ids));

        public static void Disarm() => UnityEditor.SessionState.EraseString(ArmKey);

        public static string RunMarkerPath => Path.Combine(OutRoot, "_capture-run.txt");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            var armed = UnityEditor.SessionState.GetString(ArmKey, "");
            if (string.IsNullOrEmpty(armed)) return;
            UnityEditor.SessionState.EraseString(ArmKey);
            var go = new GameObject("RecipeShooter");
            go.AddComponent<RecipeShooter>()._ids = armed.Split(',');
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            var runLog = new StringBuilder();
            foreach (var id in _ids)
            {
                var errors = new List<string>();
                if (!CaptureRecipes.TryGet(id, out var recipe, errors))
                {
                    WriteOutcome(id, false, errors, null);
                    runLog.Append(id).Append(" FAILED\n");
                    continue;
                }
                var run = new Run(recipe);
                yield return run.Execute(this);
                runLog.Append(id).Append(run.Ok ? " DONE\n" : " FAILED\n");
            }
            Directory.CreateDirectory(OutRoot);
            File.WriteAllText(RunMarkerPath, runLog.ToString());
            Debug.Log("[capture] 終了\n" + runLog);
            UnityEditor.EditorApplication.isPlaying = false;
        }

        private static void WriteOutcome(string id, bool ok, List<string> errors, string summaryJson)
        {
            var dir = Path.Combine(OutRoot, id);
            Directory.CreateDirectory(dir);
            if (summaryJson != null) File.WriteAllText(Path.Combine(dir, "summary.json"), summaryJson);
            File.WriteAllText(Path.Combine(dir, ok ? "DONE" : "FAILED"), string.Join("\n", errors) + "\n");
        }

        // ---------------------------------------------------------------------------------------------

        /// <summary>レシピ1件ぶんの実行。</summary>
        private sealed class Run
        {
            private readonly CaptureRecipe _r;
            private readonly List<string> _errors = new List<string>();
            private readonly List<string> _shotFiles = new List<string>();
            private readonly Dictionary<string, double> _anchors = new Dictionary<string, double>();
            private readonly List<(string Name, Func<DuelStageRig, object> Read)> _probes = new List<(string, Func<DuelStageRig, object>)>();
            private readonly List<(FieldInfo F, PropertyInfo P, object Old)> _restore = new List<(FieldInfo, PropertyInfo, object)>();
            private string _dir;
            private StreamWriter _jsonl;
            private int _attemptUsed;
            private ulong _seedUsed;
            private string _result;
            private int _videoFrames;
            public bool Ok { get; private set; }

            public Run(CaptureRecipe r) { _r = r; }

            public IEnumerator Execute(MonoBehaviour host)
            {
                float t0 = Time.realtimeSinceStartup;
                _dir = Path.Combine(OutRoot, _r.Id);
                if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
                Directory.CreateDirectory(_dir);

                Validate();
                if (_errors.Count == 0) ApplyOverrides();
                if (_errors.Count > 0) { Finish(t0); yield break; }

                _jsonl = new StreamWriter(Path.Combine(_dir, "probes.jsonl"));
                try
                {
                    bool got = false;
                    for (int a = 0; a < _r.Attempts && !got && _errors.Count == 0; a++)
                    {
                        _attemptUsed = a + 1;
                        _seedUsed = _r.Seed + (ulong)a;
                        var e = Attempt(_seedUsed);
                        while (true)
                        {
                            object cur;
                            try
                            {
                                if (!e.MoveNext()) break;
                                cur = e.Current;
                            }
                            catch (Exception ex) { _errors.Add("撮影中の例外: " + ex.Message); break; }
                            if (cur is bool b) { got = b; break; }
                            yield return cur;
                        }
                    }
                    if (!got && _errors.Count == 0)
                        _errors.Add($"{_r.Attempts}回やり直しても守備ビューの再生が来なかった（scenario/force/sign を見直す）");
                }
                finally
                {
                    _jsonl.Dispose();
                    Time.captureDeltaTime = 0f;
                    RestoreOverrides();
                    MatchLiveController.ScenarioIdForCapture = null;
                    MatchLiveController.ForceOverrideForCapture = null;
                    MatchLiveController.GameSeedOverrideForCapture = null;
                    MatchLiveController.ManagerIsAwayOverrideForCapture = null;
                    MatchLiveController.AwayNameForCapture = null;
                    MatchLiveController.HomeNameForCapture = null;
                    MatchLiveController.AwayColorIdForCapture = null;
                    MatchLiveController.HomeColorIdForCapture = null;
                    MatchLiveController.AwayHelmetForCapture = null;
                    MatchLiveController.HomeHelmetForCapture = null;
                }
                Finish(t0);
            }

            /// <summary>1回ぶん。守備ビューの再生を撮り終えたら true を、来なかったら false を yield して終わる。</summary>
            private IEnumerator Attempt(ulong seed)
            {
                MatchLiveController.ScenarioIdForCapture = _r.Scenario;
                MatchLiveController.ForceOverrideForCapture = _r.Force;
                MatchLiveController.GameSeedOverrideForCapture = seed;
                MatchLiveController.ManagerIsAwayOverrideForCapture = _r.ManagerAway;
                MatchLiveController.AwayNameForCapture = _r.AwayName;
                MatchLiveController.HomeNameForCapture = _r.HomeName;
                MatchLiveController.AwayColorIdForCapture = _r.AwayColorId;
                MatchLiveController.HomeColorIdForCapture = _r.HomeColorId;
                MatchLiveController.AwayHelmetForCapture = _r.AwayHelmet;
                MatchLiveController.HomeHelmetForCapture = _r.HomeHelmet;
                EnsureOnlyMatchLive(false);
                yield return null;
                EnsureOnlyMatchLive(true);
                yield return null; yield return null;
                var ctrl = FindController();
                if (ctrl == null) { _errors.Add("MatchLive のコントローラが見つからない"); yield return false; yield break; }
                if (ctrl.DuelViewOnForCapture != _r.DuelView) ctrl.ToggleDuelViewForCapture();
                ctrl.SetSpeedForCapture(_r.Speed);

                // 対決ビューのまま撮る（duel_seconds）: 最初の球が投げられた瞬間から決まった秒数だけ連番で撮る
                if (_r.DuelSeconds > 0f)
                {
                    float waitedDuel = 0f;
                    while (!_r.DuelFromLaunch && !ctrl.InPitchPhaseForCapture)
                    {
                        if (ctrl.IsGameOver || waitedDuel > _r.TimeoutSeconds) { yield return false; yield break; }
                        waitedDuel += Time.unscaledDeltaTime;
                        yield return null;
                    }
                    var ddir = Path.Combine(_dir, "video");
                    Directory.CreateDirectory(ddir);
                    Time.captureDeltaTime = 1f / _r.VideoFps;
                    int frames = Mathf.CeilToInt(_r.DuelSeconds * _r.VideoFps);
                    bool timeoutSent = false;
                    bool sceneSeen = false, restoreLogged = false;
                    int woMax = -1, woExpected = -1;
                    float woCelebrate = -1f;
                    for (int f = 0; f < frames; f++)
                    {
                        if (!timeoutSent && _r.DefenseTimeoutAt >= 0f && f / (float)_r.VideoFps >= _r.DefenseTimeoutAt)
                        {
                            if (_r.TimeoutViaMenu)
                            {
                                // 画面の操作と同じ流れで送る（issue #1174）。球の途中なら送れないので次のコマで試す
                                timeoutSent = ctrl.SendTimeoutViaMenuForCapture(_r.TimeoutMessengerIndex);
                                if (timeoutSent) sceneSeen = false;
                            }
                            else
                            {
                                timeoutSent = true;
                                ctrl.DefenseTimeoutForCapture();
                            }
                        }
                        // 伝令の場面が明けたら、投手・捕手の基準位置/向きが場面の前へ戻ったかを記録する（汚染の見張り）
                        if (_r.TimeoutViaMenu && timeoutSent)
                        {
                            if (ctrl.TimeoutSceneOnForCapture) sceneSeen = true;
                            else if (sceneSeen && !restoreLogged && ctrl.DuelRigForCapture != null)
                            {
                                restoreLogged = true;
                                var rig = ctrl.DuelRigForCapture;
                                var err = rig.TimeoutRestoreErrorForCapture();
                                var oerr = rig.OffenseTimeoutRestoreErrorForCapture();   // 攻撃伝令（issue #1175）は打者
                                File.WriteAllText(Path.Combine(_dir, "timeout-restore.txt"), string.Format(CultureInfo.InvariantCulture,
                                    "frame\t{0}\nposM\t{1:F5}\nyawDeg\t{2:F4}\nbatterPosM\t{3:F5}\nbatterYawDeg\t{4:F4}\nmouthEarM\t{5:F3}\nbatterLeft\t{6}\n",
                                    f, err.PosM, err.YawDeg, oerr.PosM, oerr.YawDeg, rig.OffenseTimeoutMouthEarForCapture, rig.BatterLeftForCapture));
                            }
                        }
                        // サヨナラの場面（issue #1176）: 喜んだ人数がベンチ入りの人数と一致するかを数える
                        if (ctrl.DuelRigForCapture != null && ctrl.DuelRigForCapture.WalkoffSceneActive)
                        {
                            woMax = Math.Max(woMax, ctrl.DuelRigForCapture.WalkoffCelebrantsForCapture);
                            woExpected = ctrl.WalkoffExpectedCountForCapture;
                            woCelebrate = Math.Max(woCelebrate, ctrl.DuelRigForCapture.WalkoffCelebrateSeconds);
                        }
                        WriteProbes(f / (double)_r.VideoFps, ctrl.DuelRigForCapture);
                        yield return Capture(ctrl, Path.Combine(ddir, "f" + _videoFrames.ToString("000", CultureInfo.InvariantCulture) + ".png"));
                        _videoFrames++;
                        yield return null;
                    }
                    Time.captureDeltaTime = 0f;
                    if (woMax >= 0)
                        File.WriteAllText(Path.Combine(_dir, "walkoff.txt"), string.Format(CultureInfo.InvariantCulture,
                            "celebrants\t{0}\nbenchEntry\t{1}\nmatch\t{2}\ncelebrateSeconds\t{3:F2}\n",
                            woMax, woExpected, woMax == woExpected, woCelebrate));
                    yield return true;
                    yield break;
                }

                // 采配カードを押す場面（cards・issue #1473）: 最初の采配窓で止めて、押す前・押した直後・その球の後を撮る
                if (_r.Cards != null || _r.WindowCalls != null)
                {
                    // 開幕の「プレイボール」テロップが消えてから撮る（テロップは全画面の層で手札も局面も隠す）
                    float waitedWin = 0f;
                    string headBatter = null;
                    while (!ctrl.AtWindowForCapture || ctrl.GameTelopOnForCapture || ctrl.WindowSituationForCapture == null
                           || ctrl.WindowPitchNumberForCapture < _r.WindowPitch)
                    {
                        if (ctrl.IsGameOver || waitedWin > _r.TimeoutSeconds) { yield return false; yield break; }
                        // カウントの途中で止めたい（window_pitch>=2）のに、その打席が先に終わって次の打者の頭へ進んだ＝撮り直し
                        if (_r.WindowPitch > 1 && ctrl.AtWindowForCapture && ctrl.WindowSituationForCapture != null)
                        {
                            headBatter ??= ctrl.WindowBatterForCapture;
                            if (ctrl.WindowBatterForCapture != headBatter) { yield return false; yield break; }
                        }
                        waitedWin += Time.unscaledDeltaTime;
                        yield return null;
                    }
                    ctrl.FreezeLiveForCapture(true);
                    yield return null; yield return null;
                    // 撮った窓の局面を残す（走者の有無・攻守を絵と突き合わせる）
                    File.WriteAllText(Path.Combine(_dir, "window.txt"), ctrl.WindowSituationForCapture + "\n");
                    yield return Capture(ctrl, Path.Combine(_dir, "01-window.png"));
                    _shotFiles.Add("01-window.png");
                    if (_r.WindowCalls != null)
                    {
                        int uiShot = 11;
                        foreach (var call in _r.WindowCalls)
                        {
                            if (call == "shot")
                            {
                                yield return null; yield return null;
                                var file = uiShot.ToString(CultureInfo.InvariantCulture) + "-ui.png";
                                uiShot++;
                                yield return Capture(ctrl, Path.Combine(_dir, file));
                                _shotFiles.Add(file);
                                continue;
                            }
                            if (call.StartsWith("wait(") && call.EndsWith(")"))
                            {
                                // 実時間で待つ（パネルの遅延描画を越える・計測の間だけ止めておく用）
                                if (!float.TryParse(call.Substring(5, call.Length - 6), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) || sec < 0f)
                                {
                                    _errors.Add("window_calls: wait の秒数が数でない: " + call);
                                    ctrl.FreezeLiveForCapture(false);
                                    yield return true;
                                    yield break;
                                }
                                float t0 = Time.realtimeSinceStartup;
                                while (Time.realtimeSinceStartup - t0 < sec) yield return null;
                                continue;
                            }
                            var err = InvokeWindowCall(ctrl, call);
                            if (err != null)
                            {
                                _errors.Add("window_calls: " + call + ": " + err);
                                ctrl.FreezeLiveForCapture(false);
                                yield return true;
                                yield break;
                            }
                            yield return null;
                        }
                        if (_r.Cards == null)
                        {
                            ctrl.FreezeLiveForCapture(false);
                            if (_r.WindowAfterSeconds > 0f)
                            {
                                var log = new System.Text.StringBuilder();
                                float after = 0f;
                                while (after < _r.WindowAfterSeconds && !ctrl.IsGameOver)
                                {
                                    log.Append(after.ToString("F2", CultureInfo.InvariantCulture)).Append('\t').Append(ctrl.FrameStateForCapture()).Append('\n');
                                    after += Time.deltaTime;
                                    yield return null;
                                }
                                File.WriteAllText(Path.Combine(_dir, "window-after.txt"), log.ToString());
                                yield return Capture(ctrl, Path.Combine(_dir, "03-after.png"));
                                _shotFiles.Add("03-after.png");
                            }
                            yield return true;
                            yield break;
                        }
                    }
                    foreach (var id in _r.Cards)
                    {
                        if (!ctrl.TacticsCardSelectableForCapture(id))
                        {
                            _errors.Add("采配カードが手札に無いか押せない: " + id);
                            ctrl.FreezeLiveForCapture(false);
                            yield return true;
                            yield break;
                        }
                        ctrl.SelectSignForCapture(id);
                    }
                    yield return null; yield return null;
                    yield return Capture(ctrl, Path.Combine(_dir, "02-pressed.png"));
                    _shotFiles.Add("02-pressed.png");
                    ctrl.FreezeLiveForCapture(false);
                    float ran = 0f;
                    while (ran < _r.CardsAfterSeconds && !ctrl.IsGameOver)
                    {
                        ran += Time.deltaTime;
                        yield return null;
                    }
                    yield return Capture(ctrl, Path.Combine(_dir, "03-after.png"));
                    _shotFiles.Add("03-after.png");
                    yield return true;
                    yield break;
                }

                PitchBattingOverride sign = default;
                bool hasSign = !string.IsNullOrEmpty(_r.Sign) && Enum.TryParse(_r.Sign, true, out sign);
                float waited = 0f;
                bool prevWin = false;
                while (!ctrl.FieldPlayActiveForCapture)
                {
                    if (ctrl.IsGameOver || waited > _r.TimeoutSeconds) { yield return false; yield break; }
                    bool win = ctrl.AtWindowForCapture;
                    // 打撃の指示は1球ごとに消える＝毎球の窓の頭で押し直す
                    if (hasSign && win && !prevWin && ctrl.ManagerOnOffenseForCapture) ctrl.SelectPitchBattingForCapture(sign);
                    prevWin = win;
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }

                var play = ctrl.PlayingForCapture;   // 打席途中のプレー（ファウル等）も撮れる
                _result = play?.Result;
                ComputeAnchors(play);
                // 「end」はゲームが実際に再生を閉じる時刻の1コマ手前で置く（打席途中のプレーは play.Dur より手前で閉じる・#1264④で約0.3秒早まった）
                if (play != null && _anchors.ContainsKey("end"))
                    _anchors["end"] = Math.Max(0, Math.Min(_anchors["end"], ctrl.PlayCloseAtForCapture - 0.05));
                var shots = new List<(string Label, double T)>();
                foreach (var s in _r.Shots)
                {
                    if (TryResolve(s, out var t)) shots.Add((s, t));
                    else _errors.Add("撮る瞬間を解けない: " + s + "（このプレーに無い節目か、書式違い）");
                }
                double vFrom = 0, vTo = -1;
                bool video = !string.IsNullOrEmpty(_r.VideoFrom);
                if (video && !(TryResolve(_r.VideoFrom, out vFrom) && TryResolve(_r.VideoTo, out vTo)))
                {
                    _errors.Add($"連番の区間を解けない: {_r.VideoFrom} 〜 {_r.VideoTo}");
                    video = false;
                }

                // 固定刻みで1コマずつ進める（撮影の重さで再生の時計が飛ばない）。刻みは連番の fps か 60
                float step = 1f / (video ? _r.VideoFps : 60);
                Time.captureDeltaTime = step;
                int shotIdx = 0;
                shots.Sort((x, y) => x.T.CompareTo(y.T));
                int guard = 0;
                while (ctrl.FieldPlayActiveForCapture && guard++ < 20000)
                {
                    double clock = ctrl.PlayClockForCapture;
                    var rig = ctrl.DuelRigForCapture;
                    WriteProbes(clock, rig);
                    bool inVideo = video && clock >= vFrom && clock <= vTo;
                    bool shotDue = shotIdx < shots.Count && clock >= shots[shotIdx].T;
                    if (inVideo || shotDue)
                    {
                        ctrl.FreezeLiveForCapture(true);
                        if (inVideo)
                        {
                            var vdir = Path.Combine(_dir, "video");
                            Directory.CreateDirectory(vdir);
                            yield return Capture(ctrl, Path.Combine(vdir, "f" + _videoFrames.ToString("000", CultureInfo.InvariantCulture) + ".png"));
                            _videoFrames++;
                        }
                        while (shotIdx < shots.Count && clock >= shots[shotIdx].T)
                        {
                            var file = Slug(shots[shotIdx].Label) + ".png";
                            yield return Capture(ctrl, Path.Combine(_dir, file));
                            _shotFiles.Add(file);
                            shotIdx++;
                        }
                        ctrl.FreezeLiveForCapture(false);
                    }
                    if (!video && shotIdx >= shots.Count) break;
                    if (video && clock > vTo && shotIdx >= shots.Count) break;
                    yield return null;
                }
                Time.captureDeltaTime = 0f;
                for (; shotIdx < shots.Count; shotIdx++)
                    _errors.Add($"撮れなかった: {shots[shotIdx].Label}（t={shots[shotIdx].T:F2} の前に再生が終わった）");
                yield return true;
            }

            // ---- 節目 ----

            private void ComputeAnchors(PlaybackPlay play)
            {
                _anchors.Clear();
                if (play == null) return;
                _anchors["start"] = 0;
                // 守備ビューの再生はプレーの長さの約0.1秒手前で閉じる（2026-11-17 実測）＝最後に絵が出る時刻で置く。
                // 実際に閉じる時刻（PlayCloseAtForCapture）の方が早ければ、呼び出し側でそちらへ寄せる
                _anchors["end"] = Math.Max(0, play.Dur - 0.15);
                _anchors["result"] = play.ResAt;
                for (int i = 0; i < play.Ball.Count; i++)
                {
                    var s = play.Ball[i];
                    var eb = s as EndpointBallSegment;
                    bool isThrow = s is ThrowSegment || (eb != null && eb.Kind == BallSegmentKind.Throw);
                    if (isThrow && !_anchors.ContainsKey("release"))
                    {
                        _anchors["release"] = s.T0;
                        if (i > 0) _anchors["catch"] = play.Ball[i - 1].T1;
                    }
                    if (eb != null && eb.Kind == BallSegmentKind.Flight && !_anchors.ContainsKey("flight_end"))
                        _anchors["flight_end"] = s.T1;
                }
                if (play.Judgements.Count > 0) _anchors["judgement"] = play.Judgements[0].T;
            }

            /// <summary>"release-0.2" / "flight_end" / "1.5" を再生の時計[s]へ。</summary>
            private bool TryResolve(string spec, out double t)
            {
                t = 0;
                spec = spec.Trim();
                if (double.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out t)) return true;
                int k = spec.IndexOfAny(new[] { '+', '-' }, 1);
                var name = k > 0 ? spec.Substring(0, k) : spec;
                double off = 0;
                if (k > 0 && !double.TryParse(spec.Substring(k), NumberStyles.Float, CultureInfo.InvariantCulture, out off)) return false;
                if (!_anchors.TryGetValue(name, out var baseT)) return false;
                t = baseT + off;
                return true;
            }

            // ---- 検証・切り替え・実測 ----

            private void Validate()
            {
                if (!string.IsNullOrEmpty(_r.Force) && !ForcedOutcomes.TryParse(_r.Force, out _))
                    _errors.Add("force が ForcedOutcome に無い: " + _r.Force);
                if (!string.IsNullOrEmpty(_r.Sign) && !Enum.TryParse<PitchBattingOverride>(_r.Sign, true, out _))
                    _errors.Add("sign が PitchBattingOverride に無い: " + _r.Sign);
                if (!DebugScenarios.Catalog.TryGet(_r.Scenario, out _))
                    _errors.Add("scenario が scenarios.yaml に無い: " + _r.Scenario);
                foreach (var name in _r.Probes)
                {
                    var read = ResolveProbe(name);
                    if (read == null) _errors.Add("実測が DuelStageRig に無い: " + name);
                    else _probes.Add((name, read));
                }
            }

            private static Func<DuelStageRig, object> ResolveProbe(string name)
            {
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var t = typeof(DuelStageRig);
                foreach (var n in new[] { name, name + "ForCapture" })
                {
                    var m = t.GetMethod(n, bf, null, Type.EmptyTypes, null);
                    if (m != null && m.ReturnType != typeof(void)) return rig => m.Invoke(rig, null);
                    var p = t.GetProperty(n, bf);
                    if (p != null && p.GetIndexParameters().Length == 0) return rig => p.GetValue(rig);
                    var f = t.GetField(n, bf);
                    if (f != null) return rig => f.GetValue(rig);
                }
                return null;
            }

            private void ApplyOverrides()
            {
                const BindingFlags bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var kv in _r.Overrides)
                {
                    int dot = kv.Key.LastIndexOf('.');
                    var type = dot > 0 ? FindType(kv.Key.Substring(0, dot)) : null;
                    var member = dot > 0 ? kv.Key.Substring(dot + 1) : kv.Key;
                    var f = type?.GetField(member, bf);
                    var p = f == null ? type?.GetProperty(member, bf) : null;
                    var memberType = f?.FieldType ?? (p != null && p.CanWrite ? p.PropertyType : null);
                    if (memberType == null) { _errors.Add("切り替えが見つからない（static のフィールド/プロパティ）: " + kv.Key); continue; }
                    if (!TryConvert(kv.Value, memberType, out var value)) { _errors.Add($"切り替えの値を読めない: {kv.Key} = {kv.Value}"); continue; }
                    _restore.Add((f, p, f != null ? f.GetValue(null) : p.GetValue(null)));
                    if (f != null) f.SetValue(null, value); else p.SetValue(null, value);
                }
            }

            private void RestoreOverrides()
            {
                foreach (var (f, p, old) in _restore)
                {
                    if (f != null) f.SetValue(null, old); else p.SetValue(null, old);
                }
                _restore.Clear();
            }

            private static Type FindType(string simpleOrFull)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(simpleOrFull) ?? asm.GetType("KokoSim.Unity.Match." + simpleOrFull)
                            ?? asm.GetType("KokoSim.Unity.Debugging." + simpleOrFull);
                    if (t != null) return t;
                }
                return null;
            }

            private static bool TryConvert(string s, Type t, out object value)
            {
                value = null;
                var u = Nullable.GetUnderlyingType(t) ?? t;
                try
                {
                    if (u == typeof(bool)) { value = bool.Parse(s); return true; }
                    if (u.IsEnum) { value = Enum.Parse(u, s, true); return true; }
                    if (u == typeof(string)) { value = s; return true; }
                    value = Convert.ChangeType(s, u, CultureInfo.InvariantCulture);
                    return true;
                }
                catch { return false; }
            }

            private void WriteProbes(double clock, DuelStageRig rig)
            {
                if (_probes.Count == 0 || rig == null) return;
                var sb = new StringBuilder("{\"t\":").Append(clock.ToString("F4", CultureInfo.InvariantCulture));
                foreach (var (name, read) in _probes)
                {
                    string v;
                    try { v = Convert.ToString(read(rig), CultureInfo.InvariantCulture); }
                    catch (Exception e) { v = "ERR " + (e.InnerException ?? e).Message; }
                    sb.Append(",\"").Append(name).Append("\":\"").Append(Json(v)).Append('"');
                }
                _jsonl.WriteLine(sb.Append('}').ToString());
            }

            // ---- 出力 ----

            private void Finish(float t0)
            {
                Ok = _errors.Count == 0;
                var sb = new StringBuilder("{\n");
                sb.Append("  \"id\": \"").Append(Json(_r.Id)).Append("\",\n");
                sb.Append("  \"ok\": ").Append(Ok ? "true" : "false").Append(",\n");
                sb.Append("  \"scenario\": \"").Append(Json(_r.Scenario)).Append("\",\n");
                sb.Append("  \"seed\": ").Append(_seedUsed).Append(",\n");
                sb.Append("  \"attempts_used\": ").Append(_attemptUsed).Append(",\n");
                sb.Append("  \"result\": \"").Append(Json(_result ?? "")).Append("\",\n");
                sb.Append("  \"seconds\": ").Append((Time.realtimeSinceStartup - t0).ToString("F1", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"anchors\": {");
                bool first = true;
                foreach (var kv in _anchors)
                {
                    sb.Append(first ? "" : ", ").Append('"').Append(kv.Key).Append("\": ")
                      .Append(kv.Value.ToString("F3", CultureInfo.InvariantCulture));
                    first = false;
                }
                sb.Append("},\n  \"shots\": [");
                for (int i = 0; i < _shotFiles.Count; i++) sb.Append(i > 0 ? ", " : "").Append('"').Append(_shotFiles[i]).Append('"');
                sb.Append("],\n  \"video_frames\": ").Append(_videoFrames).Append(",\n  \"errors\": [");
                for (int i = 0; i < _errors.Count; i++) sb.Append(i > 0 ? ", " : "").Append('"').Append(Json(_errors[i])).Append('"');
                sb.Append("]\n}\n");
                WriteOutcome(_r.Id, Ok, _errors, sb.ToString());
                Debug.Log($"[capture] {_r.Id} {(Ok ? "DONE" : "FAILED")} " + string.Join(" / ", _errors));
            }

            private static string Json(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

            private static string Slug(string s)
            {
                var sb = new StringBuilder();
                foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '.' ? c : c == '+' ? 'p' : c == '-' ? 'm' : '_');
                return sb.ToString();
            }

            // ---- 撮影 ----

            /// <summary>
        /// window_calls の1件（issue #1533）を MatchLiveController の公開メソッドとして呼ぶ。
        /// 「Name」か「Name(arg)」（引数は1つまで・数か列挙の名前）。失敗の理由を返す（成功は null）。
        /// </summary>
        private static string InvokeWindowCall(MatchLiveController ctrl, string call)
        {
            string name = call.Trim(), arg = null;
            int open = name.IndexOf('(');
            if (open >= 0)
            {
                if (!name.EndsWith(")")) return "括弧が閉じていない";
                arg = name.Substring(open + 1, name.Length - open - 2).Trim();
                name = name.Substring(0, open).Trim();
                if (arg.Length == 0) arg = null;
            }
            MethodInfo method = null;
            foreach (var m in typeof(MatchLiveController).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != name) continue;
                if (m.GetParameters().Length == (arg == null ? 0 : 1)) { method = m; break; }
            }
            if (method == null) return "その名前と引数の数のメソッドが無い";
            object[] args = null;
            if (arg != null)
            {
                var t = method.GetParameters()[0].ParameterType;
                try
                {
                    if (t.IsEnum) args = new[] { Enum.Parse(t, arg, true) };
                    else if (t == typeof(string)) args = new object[] { arg.Trim('"') };
                    else args = new[] { Convert.ChangeType(arg, t, CultureInfo.InvariantCulture) };
                }
                catch (Exception e) { return "引数を " + t.Name + " にできない（" + e.Message + "）"; }
            }
            try { method.Invoke(ctrl, args); }
            catch (TargetInvocationException e) { return (e.InnerException ?? e).Message; }
            return null;
        }

        private IEnumerator Capture(MatchLiveController ctrl, string path)
            {
                yield return new WaitForEndOfFrame();
                var rig = ctrl.DuelRigForCapture;
                rig?.RenderNow();
                if (_r.View == "rig")
                {
                    if (rig?.Target == null) { _errors.Add("rig の描画先が無い（duel_view: false？）"); yield break; }
                    Save(rig.Target, new RectInt(0, 0, rig.Target.width, rig.Target.height), path);
                    yield break;
                }
                var ps = ctrl.GetComponent<UIDocument>().panelSettings;
                var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
                var rt = new RenderTexture(_r.Width, _r.Height, 24, RenderTextureFormat.ARGB32);
                rt.Create();
                ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                if (!_r.GameScale) ps.referenceResolution = new Vector2Int(_r.Width, _r.Height);
                ps.targetTexture = rt;
                yield return null;
                yield return new WaitForEndOfFrame();
                try
                {
                    // 盤面（左右の打順表・下の采配を除いた中央）。1600×900 で実測した枠を画面の大きさへ比例させる
                    var rect = _r.View == "board"
                        ? new RectInt(_r.Width * 282 / 1600, _r.Height * 230 / 900, _r.Width * 1036 / 1600, _r.Height * 652 / 900)
                        : new RectInt(0, 0, _r.Width, _r.Height);
                    Save(rt, rect, path);
                }
                finally
                {
                    ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
            }

            private static void Save(RenderTexture src, RectInt r, string path)
            {
                var tex = new Texture2D(r.width, r.height, TextureFormat.RGB24, false);
                var prev = RenderTexture.active;
                RenderTexture.active = src;
                tex.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }

            private static void EnsureOnlyMatchLive(bool on)
            {
                foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                {
                    if (!d.gameObject.scene.IsValid()) continue;
                    d.gameObject.SetActive(on && d.gameObject.name == "MatchLive");
                }
            }

            private static MatchLiveController FindController()
            {
                foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                    if (d.gameObject.scene.IsValid() && d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
                return null;
            }
        }
    }
}
#endif
