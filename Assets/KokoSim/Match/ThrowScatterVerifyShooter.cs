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
    /// 【開発用・エディタ限定】issue #859 の確認撮影。実試合の打席を進めながら、送球の種類（一塁・併殺・三塁・本塁・
    /// 外野の返球）× 逸れ（小・浮き・ショートバウンド・横）の組み合わせごとに最初に出た場面を、受け手の正面
    /// （投げ手の側）から捕球の前後4コマで撮る。盗塁の送球は判定の値の配線を engine のテストで固めてあるので、
    /// ここでは engine の絵の組み立て（<see cref="TimelineBuilder.BuildSteal"/>）へ逸れを与えて撮る。
    /// 画像は out/issue-859/ に出す（コミットしない）。
    /// </summary>
    public sealed class ThrowScatterVerifyShooter : MonoBehaviour
    {
        private const string Dir = "out/issue-859";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly double[] Dts = { -0.45, -0.20, 0.0, 0.20 };

        private const string SpawnFlag = "KokoSim.ThrowScatterVerifyShooter.Spawn";
        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~ThrowScatterVerify").AddComponent<ThrowScatterVerifyShooter>();
        }

        private static string Category(EndpointBallSegment e)
        {
            if (e.ScatterIsShortHop) return "shorthop";
            if (e.ScatterVerticalM >= 0.25) return "high";
            if (System.Math.Abs(e.ScatterLateralM) >= 0.35) return "side";
            if (System.Math.Sqrt(e.ScatterLateralM * e.ScatterLateralM + e.ScatterVerticalM * e.ScatterVerticalM) < 0.20) return "small";
            return null;
        }

        private static string Kind(PlaybackPlay play, EndpointBallSegment e)
        {
            int throws = 0; bool toSecond = false;
            foreach (var s in play.Ball)
                if (s is EndpointBallSegment t && t.Kind == BallSegmentKind.Throw)
                {
                    throws++;
                    if (t.ToBase == "second") toSecond = true;
                }
            bool fromOf = e.FromRole is FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField;
            switch (e.ToBase)
            {
                case "first": return toSecond ? "dp-first" : (throws == 1 && !fromOf ? "first" : null);
                case "second": return fromOf ? "of-second" : (throws >= 2 ? "dp-second" : null);
                case "third": return "third";
                case "home": return "home";
                default: return null;
            }
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[i859v] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);
            var fCurrent = typeof(MatchLiveController).GetField("_current", Priv);

            var found = new Dictionary<string, (PlaybackPlay Play, int Idx)>();
            string[] kinds = { "first", "dp-second", "dp-first", "third", "home", "of-second" };
            string[] cats = { "small", "high", "shorthop", "side" };
            int pa;
            for (pa = 0; pa < 6000 && found.Count < kinds.Length * cats.Length; pa++)
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
                for (int i = 0; i < play.Ball.Count; i++)
                {
                    if (play.Ball[i] is not EndpointBallSegment e || e.Kind != BallSegmentKind.Throw || e.MissedBy.HasValue) continue;
                    var k = Kind(play, e); var c = Category(e);
                    if (k == null || c == null) continue;
                    var key = k + "-" + c;
                    if (found.ContainsKey(key)) continue;
                    found[key] = (play, i);
                    Debug.Log($"[i859v] {key}: {pa}打席目 {play.Result} 横{e.ScatterLateralM * 100:F0}cm 縦{e.ScatterVerticalM * 100:F0}cm");
                }
            }
            Debug.Log($"[i859v] 探索 {pa}打席 見つかった {found.Count} 組");

            var rig = ctrl.DuelRigForCapture;
            if (rig == null || !rig.Available) { Debug.LogError("[i859v] rig なし"); yield break; }
            rig.EnsureSize(960, 540);
            yield return null;

            foreach (var kv in found)
                yield return Shoot(rig, kv.Value.Play, kv.Value.Idx, kv.Key);

            // 盗塁の送球（二塁へ）。逸れを与えて engine の絵の組み立てを通す
            var c0 = new BaserunningCoefficients();
            var field = new FieldGeometry();
            var steals = new (string Name, double Lat, double Vert)[]
            {
                ("steal-small", 8, -5), ("steal-high", -10, 45), ("steal-shorthop", 5, -50), ("steal-side", 50, 0),
            };
            foreach (var st in steals)
            {
                var tl = TimelineBuilder.BuildSteal(new Player(), new Player(), StealResult.CaughtStealing, c0, field,
                    throwScatter: new ThrowScatterModel.Scatter(st.Lat, st.Vert));
                var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
                for (int i = 0; i < play.Ball.Count; i++)
                    if (play.Ball[i] is EndpointBallSegment e && e.Kind == BallSegmentKind.Throw)
                    { yield return Shoot(rig, play, i, st.Name); break; }
            }
            DuelStageRig.FieldCameraOverrideForCapture = null;
            Debug.Log($"[i859v] DONE → {Dir}");
        }

        private IEnumerator Shoot(DuelStageRig rig, PlaybackPlay play, int idx, string name)
        {
            var seg = (EndpointBallSegment)play.Ball[idx];
            var a0 = seg.At(0); var a1 = seg.At(seg.T1 - seg.T0);
            var dir = new Vector3((float)(a1.X - a0.X), 0f, (float)(a1.Y - a0.Y)).normalized;
            var baseP = new Vector3((float)a1.X, 0f, (float)a1.Y);
            // 受け手の正面（投げ手の側）から見る。横の逸れが画面の左右、縦の逸れが画面の上下に出る
            // 高めから見下ろす（塁へ駆け込む走者が受け手の前を横切って隠さないように）
            DuelStageRig.FieldCameraOverrideForCapture = (baseP - dir * 6.5f + Vector3.up * 3.2f, baseP + Vector3.up * 0.9f, 3.6f);
            rig.BeginFieldPlay(play, 0f);
            for (int i = 0; i < Dts.Length; i++)
            {
                float t = (float)(seg.T1 + Dts[i]);
                for (float tt = t - 0.6f; tt < t; tt += 1f / 30f) { rig.Tick(1f / 30f); rig.SetFieldTime(tt); yield return null; }
                rig.SetFieldTime(t);
                rig.Tick(1f / 60f);
                yield return null;
                rig.RenderNow();
                yield return CaptureRig(rig, $"{name}-{i}");
            }
            // 実測（design-18 §3.3: 装着は目測でなく実測）。受け手の捕球の収録・グラブの高さ・ミット中心→捕球点の距離と、
            // 逸れた球が届く高さを並べる
            var role = seg.ToRole?.ToString() ?? "?";
            foreach (var line in rig.FieldCatchLineDiagForCapture().Split('\n'))
                if (line.StartsWith(role + "\t"))
                    File.AppendAllText(Path.Combine(Dir, "measure.tsv"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}\t横{1:F2}\t縦{2:F2}\t届く高さ{3:F2}\t{4}\n", name, seg.ScatterLateralM, seg.ScatterVerticalM,
                        seg.ScatterArrivalHeightM, line.TrimEnd()));
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
