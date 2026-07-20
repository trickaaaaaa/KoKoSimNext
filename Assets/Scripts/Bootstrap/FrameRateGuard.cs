using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace KokoSim.Bootstrap
{
    /// <summary>
    /// フレームレート上限＋VSyncを起動時に強制する。
    /// 外部ディスプレイ接続時のGPU青天井fpsによるコイル鳴き対策（設定は揮発するため毎回再適用）。
    /// </summary>
    public static class FrameRateGuard
    {
        private const int TargetFps = 60;

        private static void Apply()
        {
            QualitySettings.vSyncCount = 1;          // ディスプレイのリフレッシュに同期
            Application.targetFrameRate = TargetFps; // 保険の上限
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
