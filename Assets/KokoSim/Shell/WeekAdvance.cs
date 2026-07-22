namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ホーム以外の画面（選手・メンバー・練習・練習試合・大会）の「今週を進める」の共通実装（issue #134）。
    /// どのタブでも同一挙動にするための単一ソース。以前は各画面が個別に配線しており、選手/メンバータブは
    /// 一切配線されず（＝死にボタン）、他タブは大会モードでも週を丸ごと進めて大会日程を飛び越していた。
    ///
    /// ・通常週: 共有クロックを1週進める（大会開幕週の大会モード遷移は <see cref="GameClock.EnterWeek"/> に
    ///   集約済みなので、ここに大会判定は要らない。開幕時は BannerPending が立ち <see cref="ScreenRouter"/> が
    ///   ホームへ回送して開幕演出→日送りへ引き継ぐ）。
    /// ・大会モード中: 日送り・試合ダイアログはホームが担うため、側画面からはホームへ回送する。
    /// </summary>
    public static class WeekAdvance
    {
        /// <summary>側画面の「今週を進める」共通処理。<paramref name="render"/> は進週後の再描画（省略可）。</summary>
        public static void FromSideScreen(System.Action render)
        {
            if (GameSession.Current.InTournament)
            {
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
                return;
            }
            GameClock.Advance(+1);
            render?.Invoke();
        }
    }
}
