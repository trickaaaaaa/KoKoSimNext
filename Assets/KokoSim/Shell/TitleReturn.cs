using System.Collections;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ゲーム中からタイトルへ戻る手順（システムタブの「タイトルへ戻る」・体験版の終了画面の「タイトルへ」）。
    /// 裏の全シムを完了させてから静的リセットする。GameClock.Advance の年度替わり・新チーム発足と
    /// 同じ作法（issue #529・#1226）＝背景スレッドと同じ NationRosters を同時に触らない。
    /// 裏シムの完了はコマをまたいで待つ（<see cref="LoadingJob"/>・待つ間もポップアップが動く）。
    /// </summary>
    public static class TitleReturn
    {
        /// <param name="root">呼び出し元の画面（互換のため受け取る。進めるのは ScreenRouter）。</param>
        public static void Run(VisualElement root) => LoadingJob.Run("準備中", "タイトルへ戻ります", Routine());

        private static IEnumerator Routine()
        {
            yield return LoadingJob.WaitBackgroundIdle();
            BackgroundSims.EnsureAllCompleted();
            SaveGameIO.ResetForNewGame();
            KokoSim.Unity.Home.HomeState.Reset();
            ScreenRouter.Instance?.ShowDeferred("TitleScreen");
        }
    }
}
