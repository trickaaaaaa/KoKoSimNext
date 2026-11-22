using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace KokoSim.Bootstrap
{
    /// <summary>
    /// 起動時の vSync / fps 上限の唯一の設定場所。
    /// vSyncCount != 0 だと targetFrameRate は Unity 側の仕様で無視される上、Editor の Game View は
    /// vSync の実効挙動が OS/GPU ドライバ依存（特に macOS/Metal）なので、vSync は切って targetFrameRate で直接キャップする。
    /// 外部ディスプレイ接続時の GPU 青天井 fps によるコイル鳴き対策も兼ねる（設定は揮発するため毎回再適用）。
    /// 設定画面（段階2）は <see cref="FpsLimit"/> を書き換えるだけでよい（保存と即時反映は内側でやる）。
    /// </summary>
    public static class FrameRateGuard
    {
        public const int DefaultFps = 60;
        public const int MinFps = 30;
        public const int MaxFps = 240;

        private const string PrefKey = "KokoSim.Settings.FpsLimit";

        public static int FpsLimit
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(PrefKey, DefaultFps), MinFps, MaxFps);
            set
            {
                PlayerPrefs.SetInt(PrefKey, Mathf.Clamp(value, MinFps, MaxFps));
                Apply();
            }
        }

        private static void Apply()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = FpsLimit;
        }

        // 実機・Play時
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnRuntimeLoad() => Apply();

#if UNITY_EDITOR
        // Editor起動・ドメインリロード時（Editアイドル時のfpsも抑える）
        [InitializeOnLoadMethod]
        private static void OnEditorLoad() => Apply();
#endif
    }
}
