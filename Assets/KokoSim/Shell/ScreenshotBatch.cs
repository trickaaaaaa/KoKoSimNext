using System.Collections;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// バッチスクリーンショット（UI-BUILD-METHOD Step 3 の運用ループ）。
    /// <see cref="ScreenshotCapture"/> は1枚ずつの非同期コルーチンなので、複数画面を撮るのに
    /// MCP の execute_code を連打すると（ドメインリロードや撮影の重なりで）取りこぼす。
    /// ここで「画面を Show → 数フレーム待つ → 1枚撮る → 撮り終わるまで待つ」を直列に回す。
    ///
    /// 使い方（MCP execute_code から）:
    ///   ScreenshotBatch.Run(new[]{"HomeDashboard","PlayerList"}, "screenshots/f1-", 1600, 900);
    /// 出力は "screenshots/f1-HomeDashboard.png" のように prefix ＋ 画面名 ＋ .png。
    /// </summary>
    public sealed class ScreenshotBatch : MonoBehaviour
    {
        // ScreenshotCapture が PanelSettings を書き換えて finally で戻すまでの余裕。
        // 撮影が重なると共有 PanelSettings の復元順が壊れるので、必ず終わらせてから次へ進む。
        private const int SettleFrames = 14;

        public static void Run(string[] screens, string pathPrefix, int width, int height)
        {
            var runner = new GameObject("~ScreenshotBatch").AddComponent<ScreenshotBatch>();
            runner.StartCoroutine(runner.Loop(screens, pathPrefix, width, height));
        }

        private IEnumerator Loop(string[] screens, string pathPrefix, int width, int height)
        {
            // エディタ非フォーカスでも WaitForEndOfFrame を進めるため（撮影が空フレームになる対策）。
            var prevBackground = Application.runInBackground;
            Application.runInBackground = true;

            foreach (var screen in screens)
            {
                var router = ScreenRouter.Instance;
                if (router == null)
                {
                    Debug.LogError("[ScreenshotBatch] ScreenRouter.Instance is null (Play 中に呼ぶこと)");
                    break;
                }

                router.Show(screen);
                // OnEnable → 初回レイアウトが確定するまで数フレーム回す。
                yield return null;
                yield return new WaitForEndOfFrame();
                yield return null;

                ScreenshotCapture.Capture(screen, width, height, pathPrefix + screen + ".png");
                for (var f = 0; f < SettleFrames; f++) yield return new WaitForEndOfFrame();
            }

            Debug.Log("[ScreenshotBatch] done: " + screens.Length + " screens → " + pathPrefix + "*.png");
            Application.runInBackground = prevBackground;
            Destroy(gameObject);
        }
    }
}
