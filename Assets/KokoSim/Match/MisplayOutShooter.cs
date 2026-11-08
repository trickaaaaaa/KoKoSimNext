#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #815 の確認撮影。実シムの打席を進めて
    /// ①弾いたがアウト（お手玉の絵＋アウト・失策なし）②悪送球を拾い直してアウト（受け手なしの送球＋アウト）
    /// を1打席ずつ見つけ、3D守備ビュー（リグ直駆動）で時刻を進めながら撮る。判定テロップも記録する。
    /// </summary>
    public sealed class MisplayOutShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/misplay-out-815";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[i815] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);
            var fCurrent = typeof(MatchLiveController).GetField("_current", Priv);

            bool doneFumble = false, doneRecover = false;
            int pa;
            for (pa = 0; pa < 3000 && (!doneFumble || !doneRecover); pa++)
            {
                if (!ctrl.AdvanceForCapture())
                {
                    // 試合終了＝シードを変えて次の試合へ（ErrorKindPictureShooter.RestartWithSeed と同じ作法）
                    MatchLiveController.GameSeedOverrideForCapture = (ulong)(81500 + pa * 7919);
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

                bool hasError = false, batterOut = false;
                double outT = -1;
                foreach (var j in play.Judgements)
                {
                    if (j.Kind == JudgementKind.Error) hasError = true;
                    if (j.Kind == JudgementKind.Out && j.Label == "打") { batterOut = true; outT = j.T; }
                }
                if (hasError || !batterOut) continue;

                string kind = null;
                double keyT = -1;
                var bobble = HandleBobblePlanner.Plan(play);
                if (!doneFumble && bobble.Kind == HandleBobbleKind.Fumble) { kind = "fumble"; keyT = bobble.CatchT; }
                if (kind == null && !doneRecover)
                {
                    foreach (var s in play.Ball)
                    {
                        if (s is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Throw && eb.ToRole == null)
                        { kind = "recover"; keyT = eb.T0; break; }
                    }
                }
                if (kind == null) continue;

                Debug.Log($"[i815] {kind} 見つけた: {pa}打席目 「{play.Result}」 アウト={outT:F2} 起点={keyT:F2}");
                var rig = ctrl.DuelRigForCapture;
                if (rig == null || !rig.Available) { Debug.LogError("[i815] rig なし"); yield break; }
                rig.BeginFieldPlay(play, 0f);
                // 起点の少し前 → 起点 → 中間 → アウト → アウト後
                double[] ts = { keyT - 0.2, keyT + 0.3, keyT + (outT - keyT) * 0.5, outT - 0.05, outT + 0.3 };
                for (int i = 0; i < ts.Length; i++)
                {
                    rig.SetFieldTime((float)ts[i]);
                    yield return null;
                    rig.RenderNow();
                    var call = PlaybackEvaluator.JudgementAt(play, ts[i], 0.6);
                    Debug.Log($"[i815] {kind} t={ts[i]:F2} テロップ={(call.HasValue ? call.Value.Kind.ToString() : "なし")}");
                    yield return CaptureRig(rig, $"{kind}-{i}-t{ts[i]:F2}");
                }
                rig.EndFieldPlay();
                if (kind == "fumble") doneFumble = true; else doneRecover = true;
            }
            Debug.Log($"[i815] DONE {pa}打席 fumble={doneFumble} recover={doneRecover} → {Dir}");
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
