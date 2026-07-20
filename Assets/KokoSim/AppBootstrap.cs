using UnityEngine;

namespace KokoSim.Unity
{
    /// <summary>
    /// 起動時のアプリ全体設定。ターン制UIゲームは高FPS不要のため、フレームレートを上限化して
    /// GPUの無駄回し（コイル鳴き＝モニタ/PCからの高音）を防ぐ。
    /// vSyncCount!=0だとtargetFrameRateがUnity側の仕様で無視される上、Unity EditorのGame Viewは
    /// VSyncの実効挙動がOS/GPUドライバ依存で不安定（特にmacOS/Metal）なため、
    /// VSyncには頼らずtargetFrameRateで直接キャップする（Editor再生時も確実に効く）。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class AppBootstrap : MonoBehaviour
    {
        [SerializeField] private int targetFps = 60;

        private void Awake()
        {
            QualitySettings.vSyncCount = 0;           // targetFrameRateを無視させない
            Application.targetFrameRate = targetFps;  // 明示的な上限
        }
    }
}
