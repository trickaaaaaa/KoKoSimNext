#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Practice
{
    /// <summary>
    /// 【開発用・エディタ限定】年契約「遠征」で並ぶ県外の相手（設計書04 §1.4a）の見え方を確かめる受け入れスクショ。
    /// 県外の行にだけ県名を添えるため学校の欄を器（.pm-row__namecell）に変えたので、
    /// <b>県内の行が従来どおりに見えるか</b>も同時に確かめる。使い捨て。
    /// </summary>
    public sealed class AwayOpponentShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/practice";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 8; i++) yield return null;   // ScreenRouter.Start の既定画面を待つ

            GameClock.Reset();
            ManagerService.Reset();
            NationService.Reset();

            // ① 遠征なし（県内だけ）＝学校の欄の器を入れた影響が無いことの基準。
            var doc = Open("PracticeMatch");
            if (doc == null) { Debug.LogError("[AW] PracticeMatch が見つからない"); yield break; }
            yield return null; yield return null;
            Debug.Log("[AW] 遠征なし 相手=" + new PracticeMatchState().BuildView().Opponents.Count + "件");
            yield return Capture(doc, "00-home-only");

            // ② 遠征あり。県外は一覧の末尾に付くので、そこまでスクロールして写す。
            ManagerService.Contracts.SetLevel("expedition", 1);
            doc = Open("PracticeMatch");
            yield return null; yield return null;
            ScrollToBottom(doc);
            yield return null; yield return null;
            Debug.Log("[AW] 遠征あり 相手=" + new PracticeMatchState().BuildView().Opponents.Count + "件");
            yield return Capture(doc, "01-with-expedition");

            Debug.Log("[AW] DONE");
        }

        private static void ScrollToBottom(UIDocument doc)
        {
            ScrollView sv = null;
            foreach (var e in doc.rootVisualElement.Query<ScrollView>().ToList())
                if (sv == null) sv = e;
            if (sv == null) { Debug.LogError("[AW] ScrollView が無い"); return; }
            var max = Mathf.Max(0f, sv.contentContainer.layout.height - sv.contentViewport.layout.height);
            sv.scrollOffset = new Vector2(0f, max);
        }

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
                Debug.Log("[AW] wrote " + file);
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
