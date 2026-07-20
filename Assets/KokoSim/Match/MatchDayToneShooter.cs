#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】盤面「夏の昼間」トーン調整の before/after 比較スクショ。
    /// MatchDetail ハーネス（決定論サンプル7プレー・同じ Match2DPlaybackElement）を使い、
    /// フェア芝/ファウル芝/ダート/ライン/ボール＋影/軌跡/守備トークン/走者トークンが1枚に入る
    /// 代表フレームを撮る。出力先は screenshots/match2d-daytone/{before|after}/ を自動判定
    /// （before が空なら before、埋まっていれば after）。トーン確定後に削除してよい。
    /// </summary>
    public sealed class MatchDayToneShooter : MonoBehaviour
    {
        private static readonly (int Play, double T, string File)[] Shots =
        {
            (0, 2.3, "01-lefthit-air"),    // レフト前：打球滞空＋レフト前進（動野手）＋走者＋軌跡
            (0, 4.6, "02-lefthit-throw"),  // 返球：多くの野手は定位置（静止トークン）＋走者
            (5, 2.6, "03-dp-infield"),     // 6-4-3：内野ダート上のゴロ＋複数野手＋走者2
            (6, 4.3, "04-gap-deep"),       // 右中間：外野芝＋フェンス弧＋深い打球
            (1, 4.4, "05-fly-highball"),   // 中飛：高い打球＝ボールと影が大きく離れる
        };

        private const int W = 900;
        private const int H = 840;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            EnsureOnlyMatchDetail();
            yield return null;
            yield return null;
            EnsureOnlyMatchDetail();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[DayTone] MatchDetail/Controller が見つからない"); yield break; }

            var baseDir = "screenshots/match2d-daytone";
            var beforeDir = Path.Combine(baseDir, "before");
            var afterDir = Path.Combine(baseDir, "after");
            var haveBefore = Directory.Exists(beforeDir) && Directory.GetFiles(beforeDir, "*.png").Length > 0;
            var dir = haveBefore ? afterDir : beforeDir;
            Directory.CreateDirectory(dir);
            Debug.Log($"[DayTone] phase={(haveBefore ? "AFTER" : "BEFORE")} dir={dir}");

            var ps = ctrl.GetComponent<UIDocument>().panelSettings;

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
                var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                rt.Create();
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
                    File.WriteAllBytes(Path.Combine(dir, shot.File + ".png"), tex.EncodeToPNG());
                    Debug.Log("[DayTone] wrote " + shot.File);
                }
                finally
                {
                    ps.targetTexture = prevRt;
                    ps.scaleMode = prevScale;
                    ps.referenceResolution = prevRef;
                    rt.Release(); Destroy(rt);
                    if (tex != null) Destroy(tex);
                }

                yield return null;
            }

            Debug.Log("[DayTone] DONE " + Shots.Length + " shots");
        }

        private static void EnsureOnlyMatchDetail()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchDetail");
            }
        }

        private static MatchDetailController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchDetail") return d.GetComponent<MatchDetailController>();
            }
            return null;
        }
    }
}
#endif
