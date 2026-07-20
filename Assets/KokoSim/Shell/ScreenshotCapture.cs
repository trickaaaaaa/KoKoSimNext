using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 決定論スクリーンショット（UI-BUILD-METHOD Step 3）。
    /// GameView のキャプチャは空フレーム・描画タイミングで不安定なため使わず、
    /// UITK パネルを RenderTexture へ描いて ReadPixels する。Play 中に呼ぶこと。
    /// 使い方（MCP execute_code から）:
    ///   ScreenRouter.Show("TrainingPlan"); // 対象画面を activate
    ///   ScreenshotCapture.Capture("TrainingPlan", 1600, 900, "screenshots/training.png");
    /// </summary>
    public sealed class ScreenshotCapture : MonoBehaviour
    {
        public static void Capture(string goName, int width, int height, string path)
        {
            var runner = new GameObject("~ScreenshotRunner").AddComponent<ScreenshotCapture>();
            runner.StartCoroutine(runner.Run(goName, width, height, path));
        }

        private IEnumerator Run(string goName, int width, int height, string path)
        {
            var go = GameObject.Find(goName);
            var doc = go != null ? go.GetComponent<UIDocument>() : null;
            if (doc == null)
            {
                Debug.LogError("[Screenshot] UIDocument '" + goName + "' not found (activate the screen first).");
                Destroy(gameObject);
                yield break;
            }

            // PanelSettings をキャプチャ用に一時変更する。ここで書き換えた scaleMode /
            // referenceResolution / targetTexture は「共有アセット」への破壊的変更なので、
            // 例外・Play停止によるコルーチン中断でも必ず戻すよう try/finally で復元する
            // （復元漏れは PanelSettings を ScaleWithScreenSize のまま残し、実描画スケールを
            //   非整数化させて全画面の文字を滲ませる。実際にこの取りこぼしが起きた）。
            var ps = doc.panelSettings;
            var prevRt = ps.targetTexture;
            var prevScale = ps.scaleMode;
            var prevRef = ps.referenceResolution;

            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "ScreenshotRT" };
                rt.Create();

                ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                ps.referenceResolution = new Vector2Int(width, height);
                ps.targetTexture = rt;

                // レイアウト＆描画を確定させるため数フレーム回す（空フレーム対策）。
                // ※ WaitForEndOfFrame は runInBackground=false だとエディタ非フォーカス時に
                //   進まないため、呼び出し側で Application.runInBackground=true を保証すること。
                yield return null;
                yield return new WaitForEndOfFrame();
                yield return null;
                yield return new WaitForEndOfFrame();

                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log("[Screenshot] wrote " + path + " (" + width + "x" + height + ")");
            }
            finally
            {
                // 後始末：PanelSettings を必ず元に戻す（中断・例外時も実行される）。
                ps.targetTexture = prevRt;
                ps.scaleMode = prevScale;
                ps.referenceResolution = prevRef;
                if (rt != null) { rt.Release(); Destroy(rt); }
                if (tex != null) Destroy(tex);
                Destroy(gameObject);
            }
        }
    }
}
