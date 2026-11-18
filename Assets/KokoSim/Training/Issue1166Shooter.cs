#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Training
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #1166: 弾道専用メニュー（フライ打撃/ゴロ打撃）が
    /// 練習計画画面に出ることの受け入れスクショ。①一覧の積み上げバーに新色（金・テラコッタ）が
    /// 出る ②モーダルのメニュー一覧（Custom編集）に新2メニューが選べる、の2枚を撮る。
    /// </summary>
    public sealed class Issue1166Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/issue-1166";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            RosterService.Reset();
            var roster = RosterService.Roster;
            if (roster.Count == 0) { Debug.LogError("[1166] roster empty"); yield break; }

            // 先頭選手にカスタム配分（フライ打撃90分＋ゴロ打撃60分＋打撃60分）を設定し、
            // 一覧の積み上げバーに新色が出ることを確認する。
            var p = roster[0];
            p.Plan = new TrainingPlan
            {
                Preset = TrainingPreset.Custom,
                Allocations = new[]
                {
                    new MenuAllocation(TrainingMenu.FlyBallHitting, 90),
                    new MenuAllocation(TrainingMenu.GroundBallHitting, 60),
                    new MenuAllocation(TrainingMenu.Batting, 60),
                },
            };

            var doc = Open("TrainingPlan");
            if (doc == null) { Debug.LogError("[1166] TrainingPlan screen not found"); yield break; }
            yield return null; yield return null;
            yield return Capture(doc, "00-table");

            // モーダルを開き、Custom編集のメニュー一覧（EditableMenus）に新2メニューが選べることを確認する。
            var ctrl = doc.GetComponent<TrainingPlanController>();
            var openModal = typeof(TrainingPlanController).GetMethod(
                "OpenModal", BindingFlags.NonPublic | BindingFlags.Instance);
            if (ctrl == null || openModal == null)
            {
                Debug.LogError("[1166] TrainingPlanController/OpenModal not found");
                yield break;
            }
            openModal.Invoke(ctrl, new object[] { 0 });
            yield return null; yield return null;
            yield return Capture(doc, "01-modal");

            Debug.Log("[1166] DONE all shots");
        }

        // 指定画面だけをアクティブにして開き直す（OnEnable を確実に走らせる。ManagementShooter と同型）。
        private static UIDocument Open(string screen)
        {
            UIDocument target = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) target = d;
                d.gameObject.SetActive(false);
            }
            if (target != null) target.gameObject.SetActive(true);
            return target;
        }

        private IEnumerator Capture(UIDocument doc, string file)
        {
            var ps = doc.panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null;
            yield return new WaitForEndOfFrame();
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
                Debug.Log("[1166] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }
    }
}
#endif
