#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】判定テロップ（ストライク/ボール）が<b>捕手の捕球より後</b>に出ているかの実測。
    ///
    /// 監督指摘「ストライク、ボールの表示が、捕手が捕球するより前に出ている気がする」の切り分け。
    /// 絵では前後を判定できないので、投球時計（<c>MatchLiveController._pitchClock</c>）の目盛の上で
    /// 「捕球面到達の時刻」と「テロップが出た時刻」を並べて測る。
    /// 球はミットに入って 0.1 秒で消える（BallLingerSeconds）ので、テロップ時点の球時計を読むのでは
    /// 遅い球しか測れない＝発射のコマで到達時刻を先に確定しておく。
    /// 使い捨て。
    /// </summary>
    public sealed class MatchLiveCallTimingProbe : MonoBehaviour
    {
        [SerializeField] private float watchSeconds = 90f;
        [SerializeField] private float speed = 1f;
        [Tooltip("テロップが出たコマで撮る枚数（0=撮らない）。捕球済みかを絵でも確かめる用。")]
        [SerializeField] private int captureShots = 3;

        private MatchLiveController _ctrl;
        private FieldInfo _fCallOn, _fRig, _fPitchClock, _fPitchIdx, _fQueue, _fDuelOn;
        private FieldInfo _fBallT, _fBallArrival, _fBallFlight, _fCatchIndex;

        private bool _prevCallOn;
        private float _prevBallT = -1f;
        private float _arrivalClock = float.NaN;   // 捕球面へ着く投球時計の時刻
        private float _arrival, _flight;
        private int _catchIdx = -1;
        private readonly List<float> _gaps = new();
        private readonly List<float> _releaseGaps = new();   // リリース間隔の実測（間合いの確認用）
        private float _lastRelease = -1f;
        private int _samples, _notCaught, _unknown;
        private bool _shotPending;
        private int _shots;
        private const int W = 1600, H = 900;
        private const string Dir = "screenshots/matchlive-calltiming";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid() || d.gameObject.name != "MatchLive") continue;
                _ctrl = d.GetComponent<MatchLiveController>();
            }
            if (_ctrl == null) { Debug.LogError("[CallTiming] MatchLive が見つからない"); yield break; }

            const BindingFlags Bf = BindingFlags.NonPublic | BindingFlags.Instance;
            var t = typeof(MatchLiveController);
            _fCallOn = t.GetField("_pitchCallOn", Bf);
            _fRig = t.GetField("_duelRig", Bf);
            _fPitchClock = t.GetField("_pitchClock", Bf);
            _fPitchIdx = t.GetField("_pitchIdx", Bf);
            _fQueue = t.GetField("_paQueue", Bf);
            _fDuelOn = t.GetField("_duelOn", Bf);
            t.GetField("_speed", Bf).SetValue(_ctrl, speed);

            var rt = typeof(DuelStageRig);
            _fBallT = rt.GetField("_ballT", Bf);
            _fBallArrival = rt.GetField("_ballArrival", Bf);
            _fBallFlight = rt.GetField("_ballFlight", Bf);
            _fCatchIndex = rt.GetField("_catchIndex", Bf);

            var clock = 0f;
            while (clock < watchSeconds && !_ctrl.IsGameOver)
            {
                clock += Time.deltaTime;
                Sample();
                // 数値だけでなく絵でも見せる: テロップが出たコマで凍らせて撮る＝ミットに球が入っているか。
                if (_shots < captureShots && _shotPending)
                {
                    _shotPending = false;
                    _shots++;
                    yield return Capture("call-" + _shots.ToString("00"));
                }
                yield return null;
            }

            _gaps.Sort();
            var min = _gaps.Count > 0 ? _gaps[0] : 0f;
            var max = _gaps.Count > 0 ? _gaps[_gaps.Count - 1] : 0f;
            var sum = 0f; foreach (var g in _gaps) sum += g;
            _releaseGaps.Sort();
            var rsum = 0f; foreach (var g in _releaseGaps) rsum += g;
            // 打席をまたぐ間（打球再生・イニング間）を混ぜないよう、球間だけを見る中央値も出す。
            var rmed = _releaseGaps.Count > 0 ? _releaseGaps[_releaseGaps.Count / 2] : 0f;
            Debug.Log($"[CallTiming] リリース間隔 {_releaseGaps.Count}件 中央値{rmed:F2}s " +
                      $"平均{(_releaseGaps.Count > 0 ? rsum / _releaseGaps.Count : 0f):F2}s"); 
            Debug.Log($"[CallTiming] 結果 t={clock:F1}s 判定テロップ={_samples}回／" +
                      $"捕られる球で測れた={_gaps.Count}回 " +
                      $"（テロップ−捕球）平均{(_gaps.Count > 0 ? sum / _gaps.Count : 0f):F3}s " +
                      $"最小{min:+0.000;-0.000}s 最大{max:+0.000;-0.000}s ／" +
                      $"捕られない球={_notCaught}回 測れず={_unknown}回");
        }

        private void Sample()
        {
            var duelOn = (bool)_fDuelOn.GetValue(_ctrl);
            var rig = _fRig.GetValue(_ctrl);
            var clock = (float)_fPitchClock.GetValue(_ctrl);

            // ① 発射のコマを捕まえて「捕球面へ着く投球時計の時刻」を先に確定する。
            if (duelOn && rig != null)
            {
                var ballT = (float)_fBallT.GetValue(rig);
                if (ballT >= 0f && _prevBallT < 0f)
                {
                    _arrival = (float)_fBallArrival.GetValue(rig);
                    _flight = (float)_fBallFlight.GetValue(rig);
                    _catchIdx = (int)_fCatchIndex.GetValue(rig);
                    _arrivalClock = clock - ballT + _arrival;
                    if (_lastRelease > 0f) _releaseGaps.Add(Time.time - _lastRelease);
                    _lastRelease = Time.time;
                }
                _prevBallT = ballT;
            }

            // ② テロップが出たコマで読み比べる。
            var on = (bool)_fCallOn.GetValue(_ctrl);
            if (!on || _prevCallOn) { _prevCallOn = on; return; }
            _prevCallOn = true;
            _samples++;
            if (!duelOn || rig == null) return;
            if (_catchIdx < 0) { _notCaught++; return; }   // 打った・死球＝捕球の前後という話にならない
            if (float.IsNaN(_arrivalClock)) { _unknown++; return; }

            var gap = clock - _arrivalClock;
            _gaps.Add(gap);
            _shotPending = true;
            var idx = (int)_fPitchIdx.GetValue(_ctrl);
            var queued = ((System.Collections.ICollection)_fQueue.GetValue(_ctrl)).Count;
            Debug.Log($"[CallTiming] {idx + 1}/{queued}球目 テロップ={clock:F3}s 捕球={_arrivalClock:F3}s " +
                      $"（飛翔{_flight:F3}s→捕球面{_arrival:F3}s） → テロップ−捕球={gap:+0.000;-0.000}s");
        }

        /// <summary>テロップが出ているコマで進行を凍らせて撮る（既存シューターと同じ作法）。</summary>
        private IEnumerator Capture(string file)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            _ctrl.DuelRigForCapture?.RenderNow();   // RT差し替え中に素体が黒く焼ける回への保険
            yield return null;

            System.IO.Directory.CreateDirectory(Dir);
            var ps = _ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[CallTiming] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
            _ctrl.FreezeLiveForCapture(false);
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                var want = d.gameObject.name == "MatchLive";
                if (d.gameObject.activeSelf != want) d.gameObject.SetActive(want);
            }
        }
    }
}
#endif
