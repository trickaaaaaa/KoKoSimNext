#if UNITY_EDITOR
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #1079 の見比べ。<c>data/debug/scenarios.yaml</c> の of-grounder-*-lab から試合を始め、
    /// 外野手が転がる球を捕る前〜投げ始めを数コマ撮る。走者ありは収録の候補（<see cref="DuelStageRig.OutfieldRunThroughPick"/>）
    /// ごとに撮り直す。結果は screenshots/of-run-through-1079/。
    /// </summary>
    public sealed class OutfieldRunThroughShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/of-run-through-1079";
        private const int W = 1600, H = 900;

        /// <summary>この文字列を含むシナリオだけ撮る（null＝全部）。</summary>
        public static string Only;
        /// <summary>真なら送球の振りかぶりで体を閉じない（issue #1095 の見比べ用の旧挙動）。</summary>
        public static bool ClosedOff;
        /// <summary>外野手の送球の収録を強制する添字（−1＝距離で選ぶ通常どおり）。</summary>
        public static int ThrowAnchor = -1;

        public static (string Scenario, DuelStageRig.OutfieldRunThroughPick Pick)[] Runs =
        {
            ("of-grounder-runner-lf-lab", DuelStageRig.OutfieldRunThroughPick.TypeF),
            ("of-grounder-runner-cf-lab", DuelStageRig.OutfieldRunThroughPick.TypeF),
            ("of-grounder-runner-rf-lab", DuelStageRig.OutfieldRunThroughPick.TypeF),
            ("of-grounder-norunner-lab", DuelStageRig.OutfieldRunThroughPick.Current),
            // 内野ゴロの一塁送球（issue #1095: 送球の振りかぶりで体を閉じる補正の確認）
            ("if-grounder-3b-lab", DuelStageRig.OutfieldRunThroughPick.Current),
        };
        /// <summary>捕る瞬間からの相対秒（最後の1枚は投げ始めの直前に別に撮る）。</summary>
        public static float[] Offsets = { -0.45f, -0.20f, 0f, 0.30f, 0.60f, 0.90f };

        private readonly StringBuilder _log = new StringBuilder();

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            DuelStageRig.OutfieldThrowAnchorForCapture = ThrowAnchor;
            DuelStageRig.ThrowClosedWindupOffForCapture = ClosedOff;
            foreach (var run in Runs)
            {
                if (Only != null && !run.Scenario.Contains(Only)) continue;
                DuelStageRig.OutfieldRunThroughPickForCapture = run.Pick;
                MatchLiveController.ScenarioIdForCapture = run.Scenario;
                MatchLiveController.GameSeedOverrideForCapture = 1;
                MatchLiveController.ManagerIsAwayOverrideForCapture = false;
                EnsureOnlyMatchLive(false);
                yield return null;
                EnsureOnlyMatchLive(true);
                yield return null; yield return null;
                var ctrl = FindController();
                if (ctrl == null) { Debug.LogError("[1079] controller なし"); yield break; }
                if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
                ctrl.SetSpeedForCapture(1f);

                string tag = (ClosedOff ? "open-" : "") + (ThrowAnchor >= 0 ? "throw" + ThrowAnchor + "-" : "") + run.Scenario.Replace("of-grounder-", "").Replace("-lab", "") + "-" + run.Pick;
                float guard = 0f;
                while (!ctrl.FieldPlayActiveForCapture && guard < 60f) { guard += Time.deltaTime; yield return null; }
                var play = ctrl.CurrentPa?.Play;
                double tc = -1, release = -1;
                if (play != null)
                {
                    for (int i = 0; i < play.Ball.Count; i++)
                    {
                        var s = play.Ball[i];
                        bool isThrow = s is ThrowSegment || (s is EndpointBallSegment eb && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw);
                        if (isThrow) { release = s.T0; if (i > 0) tc = play.Ball[i - 1].T1; break; }
                    }
                }
                _log.AppendLine("=== " + tag + " 結果=" + (play?.Result ?? "?") + " 捕球=" + tc.ToString("F2", CultureInfo.InvariantCulture)
                                + " 投げ始め=" + release.ToString("F2", CultureInfo.InvariantCulture));
                if (tc < 0) { _log.AppendLine("  捕球が見つからない"); continue; }

                int shot = 0;
                bool releaseShot = false;
                while (ctrl.FieldPlayActiveForCapture)
                {
                    var clock = ctrl.PlayClockForCapture;
                    if (shot < Offsets.Length && clock >= tc + Offsets[shot])
                    {
                        yield return Capture(ctrl, tag + "-" + shot + "-c" + (Offsets[shot] >= 0 ? "+" : "") + Offsets[shot].ToString("0.00", CultureInfo.InvariantCulture));
                        _log.AppendLine("  " + Offsets[shot].ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + "s " + ctrl.DuelRigForCapture?.CatchDiagForCapture());
                        shot++;
                        continue;
                    }
                    if (!releaseShot && release > 0 && clock >= release - 0.05)
                    {
                        releaseShot = true;
                        yield return Capture(ctrl, tag + "-9-release");
                    }
                    if (shot >= Offsets.Length && releaseShot && clock > release + 0.4) break;
                    yield return null;
                }
                // 送球の体の向きの実測（腰・胸と的の差、投げ始め→リリースで腰が回った量）
                _log.Append(ctrl.DuelRigForCapture?.FieldThrowDiagForCapture());
                File.WriteAllText(Path.Combine(Dir, "log.txt"), _log.ToString());
            }
            DuelStageRig.OutfieldRunThroughPickForCapture = DuelStageRig.OutfieldRunThroughPick.TypeF;
            DuelStageRig.OutfieldThrowAnchorForCapture = -1;
            DuelStageRig.ThrowClosedWindupOffForCapture = false;
            MatchLiveController.ScenarioIdForCapture = null;
            MatchLiveController.GameSeedOverrideForCapture = null;
            MatchLiveController.ManagerIsAwayOverrideForCapture = null;
            _log.AppendLine("DONE");
            File.WriteAllText(Path.Combine(Dir, "log.txt"), _log.ToString());
            Debug.Log("[1079] DONE");
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
