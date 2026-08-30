using System;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// メインスレッドを同期的にブロックする短い処理（セーブ/ロードのファイルI/O等）の間だけ
    /// <see cref="WaitOverlayController"/> に「待つしかない」旨を出させる手動フラグ。
    ///
    /// セーブ中の状態キャプチャは他スレッドとの並行変更を避けたいため、この処理自体は非同期化しない
    /// （<see cref="BackgroundGameOp"/> のように Task.Run へ載せ替えない）。その代わり、ブロックへ
    /// 入る前に必ず1フレーム描画を挟んで <see cref="Active"/> の表示を確定させてから <paramref name="work"/>
    /// を実行する＝「押しても無反応に見える」を防ぐのが目的で、処理速度そのものは変えない。
    /// </summary>
    public static class ManualWait
    {
        public static bool Active { get; private set; }
        public static string Label { get; private set; } = "";

        /// <summary>
        /// [label]を表示させてから1フレーム後に[work]を同期実行し、結果を[onDone]へメインスレッドで渡す。
        /// [anyPanelElement]はスケジューラ確保のためだけに使う（同一パネルの何要素でもよい）。
        /// </summary>
        public static void Run<T>(VisualElement anyPanelElement, string label, Func<T> work, Action<T> onDone)
        {
            Active = true;
            Label = label;
            anyPanelElement.schedule.Execute(() =>
            {
                T result;
                try { result = work(); }
                finally { Active = false; }
                onDone?.Invoke(result);
            }).ExecuteLater(16);   // 約1フレームぶん先送り＝ブロック開始前にオーバーレイの描画を確定させる
        }
    }
}
