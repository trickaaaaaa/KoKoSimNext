using KokoSim.Unity.Components;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// プレイヤーが待つしかない場面（セーブ/ロード・裏試合の結果待ち・試合開始/結果集計の重い後処理）で、
    /// 全画面ブロッキング表示（<see cref="UiComponents.WaitOverlay"/>）を出す。すべての画面ルートに
    /// アタッチする（旧 SimProgressOverlay はブースト画面限定の下端ストリップだったため、Home や
    /// SaveSlots のように Boost throttle が付かない画面では「今週を進める」「セーブ」を押しても
    /// 何も表示されず固まって見えていた）。
    ///
    /// 表示条件は4系統: <see cref="ManualWait"/>（同期I/Oを1フレーム描画してからブロックする手動フラグ）・
    /// <see cref="BracketFinishSim"/>・<see cref="BackgroundGameOp"/>（試合開始/試合後処理の重い載せ替え）・
    /// 全国裏試合のブースト消化中（<see cref="NationBackgroundSim"/>）。ライブ観戦中（Paused throttle）の
    /// 通常消化はここでは出さない＝プレイヤーは実際には待たされていない（試合を見ていられる）ため。
    /// 更新は UITK の scheduler（メインスレッドのパネル更新で回る）で行う。
    /// </summary>
    public static class WaitOverlayController
    {
        private const string OverlayName = "wait-overlay";
        private const long TickMs = 66;   // 約15Hz。バーの動きを滑らかに見せつつ更新は軽量。

        private static float _marqueePhase;

        /// <summary>画面ルートの末尾にオーバーレイを付け、更新スケジューラを起動する（未付与なら生成）。</summary>
        public static void Attach(VisualElement root)
        {
            if (root == null) return;
            var overlay = root.Q<VisualElement>(OverlayName);
            if (overlay == null)
            {
                overlay = UiComponents.WaitOverlay();
                root.Add(overlay);   // 画面を再構築（SetActive）した直後は毎回ここで作り直される。
                overlay.schedule.Execute(() => Tick(overlay)).Every(TickMs);
            }
            Tick(overlay);   // 表示直後の初期反映（次tickを待たずに出す/消す）。
        }

        private static void Tick(VisualElement overlay)
        {
            if (ManualWait.Active)
            {
                overlay.style.display = DisplayStyle.Flex;
                UiComponents.SetWaitOverlay(overlay, ManualWait.Label, null);
                Marquee(overlay);
                return;
            }
            if (BracketFinishSim.Running)
            {
                overlay.style.display = DisplayStyle.Flex;
                UiComponents.SetWaitOverlay(overlay, "試合結果を集計中", BracketFinishSim.Progress);
                return;
            }
            // 全国裏試合のブースト消化（対戦カード・試合結果画面での集中消化）だけを対象にする。
            // ライブ観戦中は Paused、通常週は Normal（プレイヤーの操作を妨げない低頻度消化）＝待たせていない。
            if (NationBackgroundSim.Running && NationBackgroundSim.Throttle == NationBackgroundSim.SimThrottle.Boost)
            {
                overlay.style.display = DisplayStyle.Flex;
                UiComponents.SetWaitOverlay(overlay, "他会場の結果を集計中", NationBackgroundSim.Progress);
                return;
            }
            if (BackgroundGameOp.Running)
            {
                overlay.style.display = DisplayStyle.Flex;
                UiComponents.SetWaitOverlay(overlay, "処理中", null);   // 試合開始/試合後処理には分子・分母が無い
                Marquee(overlay);
                return;
            }
            overlay.style.display = DisplayStyle.None;
        }

        private static void Marquee(VisualElement overlay)
        {
            _marqueePhase += TickMs / 1000f * 0.5f;   // 1往復 約4秒
            UiComponents.TickWaitOverlayMarquee(overlay, _marqueePhase);
        }
    }
}
