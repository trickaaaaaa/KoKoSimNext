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
    /// 【開発用・エディタ限定・使い捨て】issue #813 の確認撮影。実シムの打席を進めて、
    /// 外野・内野のフライ落球を1打席ずつ見つけ、3D守備ビュー（リグ直駆動）で落とす前後を撮る。
    /// </summary>
    public sealed class FlyDropShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/fly-drop-813";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[i813] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);
            var fCurrent = typeof(MatchLiveController).GetField("_current", Priv);

            var done = new HashSet<string>();
            int pa;
            for (pa = 0; pa < 6000 && done.Count < 2; pa++)
            {
                if (!ctrl.AdvanceForCapture())
                {
                    MatchLiveController.GameSeedOverrideForCapture = (ulong)(81300 + pa * 7919);
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
                if (play == null || !play.Fumbled) continue;
                double errT = -1;
                foreach (var j in play.Judgements) if (j.Kind == JudgementKind.Error) errT = j.T;
                if (errT < 0) continue;
                PlaybackBallSegment fly = null;
                foreach (var sg in play.Ball)
                    if (sg is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Flight && System.Math.Abs(eb.T1 - errT) < 0.05) fly = sg;
                if (fly == null) continue;
                var p = fly.At(fly.T1 - fly.T0);
                var kind = System.Math.Sqrt(p.X * p.X + p.Y * p.Y) > 60 ? "outfield" : "infield";
                if (done.Contains(kind)) continue;
                Debug.Log($"[i813] {kind} 見つけた: {pa}打席目 「{play.Result}」 落球={errT:F2} 点=({p.X:F1},{p.Y:F1})");

                var rig = ctrl.DuelRigForCapture;
                if (rig == null || !rig.Available) { Debug.LogError("[i813] rig なし"); yield break; }
                rig.BeginFieldPlay(play, 0f);
                double[] ts = { errT - 0.4, errT - 0.1, errT, errT + 0.2, errT + 0.45, errT + 1.2 };
                for (int i = 0; i < ts.Length; i++)
                {
                    rig.SetFieldTime((float)ts[i]);
                    yield return null;
                    rig.RenderNow();
                    var ball = PlaybackEvaluator.BallAt(play, ts[i]);
                    Debug.Log($"[i813] {kind} t={ts[i]:F2} 球(engine)={(ball.HasValue ? $"({ball.Value.X:F1},{ball.Value.Y:F1},h{ball.Value.H:F2})" : "-")}");
                    yield return CaptureRig(rig, $"{kind}-{i}-t{ts[i]:F2}");
                }
                rig.EndFieldPlay();
                done.Add(kind);
            }
            Debug.Log($"[i813] DONE {pa}打席 {string.Join(",", done)} → {Dir}");
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
