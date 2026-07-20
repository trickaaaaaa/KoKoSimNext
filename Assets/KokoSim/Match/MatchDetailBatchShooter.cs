#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】試合2D俯瞰ビューのバッチスクショ（設計者Claude指示 Step 4 受け入れ確認）。
    /// 7プレー×2時点（打球中・結果表示後）を1回の Play セッションで撮り切る。
    /// execute_code を毎回叩くとドメインリロードで Play が抜けるため、コルーチンで連続撮影する。
    /// 使い捨て。α後に削除してよい。
    /// </summary>
    public sealed class MatchDetailBatchShooter : MonoBehaviour
    {
        private static readonly (int Play, double T, string File)[] Shots =
        {
            (0, 2.3, "p0-a"), (0, 4.5, "p0-b"),
            (1, 3.0, "p1-a"), (1, 5.0, "p1-b"),
            (2, 1.7, "p2-a"), (2, 4.2, "p2-b"),
            (3, 2.6, "p3-a"), (3, 5.2, "p3-b"),
            (4, 2.6, "p4-a"), (4, 4.6, "p4-b"),
            (5, 6.6, "p5-a"), (5, 9.6, "p5-b"),
            (6, 2.0, "p6-a"), (6, 4.3, "p6-b"),
        };

        private const int W = 900;
        private const int H = 840;
        private const string Dir = "screenshots/match2d";

        private IEnumerator Start()
        {
            Application.runInBackground = true;

            // 単一 PanelSettings 競合回避: MatchDetail だけ有効化。
            // ScreenRouter.Start が同フレームで HomeDashboard を Show するため、待機後に再保証する。
            EnsureOnlyMatchDetail();
            yield return null;
            yield return null;
            EnsureOnlyMatchDetail();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[BatchShooter] MatchDetail/Controller が見つからない"); yield break; }

            var doc = ctrl.GetComponent<UIDocument>();
            var ps = doc.panelSettings;
            Directory.CreateDirectory(Dir);

            foreach (var shot in Shots)
            {
                EnsureOnlyMatchDetail();
                ctrl.CaptureSeek(shot.Play, shot.T);
                yield return null;
                yield return new WaitForEndOfFrame();
                yield return null;

                var prevRt = ps.targetTexture;
                var prevScale = ps.scaleMode;
                var prevRef = ps.referenceResolution;
                RenderTexture rt = null;
                Texture2D tex = null;
                try
                {
                    rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                    rt.Create();
                    ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                    ps.referenceResolution = new Vector2Int(W, H);
                    ps.targetTexture = rt;
                }
                finally { }

                yield return null;
                yield return new WaitForEndOfFrame();
                yield return null;
                yield return new WaitForEndOfFrame();

                try
                {
                    tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prevActive;
                    File.WriteAllBytes(Path.Combine(Dir, shot.File + ".png"), tex.EncodeToPNG());
                    Debug.Log("[BatchShooter] wrote " + shot.File);
                }
                finally
                {
                    ps.targetTexture = prevRt;
                    ps.scaleMode = prevScale;
                    ps.referenceResolution = prevRef;
                    if (rt != null) { rt.Release(); Destroy(rt); }
                    if (tex != null) Destroy(tex);
                }

                yield return null;
            }

            Debug.Log("[BatchShooter] DONE all " + Shots.Length + " shots");
        }

        // MatchDetail だけを有効化（他の全画面 UIDocument は無効）。
        private static void EnsureOnlyMatchDetail()
        {
            var docs = Resources.FindObjectsOfTypeAll<UIDocument>();
            foreach (var d in docs)
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchDetail");
            }
        }

        private static MatchDetailController FindController()
        {
            var docs = Resources.FindObjectsOfTypeAll<UIDocument>();
            foreach (var d in docs)
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchDetail") return d.GetComponent<MatchDetailController>();
            }
            return null;
        }
    }
}
#endif
