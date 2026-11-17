#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #1081 の受け入れ実測。<c>data/debug/scenarios.yaml</c> の
    /// foul-fly-out-lab（サードのファウルフライ）と fair-fly-out-lab（ライトフライ）から試合を始め、対決ビューで再生する。
    /// 守備の再生の頭から打席が畳まれた2.5秒後まで、球の高さ・球の影の有無・捕った野手のグラブとの距離を0.1秒刻みで
    /// 記録し、判定の直後・0.6秒後・畳んだ瞬間・畳んだ1秒後のスクショを撮る。
    /// 実測の結論（2026-11-17）: 球はずっとグラブの中にあり、足元に見えていたのは球の影（#401①）だった。
    /// 結果は screenshots/foul-fly-hold-1081/log-x{速度}.txt。
    /// </summary>
    public sealed class FoulFlyHoldShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/foul-fly-hold-1081";
        private const int W = 1600, H = 900;
        public static string[] Scenarios = { "foul-fly-out-lab", "fair-fly-out-lab" };

        private readonly StringBuilder _log = new StringBuilder();
        /// <summary>再生速度（試合画面の速度切替と同じ）。</summary>
        public static float Speed = 1f;
        private static string Suffix => "-x" + Speed.ToString("0");

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            foreach (var id in Scenarios)
            {
                MatchLiveController.ScenarioIdForCapture = id;
                MatchLiveController.GameSeedOverrideForCapture = 1;            // FoulFlyOutScenarioTests と同じチーム
                MatchLiveController.ManagerIsAwayOverrideForCapture = false;   // 1回表＝監督は守備側（采配の窓で乱数を変えない）
                EnsureOnlyMatchLive(false);
                yield return null;
                EnsureOnlyMatchLive(true);
                yield return null; yield return null;
                var ctrl = FindController();
                if (ctrl == null) { Debug.LogError("[1081] controller なし"); yield break; }
                if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
                ctrl.SetSpeedForCapture(Speed);
                var prog = (MatchProgression)typeof(MatchLiveController)
                    .GetField("_prog", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ctrl);

                _log.AppendLine("=== " + id + " (速度×" + Speed + ")");
                var guard = 0f;
                float judgeAt = -1f, foldAt = -1f, nextSample = 0f;
                int shot = 0;
                while (guard < 90f)
                {
                    var rig = ctrl.DuelRigForCapture;
                    bool field = ctrl.FieldPlayActiveForCapture;
                    bool judged = ctrl.JudgementVisibleForCapture && ctrl.JudgementTextForCapture.Contains("アウト");
                    if (judgeAt < 0f && field && judged) judgeAt = guard;
                    if (judgeAt >= 0f && foldAt < 0f && !field) foldAt = guard;
                    if ((field || judgeAt >= 0f) && guard >= nextSample)
                    {
                        nextSample = guard + 0.1f;
                        _log.Append(judgeAt >= 0f ? "  判定+" + (guard - judgeAt).ToString("F1") + "s" : "  判定前").Append(" clock=")
                            .Append(ctrl.PlayClockForCapture.ToString("F2")).Append(' ').Append(ctrl.StageNameForCapture)
                            .Append(field ? " field" : " (畳んだ後)")
                            .Append(ctrl.JudgementVisibleForCapture ? " [" + ctrl.JudgementTextForCapture + "]" : "")
                            .Append("  ").Append(rig != null ? rig.BallHoldDiagForCapture() : "no-rig").Append('\n');
                    }
                    if (judgeAt >= 0f)
                    {
                        var dt = guard - judgeAt;
                        if (shot == 0 && dt >= 0.1f) { shot++; yield return Capture(ctrl, id + Suffix + "-a-judge"); }
                        else if (shot == 1 && dt >= 0.6f) { shot++; yield return Capture(ctrl, id + Suffix + "-b-0.6s"); }
                        else if (shot == 2 && foldAt >= 0f) { shot++; yield return Capture(ctrl, id + Suffix + "-c-folded"); }
                        else if (shot == 3 && foldAt >= 0f && guard - foldAt >= 1.0f) { shot++; yield return Capture(ctrl, id + Suffix + "-d-folded+1s"); }
                    }
                    if (foldAt >= 0f && guard - foldAt >= 2.5f) break;
                    guard += Time.deltaTime;
                    yield return null;
                }
                _log.AppendLine("  畳まれた: 判定から " + (judgeAt >= 0f && foldAt >= 0f ? (foldAt - judgeAt).ToString("F1") : "-") + "s pa="
                                + prog.ConfirmedPlateAppearances + " 最後=" + (prog.PlayLog.Count > 0 ? prog.PlayLog[prog.PlayLog.Count - 1].Result.ToString() : "-"));
                File.WriteAllText(Path.Combine(Dir, "log" + Suffix + ".txt"), _log.ToString());
            }
            _log.AppendLine("DONE");
            File.WriteAllText(Path.Combine(Dir, "log" + Suffix + ".txt"), _log.ToString());
            MatchLiveController.ScenarioIdForCapture = null;
            MatchLiveController.GameSeedOverrideForCapture = null;
            MatchLiveController.ManagerIsAwayOverrideForCapture = null;
            Debug.Log("[1081] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
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
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
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
