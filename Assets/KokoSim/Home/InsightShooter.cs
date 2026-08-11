#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】気づき（設計書03 §5.6）の通知フィード表示を確かめる受け入れスクショ。
    /// 気づきは週あたり数%しか出ないので、年1の各週を順に試して<b>最初に気づきが出た週</b>を捕まえ、
    /// その状態のホームを撮る。備品「ビデオ・分析機材」を買った状態で撮る（頻度・精度が上がる）。使い捨て。
    /// </summary>
    public sealed class InsightShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/insight";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 8; i++) yield return null;   // ScreenRouter.Start の既定画面を待つ

            GameClock.Reset();
            ManagerService.Reset();
            HomeState.Reset();
            ManagerService.Facilities.SetLevel("video", 1);

            var hs = HomeState.Current;
            var run = typeof(HomeState).GetMethod(
                "RunInsightWeek", BindingFlags.NonPublic | BindingFlags.Instance);
            var feedField = typeof(HomeState).GetField(
                "_feed", BindingFlags.NonPublic | BindingFlags.Instance);
            var weekProp = typeof(GameClock).GetProperty("Week");

            var hit = 0;
            for (var w = 1; w <= 52 && hit == 0; w++)
            {
                weekProp.SetValue(null, w, null);   // static プロパティ＝インスタンスは null
                run.Invoke(hs, null);
                var feed = (System.Collections.Generic.List<FeedItem>)feedField.GetValue(hs);
                // 直近の投入はフィードの先頭に入る（PushFeed が Insert(0)）。
                if (feed.Count > 0 && feed[0].Tag == "発見") hit = w;
            }

            if (hit == 0) { Debug.LogError("[IN] 52週で気づきが1件も出なかった"); yield break; }
            var head = (System.Collections.Generic.List<FeedItem>)feedField.GetValue(hs);
            Debug.Log("[IN] 週" + hit + " に気づき: " + head[0].Text + " / tag=" + head[0].Tag);

            var doc = Open("HomeDashboard");
            if (doc == null) { Debug.LogError("[IN] HomeDashboard が見つからない"); yield break; }
            yield return null; yield return null;
            yield return Capture(doc, "00-feed");
            Debug.Log("[IN] DONE");
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
                Debug.Log("[IN] wrote " + file);
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
