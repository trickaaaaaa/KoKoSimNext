#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using KokoSim.Unity.Debugging;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #647 の受け入れ実測。<c>data/debug/scenarios.yaml</c> の終局シナリオ3本
    /// （三振で決着・打球で決着・サヨナラ単打。どれも強制発動で最初の打席に必ず終局する＝GameEndScenarioTests が固定）
    /// から試合を始め、対決ビューで再生して終局させる。終局から3秒間、対決ビュー4役の
    /// 「クリップ・時刻・速度・フェード残」を0.25秒刻みで記録し、終局直後と2.5秒後のスクショを撮る。
    /// 結果は screenshots/game-end-freeze-647/log.txt。
    /// </summary>
    public sealed class GameEndFreezeShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/game-end-freeze-647";
        private const int W = 1600, H = 900;
        /// <summary>撮るシナリオ（既定は3本全部）。Play中に差し替えれば1本だけ撮れる。</summary>
        public static string[] Scenarios = { "game-end-strikeout-lab", "game-end-groundout-lab", "game-end-walkoff-lab" };

        private readonly StringBuilder _log = new StringBuilder();

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            foreach (var id in Scenarios)
            {
                MatchLiveController.ScenarioIdForCapture = id;
                // 監督側が攻撃に回ると采配の窓が乱数の流れを変え、engine（GameEndScenarioTests）と別の展開になる
                // （実測: 9回裏のサヨナラが決着せず3-3のまま続いた）。どの場面も監督を守備側に置く＝裏の場面は先攻を持つ。
                MatchLiveController.ManagerIsAwayOverrideForCapture = DebugScenarios.Catalog.TryGet(id, out var def) && !def.Top;
                EnsureOnlyMatchLive(false);
                yield return null;
                EnsureOnlyMatchLive(true);
                yield return null; yield return null;
                var ctrl = FindController();
                if (ctrl == null) { Debug.LogError("[647] controller なし"); yield break; }
                if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
                ctrl.SetSpeedForCapture(1f);   // 実際の見え方（×1）で終局させる
                var prog = (MatchProgression)typeof(MatchLiveController)
                    .GetField("_prog", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ctrl);

                // 最後の打席の頭から終局までの経過を0.1秒刻みで残す（余韻がどの区間で出ているかを切り分ける）。
                var timeline = new StringBuilder();
                var guard = 0f;
                var nextSample = 0f;
                while (!ctrl.IsGameOver && guard < 120f)
                {
                    if (guard >= nextSample)
                    {
                        nextSample += 0.1f;
                        timeline.Append("  ").Append(guard.ToString("F1")).Append("s ").Append(ctrl.StageNameForCapture)
                            .Append(ctrl.FieldPlayActiveForCapture ? " field" : "")
                            .Append(ctrl.JudgementVisibleForCapture ? " [" + ctrl.JudgementTextForCapture + "]" : "")
                            .Append("  ").Append(ctrl.DuelRigForCapture != null ? ctrl.DuelRigForCapture.FreezeDiagForCapture() : "")
                            .Append('\n');
                    }
                    guard += Time.deltaTime;
                    yield return null;
                }
                if (!ctrl.IsGameOver)
                {
                    _log.AppendLine("=== " + id + ": 終局せず（タイムアウト） score=" + prog.AwayScore + "-" + prog.HomeScore
                                    + " pa=" + prog.ConfirmedPlateAppearances);
                    _log.Append(timeline);
                    continue;
                }

                // 終局すると Current は null に戻る＝engine の打席ログの最後の1件で決着の種類を見る。
                var log = prog.PlayLog;
                var last = log.Count > 0 ? log[log.Count - 1] : null;
                _log.AppendLine("=== " + id + " kind=" + Classify(last, prog) + " final=" + prog.AwayScore + "-" + prog.HomeScore
                                + " last=" + (last == null ? "-" : last.Inning + (last.IsTop ? "表 " : "裏 ") + last.Result)
                                + " live=" + guard.ToString("F1") + "s duel=" + ctrl.DuelViewOnForCapture);
                _log.Append("  -- 打席の頭から終局まで --\n").Append(timeline);
                var rig = ctrl.DuelRigForCapture;
                for (var i = 0; i <= 12; i++)
                {
                    _log.AppendLine("  t+" + (i * 0.25f).ToString("F2") + "  " + (rig != null ? rig.FreezeDiagForCapture() : "no-rig")
                                    + " stage=" + ctrl.StageNameForCapture);
                    if (i == 0) yield return Capture(ctrl, id + "-0s");
                    if (i == 10) yield return Capture(ctrl, id + "-2.5s");
                    var t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 0.25f) yield return null;
                }
                File.WriteAllText(Path.Combine(Dir, "log.txt"), _log.ToString());
            }
            _log.AppendLine("DONE");
            File.WriteAllText(Path.Combine(Dir, "log.txt"), _log.ToString());
            MatchLiveController.ScenarioIdForCapture = null;
            MatchLiveController.ManagerIsAwayOverrideForCapture = null;
            Debug.Log("[647] DONE");
        }

        private static string Classify(KokoSim.Engine.Match.Game.PlayLogEntry last, MatchProgression prog)
        {
            if (last == null) return "unknown";
            if (!last.IsTop && last.Inning >= 9 && prog.HomeScore > prog.AwayScore && last.RunsScored > 0) return "sayonara";
            if (last.Result == PlateAppearanceResult.Strikeout) return "strikeout";
            return last.Timeline != null ? "batted" : "other-" + last.Result;
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
