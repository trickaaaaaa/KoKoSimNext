#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】送球の弧（issue #764）の見比べ。使い捨て。ChargePlayShooter と同じ作法。
    ///
    /// <para>実試合を進めて内野→一塁の送球を拾い、見かけの送球速度（距離÷飛翔時間）を記録する。
    /// 速い送球（強肩）と遅い送球（弱肩）をそれぞれ最初の1本ずつ、飛んでいる途中の3コマ（25/50/75%）で撮る。
    /// 弧の高さは飛翔時間だけで決まる（g·T²/8）ので、速い送球ほど低く見えるはず。</para>
    ///
    /// 出力: screenshots/throw-arc-764/*.png ／ out/throw-arc-764/throws.tsv
    /// </summary>
    public sealed class ThrowArcShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/throw-arc-764";
        private const string OutDir = "out/throw-arc-764";
        private const string SpawnFlag = "KokoSim.ThrowArcShooter.Spawn";

        private const int MaxGames = 10;
        private const int MaxPlatesPerGame = 200;

        /// <summary>強肩とみなす見かけの送球速度[km/h]（肩90≈135km/h）。</summary>
        private const double StrongKmh = 130.0;
        /// <summary>弱肩とみなす見かけの送球速度[km/h]（肩30≈105km/h）。</summary>
        private const double WeakKmh = 106.0;
        /// <summary>見比べの条件を揃えるため、送球距離はこの範囲（三遊間〜二遊間→一塁）に限る[m]。</summary>
        private const double MinDistM = 30.0, MaxDistM = 42.0;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~ThrowArcAuto").AddComponent<ThrowArcShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(OutDir);

            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[#764Shot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var log = new StringBuilder("試合\t打席\t投げ手\t距離[m]\t飛翔[s]\t速度[km/h]\t頂点の上がり[m]\t撮った\n");
            var shotStrong = false;
            var shotWeak = false;
            var throws = 0;

            for (var game = 1; game <= MaxGames && !(shotStrong && shotWeak); game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(76400 + game * 4231));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;

                    // ⚠ 解決した直後の同じフレームで見る（次の Advance で再生ごと捨てられる）。
                    var play = _ctrl.CurrentPa?.Play;
                    var th = FindInfieldThrowToFirst(play);
                    if (th == null) continue;

                    var dur = th.T1 - th.T0;
                    var a = th.At(0);
                    var b = th.At(dur);
                    var dist = System.Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    if (dur <= 0 || dist < 1) continue;
                    var kmh = dist / dur * 3.6;
                    var rise = th.At(dur / 2).H - a.H;
                    throws++;

                    string tag = null;
                    if (dist >= MinDistM && dist <= MaxDistM)
                    {
                        if (!shotStrong && kmh >= StrongKmh) tag = "strong";
                        else if (!shotWeak && kmh <= WeakKmh) tag = "weak";
                    }

                    log.Append(game).Append('\t').Append(plate).Append('\t').Append(th.FromRole).Append('\t')
                       .Append(dist.ToString("F1")).Append('\t').Append(dur.ToString("F2")).Append('\t')
                       .Append(kmh.ToString("F0")).Append('\t').Append(rise.ToString("F2")).Append('\t')
                       .Append(tag ?? "—").Append('\n');

                    if (tag == null) continue;
                    if (tag == "strong") shotStrong = true; else shotWeak = true;
                    yield return ShootThrow(tag, th, kmh, rise, dist, game, plate);
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "throws.tsv"), log.ToString());
            Debug.Log($"[#764Shot] 内野→一塁の送球 {throws} 本 / 強肩 {(shotStrong ? "撮影" : "なし")}" +
                      $" / 弱肩 {(shotWeak ? "撮影" : "なし")} → {ShotDir}");
            Time.captureDeltaTime = 0f;
        }

        private IEnumerator RestartWithSeed(ulong seed)
        {
            MatchLiveController.GameSeedOverrideForCapture = seed;
            _liveGo.SetActive(false);
            yield return null;
            _liveGo.SetActive(true);
            yield return null; yield return null;
        }

        private static EndpointBallSegment FindInfieldThrowToFirst(PlaybackPlay play)
        {
            if (play?.Ball == null) return null;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null || e.Kind != BallSegmentKind.Throw || e.ToBase != "first") continue;
                if (e.FromRole == null) continue;
                var r = e.FromRole.Value;
                if (r == FieldPosition.SecondBase || r == FieldPosition.ThirdBase || r == FieldPosition.Shortstop) return e;
            }
            return null;
        }

        private IEnumerator ShootThrow(string tag, EndpointBallSegment th, double kmh, double rise, double dist, int game, int plate)
        {
            var waited = 0;
            while (!_ctrl.FieldPlayActiveForCapture && waited++ < 1800) yield return null;
            if (!_ctrl.FieldPlayActiveForCapture)
            {
                Debug.LogWarning($"[#764Shot] 守備再生が始まらないまま打ち切り（{game}試合目 {plate}打席目）");
                yield break;
            }
            var dur = th.T1 - th.T0;
            yield return WaitClock(th.T0 + dur * 0.25);
            yield return ShootOne($"{tag}-1-25pct", false);
            yield return WaitClock(th.T0 + dur * 0.50);
            yield return ShootOne($"{tag}-2-50pct", false);
            yield return WaitClock(th.T0 + dur * 0.75);
            yield return ShootOne($"{tag}-3-75pct", false);
            Debug.Log($"[#764Shot] {tag}: {th.FromRole}→一塁 {dist:F1}m {dur:F2}s {kmh:F0}km/h 上がり{rise:F2}m（{game}試合目 {plate}打席目）");
        }

        private IEnumerator WaitClock(double t)
        {
            var guard = 0;
            while (_ctrl.FieldPlayActiveForCapture && _ctrl.PlayClockForCapture < t && guard++ < 900) yield return null;
        }

        private IEnumerator ShootOne(string file, bool overhead)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            if (!overhead) _ctrl.DuelRigForCapture?.RenderNow();
            yield return null;

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
                File.WriteAllBytes(Path.Combine(ShotDir, file + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
                _ctrl.FreezeLiveForCapture(false);
            }
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }

        private static MatchLiveController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
            }
            return null;
        }
    }
}
#endif
