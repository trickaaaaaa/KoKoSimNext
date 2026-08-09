#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// 【開発用・エディタ限定】大会モードのホーム画面と、そこで開く ≡メニューを撮る（issue #565 の
    /// 「保存の入口をどこに置くか」を実物で判断するため）。夏の大会開幕まで週を進め、開幕バナーを閉じてから撮る。
    /// 使い捨て。
    /// </summary>
    public sealed class TournamentMenuShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/tournament-menu";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            SaveGameIO.ResetForNewGame();
            GameSeed.Reset(20260809UL);

            var doc = Open("HomeDashboard");
            if (doc == null) { Debug.LogError("[TmMenu] HomeDashboard が無い"); yield break; }
            yield return null; yield return null;

            // 大会開幕週まで1週ずつ進める（開幕フックは EnterWeek で回る）。
            for (var i = 0; i < 40 && GameSession.Current.Mode != GameMode.Tournament; i++)
            {
                GameClock.Advance(1);
                yield return null;
            }
            Debug.Log("[TmMenu] mode=" + GameSession.Current.Mode + " week=" + GameClock.Week
                      + " banner=" + GameSession.Current.BannerPending);

            // 画面を作り直して大会モードの姿にする（開幕バナーが出る）。
            doc = Open("HomeDashboard");
            yield return null; yield return null;
            yield return Capture(doc, "1-banner");

            // バナーを閉じて大会モードのホームへ。
            var banner = doc.rootVisualElement.Q<VisualElement>("tm-banner");
            if (banner != null && banner.style.display != DisplayStyle.None)
            {
                Submit(doc.rootVisualElement.Q<Button>("tm-banner-ok"));
                yield return null; yield return null;
            }
            yield return Capture(doc, "2-tournament-home");

            // ≡メニューを開く（大会中はセーブが非活性＋理由1行になるはず）。
            Submit(doc.rootVisualElement.Q<Button>("hm-menu-btn"));
            yield return null; yield return null;
            var reason = doc.rootVisualElement.Q<Label>("hm-menu-save-reason");
            Debug.Log("[TmMenu] saveReason=" + (reason == null ? "(無し)" : reason.text)
                      + " canSave=" + SaveGameIO.CanSave);
            yield return Capture(doc, "3-menu-open");

            Debug.Log("[TmMenu] DONE");
        }

        private static void Submit(VisualElement target)
        {
            if (target == null) return;
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
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
