#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #979 の確認撮影。盗塁死（二盗・三盗）を、逸れなし・ショートバウンド・浮き・横の
    /// 4通りで組み、受け手が捕る形からタッチへ移る絵を撮る。画像と実測は out/issue-979/ に出す（コミットしない）。
    /// </summary>
    public sealed class TagScatterShooter : MonoBehaviour
    {
        private const string Dir = "out/issue-979";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly double[] Dts = { -0.45, -0.20, 0.0, 0.20 };

        private const string SpawnFlag = "KokoSim.TagScatterShooter.Spawn";
        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~TagScatter").AddComponent<TagScatterShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[i979] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "measure.tsv"), "");
            var rig = ctrl.DuelRigForCapture;
            if (rig == null || !rig.Available) { Debug.LogError("[i979] rig なし"); yield break; }
            rig.EnsureSize(960, 540);
            yield return null;

            var c0 = new BaserunningCoefficients();
            var field = new FieldGeometry();
            var cases = new (string Name, StealTarget Target, ThrowScatterModel.Scatter? Sc)[]
            {
                ("second-none", StealTarget.Second, null),
                ("second-shorthop", StealTarget.Second, new ThrowScatterModel.Scatter(5, -50)),
                ("second-high", StealTarget.Second, new ThrowScatterModel.Scatter(-10, 45)),
                ("second-side", StealTarget.Second, new ThrowScatterModel.Scatter(55, 0)),
                ("third-none", StealTarget.Third, null),
                ("third-shorthop", StealTarget.Third, new ThrowScatterModel.Scatter(5, -50)),
                ("third-high", StealTarget.Third, new ThrowScatterModel.Scatter(-10, 45)),
            };
            foreach (var cs in cases)
            {
                var tl = TimelineBuilder.BuildSteal(new Player(), new Player(), StealResult.CaughtStealing, c0, field,
                    target: cs.Target, throwScatter: cs.Sc);
                var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
                for (int i = 0; i < play.Ball.Count; i++)
                    if (play.Ball[i] is EndpointBallSegment e && e.Kind == BallSegmentKind.Throw)
                    { yield return Shoot(rig, play, tl, i, cs.Name); break; }
            }
            DuelStageRig.FieldCameraOverrideForCapture = null;
            Debug.Log($"[i979] DONE → {Dir}");
        }

        private IEnumerator Shoot(DuelStageRig rig, PlaybackPlay play, PlayTimeline tl, int idx, string name)
        {
            var seg = (EndpointBallSegment)play.Ball[idx];
            var a1 = seg.At(seg.T1 - seg.T0);
            var a0 = seg.At(0);
            var dir = new Vector3((float)(a1.X - a0.X), 0f, (float)(a1.Y - a0.Y)).normalized;
            var side = Vector3.Cross(Vector3.up, dir).normalized;
            var baseP = new Vector3((float)a1.X, 0f, (float)a1.Y);
            DuelStageRig.FieldCameraOverrideForCapture = (baseP - dir * 3.0f + side * 4.5f + Vector3.up * 2.2f, baseP + Vector3.up * 0.8f, 3.2f);
            rig.BeginFieldPlay(play, 0f);
            float tc = (float)seg.T1;
            float tRun = (float)tl.Runners[0].T1;
            float[] dts = { -0.2f, 0f, 0.1f, 0.2f, 0.3f, 0.5f };
            for (int i = 0; i < dts.Length; i++)
            {
                float t = tc + dts[i];
                for (float tt = t - 0.6f; tt < t; tt += 1f / 30f) { rig.Tick(1f / 30f); rig.SetFieldTime(tt); yield return null; }
                rig.SetFieldTime(t);
                rig.Tick(1f / 60f);
                yield return null;
                rig.RenderNow();
                yield return CaptureRig(rig, $"{name}-{i}");
            }
            var diag = rig.TagOutDiagForCapture();
            File.AppendAllText(Path.Combine(Dir, "measure.tsv"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "# {0}\t到達(判定)T={1:F2}\t捕球T={2:F2}\n{3}\n", name, tRun, tc, diag));
            rig.EndFieldPlay();
            yield return null;
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
