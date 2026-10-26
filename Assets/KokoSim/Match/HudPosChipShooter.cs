#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Nation;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】打者HUDの守備位置チップ（issue #658）の確認撮影。白字と被る明るい校色
    /// （山吹）の高校で撮り、チップの実際の地色・字色を値で記録する（全校共通の芝の緑 #2E7D4F になっていること）。
    /// 候補の見比べ（空の青・芝の緑・土色・白枠）はこの撮影の旧版で行い、芝の緑が選ばれた。
    /// </summary>
    public sealed class HudPosChipShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots";

        public static string FileName = "i658-hud-pos";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[HudPosChipShot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            // 白い文字と被る明るい校色を両校に着せる（ハーネスの試合は配色が null なので入る）
            var palette = Shell.TeamColorProvider.Palette;
            var light = palette.FindById("white_yamabuki");
            if (light != null) ctrl.FillTeamColorsForCapture(TeamColors.FromEntry(light), TeamColors.FromEntry(light));

            for (int i = 0; i < 3; i++) ctrl.AdvanceForCapture();
            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            ctrl.RestartCurrentPaForCapture();
            for (int i = 0; i < 24; i++) yield return null;
            ctrl.FreezeLiveForCapture(true);
            for (int i = 0; i < 6; i++) yield return null;

            yield return Capture(FileName + "-after");

            var root = ctrl.GetComponent<UIDocument>().rootVisualElement;
            var n = 0;
            root.Query<VisualElement>(className: "mhud-cp--pos").ForEach(chip =>
            {
                n++;
                var fg = chip.Q<Label>()?.resolvedStyle.color ?? Color.clear;
                Debug.Log("[HudPosChipShot] chip=" + chip.Q<Label>()?.text
                          + " bg=#" + ColorUtility.ToHtmlStringRGB(chip.resolvedStyle.backgroundColor)
                          + " fg=#" + ColorUtility.ToHtmlStringRGB(fg));
            });
            Debug.Log("[HudPosChipShot] chips=" + n);
            ctrl.FreezeLiveForCapture(false);
            Debug.Log("[HudPosChipShot] DONE");
        }

        private IEnumerator Capture(string file)
        {
            EnsureOnlyMatchLive();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ctrl = FindController();
            // 素体が真っ黒に焼ける回がある（RT差し替えと描画の噛み合わせ）。先に1回描いておく
            ctrl.DuelRigForCapture?.RenderNow();
            yield return null;
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();

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
                Debug.Log("[HudPosChipShot] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
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
