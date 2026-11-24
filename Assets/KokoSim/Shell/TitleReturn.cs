using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ゲーム中からタイトルへ戻る手順（システムタブの「タイトルへ戻る」・体験版の終了画面の「タイトルへ」）。
    /// 裏の全シムを完了させてから静的リセットする。GameClock.Advance の年度替わり・新チーム発足と
    /// 同じ作法（issue #529・#1226）＝背景スレッドと同じ NationRosters を同時に触らない。
    /// この待ちは main をブロックし得るので ManualWait で「タイトルへ戻る準備中」を1フレーム先に描画してから入る。
    /// </summary>
    public static class TitleReturn
    {
        public static void Run(VisualElement root)
        {
            ManualWait.Run<object>(root, "タイトルへ戻る準備中", () =>
            {
                BackgroundSims.EnsureAllCompleted();
                return null;
            }, _ =>
            {
                SaveGameIO.ResetForNewGame();
                KokoSim.Unity.Home.HomeState.Reset();
                ScreenRouter.Instance?.ShowDeferred("TitleScreen");
            });
        }
    }
}
