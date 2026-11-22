using UnityEngine;

namespace KokoSim.Unity
{
    /// <summary>起動時の読み込み。フレームレート設定は <see cref="Bootstrap.FrameRateGuard"/> が唯一の持ち主。</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class AppBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            Shell.DisplayNamesProvider.Load();        // 大会名の表（data/display-names.yaml・issue #1319）を最初に読む
            Shell.TrajectoryTableWarmup.Start();      // 弾道の判定テーブルを背景で先に作る（最初の試合で待たせない）
        }
    }
}
