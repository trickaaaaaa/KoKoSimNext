#if UNITY_EDITOR
using System.Collections;
using System.Globalization;
using System.IO;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #1079 / #1095 の動画。シナリオから試合を始め、捕球の少し前〜送球の後までを
    /// 1/30秒刻みで連番PNGに書き出す（毎コマ試合の時計を止めて撮る＝撮影の重さで時間が飛ばない）。
    /// 送球の振りかぶりで体を閉じる補正（#1095）の有無を切り替えて撮る。mp4 にするのは ffmpeg（手動）。
    /// 出力: screenshots/throw-video/{tag}/f000.png …
    /// </summary>
    public sealed class ThrowVideoShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/throw-video";
        private const int W = 1600, H = 900;
        private const float Step = 1f / 30f;

        public static (string Scenario, bool ClosedOff, string Tag)[] Runs =
        {
            ("of-grounder-runner-lf-lab", true, "lf-before"),
            ("of-grounder-runner-lf-lab", false, "lf-after"),
            ("if-grounder-3b-lab", true, "3b-before"),
            ("if-grounder-3b-lab", false, "3b-after"),
        };
        /// <summary>捕球の何秒前から撮るか／送球のリリースの何秒後まで撮るか。</summary>
        public static float LeadSeconds = 0.8f, TailSeconds = 0.9f;
        /// <summary>真＝送球でなく**最初の飛球の着地**の前後を撮る（issue #1084 の届かない飛球）。
        /// 着地の LeadSeconds 前〜TailSeconds 後。</summary>
        public static bool AroundFlightEnd;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            foreach (var run in Runs)
            {
                DuelStageRig.ThrowClosedWindupOffForCapture = run.ClosedOff;
                MatchLiveController.ScenarioIdForCapture = run.Scenario;
                MatchLiveController.GameSeedOverrideForCapture = 1;
                MatchLiveController.ManagerIsAwayOverrideForCapture = false;
                EnsureOnlyMatchLive(false);
                yield return null;
                EnsureOnlyMatchLive(true);
                yield return null; yield return null;
                var ctrl = FindController();
                if (ctrl == null) { Debug.LogError("[throw-video] controller なし"); yield break; }
                if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
                ctrl.SetSpeedForCapture(1f);

                float guard = 0f;
                while (!ctrl.FieldPlayActiveForCapture && guard < 60f) { guard += Time.deltaTime; yield return null; }
                var play = ctrl.CurrentPa?.Play;
                double tc = -1, release = -1;
                if (play != null && AroundFlightEnd)
                {
                    foreach (var s in play.Ball)
                        if (s is EndpointBallSegment fb && fb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Flight)
                        { tc = s.T1; release = s.T1; break; }
                }
                else if (play != null)
                    for (int i = 0; i < play.Ball.Count; i++)
                    {
                        var s = play.Ball[i];
                        bool isThrow = s is ThrowSegment
                            || (s is EndpointBallSegment eb && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw);
                        if (isThrow) { release = s.T0; if (i > 0) tc = play.Ball[i - 1].T1; break; }
                    }
                if (tc < 0) { Debug.LogWarning("[throw-video] 送球が見つからない: " + run.Tag); continue; }

                var outDir = Path.Combine(Dir, run.Tag);
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                Directory.CreateDirectory(outDir);

                // 撮り始めまでは普通に進める
                while (ctrl.FieldPlayActiveForCapture && ctrl.PlayClockForCapture < tc - LeadSeconds) yield return null;

                // ここから固定刻み: 1コマ進める→止めて撮る
                Time.captureDeltaTime = Step;
                int n = 0;
                while (ctrl.FieldPlayActiveForCapture && ctrl.PlayClockForCapture < release + TailSeconds && n < 300)
                {
                    ctrl.FreezeLiveForCapture(true);
                    yield return Capture(ctrl, Path.Combine(outDir, "f" + n.ToString("000", CultureInfo.InvariantCulture) + ".png"));
                    ctrl.FreezeLiveForCapture(false);
                    n++;
                    yield return null;   // captureDeltaTime ぶん進む
                }
                Time.captureDeltaTime = 0f;
                File.WriteAllText(Path.Combine(outDir, "info.txt"),
                    "frames=" + n + " catch=" + tc.ToString("F2", CultureInfo.InvariantCulture)
                    + " release=" + release.ToString("F2", CultureInfo.InvariantCulture) + " result=" + play?.Result
                    + "\nmiss=" + (ctrl.DuelRigForCapture?.MissDiagForCapture ?? "-")
                    + "\ngloveGap=" + (ctrl.DuelRigForCapture?.MissGloveGapForCapture ?? -1f).ToString("F2", CultureInfo.InvariantCulture));
            }
            DuelStageRig.ThrowClosedWindupOffForCapture = false;
            MatchLiveController.ScenarioIdForCapture = null;
            MatchLiveController.GameSeedOverrideForCapture = null;
            MatchLiveController.ManagerIsAwayOverrideForCapture = null;
            File.WriteAllText(Path.Combine(Dir, "DONE"), "ok");
            Debug.Log("[throw-video] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string path)
        {
            yield return new WaitForEndOfFrame();
            ctrl.DuelRigForCapture?.RenderNow();
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                // 守備ビューの盤面だけ（左右の打順表・下の采配は切る）
                const int x0 = 282, y0 = 230, cw = 1036, ch = 652;
                tex = new Texture2D(cw, ch, TextureFormat.RGB24, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(x0, y0, cw, ch), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
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
#endif
