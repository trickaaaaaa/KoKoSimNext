#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #830 ②の確認撮影。実シムの打席を進めて、
    /// 内野のポップフライ（頂点15m以上）を捕手・投一三・二遊で1打席ずつ見つけ、3D守備ビュー（リグ直駆動）で撮る。
    /// </summary>
    public sealed class PopupShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/popup-830";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[i830b] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);
            var fCurrent = typeof(MatchLiveController).GetField("_current", Priv);

            var done = new HashSet<string>();
            int pa;
            for (pa = 0; pa < 4000 && done.Count < 3; pa++)
            {
                if (!ctrl.AdvanceForCapture())
                {
                    MatchLiveController.GameSeedOverrideForCapture = (ulong)(83000 + pa * 7919);
                    var go = ctrl.gameObject;
                    go.SetActive(false);
                    yield return null;
                    go.SetActive(true);
                    yield return null; yield return null;
                    ctrl = FindController();
                    continue;
                }
                var cur = fCurrent.GetValue(ctrl);
                var play = cur?.GetType().GetProperty("Play")?.GetValue(cur) as PlaybackPlay;
                if (play == null || play.Result == null || !play.Result.EndsWith("フライ") || play.Result.Contains("ファウル")) continue;
                var kind = play.Result.StartsWith("キャッチャー") ? "catcher"
                    : play.Result.StartsWith("ピッチャー") || play.Result.StartsWith("ファースト") || play.Result.StartsWith("サード") ? "corner"
                    : play.Result.StartsWith("セカンド") || play.Result.StartsWith("ショート") ? "middle" : null;
                if (kind == null) continue;
                // 高く上がった打球（ポップフライ）だけ: 滞空中の最高点で判定
                double apex = 0;
                foreach (var sg in play.Ball)
                    if (sg is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Flight)
                        for (int k = 0; k <= 20; k++) apex = System.Math.Max(apex, sg.At((sg.T1 - sg.T0) * k / 20.0).H);
                if (apex < 15.0) continue;
                if (done.Contains(kind)) continue;
                double outT = -1;
                foreach (var j in play.Judgements) if (j.Kind == JudgementKind.Out && j.Label == "打") outT = j.T;
                if (outT < 0) continue;
                Debug.Log($"[i830b] {kind} 見つけた: {pa}打席目 「{play.Result}」 捕球={outT:F2} 頂点={apex:F1}m");

                var rig = ctrl.DuelRigForCapture;
                if (rig == null || !rig.Available) { Debug.LogError("[i830b] rig なし"); yield break; }
                rig.BeginFieldPlay(play, 0f);
                double[] ts = { 1.0, outT - 1.2, outT - 0.4, outT, outT + 0.5 };
                for (int i = 0; i < ts.Length; i++)
                {
                    rig.SetFieldTime((float)ts[i]);
                    yield return null;
                    rig.RenderNow();
                    var ball = PlaybackEvaluator.BallAt(play, ts[i]);
                    var call = PlaybackEvaluator.JudgementAt(play, ts[i], 0.6);
                    Debug.Log($"[i830b] {kind} t={ts[i]:F2} 球={(ball.HasValue ? $"({ball.Value.X:F1},{ball.Value.Y:F1},h{ball.Value.H:F2})" : "-")} テロップ={(call.HasValue ? call.Value.Kind.ToString() : "なし")}");
                    yield return CaptureRig(rig, $"{kind}-{i}-t{ts[i]:F2}");
                }
                rig.EndFieldPlay();
                done.Add(kind);
            }
            Debug.Log($"[i830b] DONE {pa}打席 {string.Join(",", done)} → {Dir}");
        }

        private IEnumerator CaptureRig(DuelStageRig rig, string file)
        {
            yield return null;
            if (rig.Target == null) yield break;
            var tex = new Texture2D(rig.Target.width, rig.Target.height, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rig.Target;
            tex.ReadPixels(new Rect(0, 0, rig.Target.width, rig.Target.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
            Destroy(tex);
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
