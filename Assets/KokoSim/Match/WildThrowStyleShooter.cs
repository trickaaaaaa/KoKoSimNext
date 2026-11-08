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
    /// 【開発用・エディタ限定・使い捨て】issue #812 の確認撮影。実シムの打席を進めて、悪送球の逸れ方
    /// （手前で弾む／横へ逸れる／高く抜ける）を1打席ずつ見つけ、3D守備ビュー（リグ直駆動）で
    /// 一塁手のそばを球が抜ける前後を撮る。
    /// </summary>
    public sealed class WildThrowStyleShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/wild-throw-812";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[i812] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);
            var fCurrent = typeof(MatchLiveController).GetField("_current", Priv);

            var done = new HashSet<WildThrowStyle>();
            int pa;
            for (pa = 0; pa < 8000 && done.Count < 3; pa++)
            {
                if (!ctrl.AdvanceForCapture())
                {
                    MatchLiveController.GameSeedOverrideForCapture = (ulong)(81200 + pa * 7919);
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
                if (play == null) continue;

                PlaybackBallSegment miss = null;
                foreach (var s in play.Ball)
                    if (s.MissedBy.HasValue && s.WildThrow.HasValue) { miss = s; break; }
                if (miss == null || done.Contains(miss.WildThrow.Value)) continue;
                var style = miss.WildThrow.Value;
                double missT = miss.T0 + miss.MissAfter;
                var b = miss.At(miss.MissAfter);
                Debug.Log($"[i812] {style} 見つけた: {pa}打席目 「{play.Result}」 抜ける瞬間={missT:F2} 球=({b.X:F1},{b.Y:F1},h{b.H:F2}) 捕り損ね={miss.MissedBy}");

                var rig = ctrl.DuelRigForCapture;
                if (rig == null || !rig.Available) { Debug.LogError("[i812] rig なし"); yield break; }
                rig.BeginFieldPlay(play, 0f);
                double[] ts = { missT - 0.5, missT - 0.15, missT, missT + 0.25, missT + 0.8, missT + 1.8 };
                for (int i = 0; i < ts.Length; i++)
                {
                    rig.SetFieldTime((float)ts[i]);
                    yield return null;
                    rig.RenderNow();
                    var fb = PlaybackEvaluator.FielderAt(play, miss.MissedBy.Value, ts[i]).Pos;
                    var ball = PlaybackEvaluator.BallAt(play, ts[i]);
                    Debug.Log($"[i812] {style} t={ts[i]:F2} 一塁手=({fb.X:F1},{fb.Y:F1}) 球={(ball.HasValue ? $"({ball.Value.X:F1},{ball.Value.Y:F1},h{ball.Value.H:F2})" : "-")}");
                    yield return CaptureRig(rig, $"{style}-{i}-t{ts[i]:F2}");
                }
                rig.EndFieldPlay();
                done.Add(style);
            }
            Debug.Log($"[i812] DONE {pa}打席 {string.Join(",", done)} → {Dir}");
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
