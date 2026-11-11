#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #859 の見せ方の案出し撮影。実シムの一塁ゴロ（逸れなしの送球）を1打席見つけ、
    /// 同じ打席へ逸れの値（小／大・高／大・低）を与えて4案（現状／A 球だけ／B 球＋受け手／C 受け手だけ）を撮る。
    /// 悪送球は #812 の既存の絵を1本撮る。
    /// </summary>
    public sealed class ThrowScatterShooter : MonoBehaviour
    {
        private const string Dir = "out/overnight/issue-859";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private struct Scn { public string Name; public double Lat, Vert; }
        private static readonly Scn[] Scns =
        {
            new Scn { Name = "small", Lat = 0.15, Vert = 0.10 },
            new Scn { Name = "largeHigh", Lat = -0.40, Vert = 0.30 },
            new Scn { Name = "largeLow", Lat = 0.15, Vert = -0.45 },
        };
        private static readonly string[] Modes = { "0now", "A", "B", "C" };

        private const string SpawnFlag = "KokoSim.ThrowScatterShooter.Spawn";
        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~ThrowScatterAuto").AddComponent<ThrowScatterShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[i859] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);
            var fCurrent = typeof(MatchLiveController).GetField("_current", Priv);

            PlaybackPlay plain = null; int plainIdx = -1;
            PlaybackPlay wild = null; PlaybackBallSegment wildSeg = null;
            int pa;
            for (pa = 0; pa < 8000 && (plain == null || wild == null); pa++)
            {
                if (!ctrl.AdvanceForCapture())
                {
                    MatchLiveController.GameSeedOverrideForCapture = (ulong)(85900 + pa * 7919);
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
                if (wild == null)
                    foreach (var s in play.Ball)
                        if (s.MissedBy.HasValue && s.WildThrow == WildThrowStyle.Wide) { wild = play; wildSeg = s; break; }
                if (plain == null)
                {
                    int n = 0, idx = -1;
                    for (int i = 0; i < play.Ball.Count; i++)
                        if (play.Ball[i] is EndpointBallSegment e && e.Kind == BallSegmentKind.Throw)
                        { n++; if (e.ToBase == "first" && !e.MissedBy.HasValue && e.ToRole.HasValue && e.FromRole.HasValue && e.FromRole != FieldPosition.Catcher) idx = i; }
                    if (n == 1 && idx >= 0) { plain = play; plainIdx = idx; Debug.Log($"[i859] 一塁ゴロ見つけた {pa}打席目 {play.Result}"); }
                }
            }
            Debug.Log($"[i859] 探索 {pa}打席 plain={(plain != null)} wild={(wild != null)}");
            var rig = ctrl.DuelRigForCapture;
            if (rig == null || !rig.Available) { Debug.LogError("[i859] rig なし"); yield break; }
            rig.EnsureSize(960, 540);
            yield return null;

            if (plain != null)
            {
                var seg = (EndpointBallSegment)plain.Ball[plainIdx];
                double tc = seg.T1;
                double[] dts = { -0.45, -0.20, 0.0, 0.20 };
                var a0 = seg.At(0); var a1 = seg.At(seg.T1 - seg.T0);
                var dir = new Vector3((float)(a1.X - a0.X), 0f, (float)(a1.Y - a0.Y)).normalized;
                var baseP = new Vector3((float)a1.X, 0f, (float)a1.Y);
                // 受け手の正面（投げ手の側）から見る。横の逸れが画面の左右、縦の逸れが画面の上下に出る
                DuelStageRig.CameraOverrideForCapture = (baseP - dir * 7.5f + Vector3.up * 1.5f, baseP + Vector3.up * 1.0f, 3.4f);
                foreach (var scn in Scns)
                    for (int m = 0; m < Modes.Length; m++)
                    {
                        var play = plain;
                        DuelStageRig.ThrowScatterReaction = m >= 2;
                        if (m > 0)
                        {
                            var list = new System.Collections.Generic.List<PlaybackBallSegment>(plain.Ball);
                            list[plainIdx] = seg.WithThrowScatter(scn.Lat, scn.Vert, shapeBall: m != 3);
                            play = plain with { Ball = list };
                        }
                        rig.BeginFieldPlay(play, 0f);
                        for (int i = 0; i < dts.Length; i++)
                        {
                            float t = (float)(tc + dts[i]);
                            // 捕球の収録を頭出しから流すため、少し手前から時間を進めて追いつかせる
                            for (float tt = t - 0.5f; tt < t; tt += 1f / 30f) { rig.Tick(1f / 30f); rig.SetFieldTime(tt); yield return null; }
                            rig.SetFieldTime(t);
                            rig.Tick(1f / 60f);
                            yield return null;
                            rig.RenderNow();
                            yield return CaptureRig(rig, $"{scn.Name}-{Modes[m]}-{i}");
                        }
                        rig.EndFieldPlay();
                        yield return null;
                    }
                DuelStageRig.ThrowScatterReaction = false;
                DuelStageRig.CameraOverrideForCapture = null;
            }
            if (wild != null)
            {
                double missT = wildSeg.T0 + wildSeg.MissAfter;
                double[] dts = { -0.5, -0.15, 0.0, 0.6 };
                var w0 = wildSeg.At(0); var w1 = wildSeg.At(wildSeg.MissAfter);
                var wdir = new Vector3((float)(w1.X - w0.X), 0f, (float)(w1.Y - w0.Y)).normalized;
                var wbase = new Vector3((float)w1.X, 0f, (float)w1.Y);
                DuelStageRig.CameraOverrideForCapture = (wbase - wdir * 7.5f + Vector3.up * 1.5f, wbase + Vector3.up * 1.0f, 3.4f);
                rig.BeginFieldPlay(wild, 0f);
                for (int i = 0; i < dts.Length; i++)
                {
                    float t = (float)(missT + dts[i]);
                    for (float tt = t - 0.5f; tt < t; tt += 1f / 30f) { rig.Tick(1f / 30f); rig.SetFieldTime(tt); yield return null; }
                    rig.SetFieldTime(t);
                    rig.Tick(1f / 60f);
                    yield return null;
                    rig.RenderNow();
                    yield return CaptureRig(rig, $"wild-now-{i}");
                }
                rig.EndFieldPlay();
                DuelStageRig.CameraOverrideForCapture = null;
            }
            Debug.Log($"[i859] DONE → {Dir}");
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
