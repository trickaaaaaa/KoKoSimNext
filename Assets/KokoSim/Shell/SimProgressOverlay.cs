using KokoSim.Unity.Components;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ブースト画面（スタメン設定・結果）の下端に進捗ストリップ（<see cref="UiComponents.SimProgress"/>）を出し、
    /// 背景の全国裏試合（<see cref="NationBackgroundSim"/>）の消化進捗を毎フレーム反映する（#208）。
    ///
    /// 更新は UITK の scheduler（メインスレッドのパネル更新で回る）で行う。ブースト時の背景シムは確保を
    /// 刻んで（Thread.Sleep で1〜2フレームぶんメインへ譲る）走るため、GC の stop-the-world でメインが
    /// 飢餓せず、このバーは滑らかに伸びる（#208 追記「メインスレッド飢餓の禁止」）。完了したら即消す。
    /// </summary>
    public static class SimProgressOverlay
    {
        private const string StripName = "sim-load";
        private const long TickMs = 66;   // 約15Hz。バーの伸びを滑らかに見せつつ更新は軽量。

        /// <summary>ブースト画面の最下段に進捗ストリップを付け、更新スケジューラを起動する（未付与なら生成）。</summary>
        public static void Attach(VisualElement root)
        {
            if (root == null) return;
            var strip = root.Q<VisualElement>(StripName);
            if (strip == null)
            {
                // 画面のフレックス列（.ui-root＝各画面の縦積みコンテナ）の最下段に流し込みで置く。
                // 絶対配置にせず末尾に足す＝下端の CTA など本文を覆わず、その下に段として並ぶ。
                var container = root.Q<VisualElement>(className: "ui-root") ?? root;
                strip = UiComponents.SimProgress();
                container.Add(strip);   // 画面を再構築（SetActive）した直後は毎回ここで作り直される。
                strip.schedule.Execute(() => Tick(strip)).Every(TickMs);
            }
            Tick(strip);   // 表示直後の初期反映（次tickを待たずに出す/消す）。
        }

        private static void Tick(VisualElement strip)
        {
            var running = NationBackgroundSim.Running;
            strip.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            if (running) UiComponents.SetSimProgress(strip, NationBackgroundSim.Progress);
        }
    }
}
