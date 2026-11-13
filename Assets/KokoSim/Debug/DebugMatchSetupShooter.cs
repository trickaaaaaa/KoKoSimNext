#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// 【開発用・エディタ限定】デバッグ試合（issue #905）の受け入れスクショ。
    /// 選択画面（既定・県大会決勝を選んだ状態）を撮り、県営（広い）／市営（狭い）／甲子園で試合を開いて
    /// 試合画面を撮る（電光掲示板の球場名と、3D のフェンスが球場ごとに変わること）。使い捨て。
    /// </summary>
    public sealed class DebugMatchSetupShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/debug-match";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var doc = Show(DebugMatchSetupController.ScreenName);
            if (doc == null) { Debug.LogError("[DMS] 画面が無い"); yield break; }
            for (var i = 0; i < 4; i++) yield return null;
            var ctrl = doc.GetComponent<DebugMatchSetupController>();

            ctrl.SelectForCapture("koshien");
            yield return Capture(doc, "00-setup-koshien");
            ctrl.SelectForCapture("higashi_tokyo_final");
            yield return Capture(doc, "01-setup-tokyo-final");
            File.WriteAllText(Path.Combine(Dir, "fonts.txt"), FontAudit.Report(doc.rootVisualElement));

            foreach (var id in new[] { "pref_large_01", "municipal_small_01", "koshien" })
            {
                doc = Show(DebugMatchSetupController.ScreenName);
                for (var i = 0; i < 4; i++) yield return null;
                ctrl = doc.GetComponent<DebugMatchSetupController>();
                ctrl.SelectForCapture(id);
                var err = ctrl.StartForCapture();
                if (err != null) { Debug.LogError("[DMS] 開始失敗: " + err); yield break; }
                // 遅延遷移＋3D の組み立て待ち
                for (var i = 0; i < 240; i++) yield return null;
                var live = Find("MatchLive");
                if (live == null || !live.gameObject.activeInHierarchy) { Debug.LogError("[DMS] MatchLive が開いていない"); yield break; }
                yield return Capture(live, "10-live-" + id);
            }
            Debug.Log("[DMS] DONE");
        }

        private static UIDocument Find(string name)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                if (d.gameObject.scene.IsValid() && d.gameObject.name == name) return d;
            return null;
        }

        private static UIDocument Show(string screen)
        {
            ScreenRouter.Instance?.Show(screen);
            return Find(screen);
        }

        private IEnumerator Capture(UIDocument doc, string file)
        {
            var ps = doc.panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;
            for (var i = 0; i < 3; i++) { yield return null; yield return new WaitForEndOfFrame(); }
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
            ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
            rt.Release();
            Destroy(tex);
            Debug.Log("[DMS] wrote " + file);
        }
    }
}
#endif
