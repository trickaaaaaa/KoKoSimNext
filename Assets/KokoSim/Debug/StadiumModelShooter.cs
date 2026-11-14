#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// 【開発用・エディタ限定】球場モデル（issue #904）の見た目の確認。デバッグ試合で指定の球場を開き、
    /// 一定間隔で試合画面を撮る（投球の構図・守備の構図の両方にスタンドが映るまで）。使い捨て。
    /// 使い方: Play 中に GameObject へ付け、<see cref="StadiumId"/> を入れる。
    /// </summary>
    public sealed class StadiumModelShooter : MonoBehaviour
    {
        public string StadiumId = "jingu";
        public int Shots = 12;
        public float IntervalSeconds = 2.5f;

        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            var dir = "screenshots/stadium-" + StadiumId;
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            ScreenRouter.Instance?.Show(DebugMatchSetupController.ScreenName);
            for (var i = 0; i < 4; i++) yield return null;
            var setup = Find(DebugMatchSetupController.ScreenName).GetComponent<DebugMatchSetupController>();
            setup.SelectForCapture(StadiumId);
            var err = setup.StartForCapture();
            if (err != null) { Debug.LogError("[SMS] 開始失敗: " + err); yield break; }
            for (var i = 0; i < 120; i++) yield return null;

            var live = Find("MatchLive");
            for (var n = 0; n < Shots; n++)
            {
                yield return Capture(live, Path.Combine(dir, $"{n:00}.png"));
                yield return new WaitForSecondsRealtime(IntervalSeconds);
            }
            Debug.Log("[SMS] DONE " + StadiumId);
        }

        private static UIDocument Find(string name)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                if (d.gameObject.scene.IsValid() && d.gameObject.name == name) return d;
            return null;
        }

        private static IEnumerator Capture(UIDocument doc, string path)
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
            File.WriteAllBytes(path, tex.EncodeToPNG());
            ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
            rt.Release();
            Destroy(tex);
        }
    }
}
#endif
