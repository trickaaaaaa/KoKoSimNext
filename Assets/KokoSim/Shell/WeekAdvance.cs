namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ホーム以外の画面（選手・メンバー・練習・練習試合・大会）の「今週を進める」の共通実装（issue #134）。
    /// どのタブでも同一挙動にするための単一ソース。以前は各画面が個別に配線しており、選手/メンバータブは
    /// 一切配線されず（＝死にボタン）、他タブは大会モードでも週を丸ごと進めて大会日程を飛び越していた。
    ///
    /// ・通常週: ホームと同じ <see cref="KokoSim.Unity.Home.HomeState.AdvanceWeek"/> で週を進める（練習・怪我・成長・調子・
    ///   週次イベント・フィードもここで回る。大会開幕週の大会モード遷移は <see cref="GameClock.EnterWeek"/> に
    ///   集約済みなので、ここに大会判定は要らない。開幕時は BannerPending が立ち <see cref="ScreenRouter"/> が
    ///   ホームへ回送して開幕演出→日送りへ引き継ぐ）。
    /// ・大会モード中: 日送り・試合ダイアログはホームが担うため、側画面からはホームへ回送し、着いたホームで1日進める
    ///   （issue #1534 項目9: 大会中はどのタブでもボタンが「1日進める」なので、押した1回で日が進むようにする）。
    /// </summary>
    public static class WeekAdvance
    {
        private static bool _dayAdvanceRequested;

        /// <summary>側画面で大会中に「1日進める」が押されたか（ホームが着いたときに1回だけ取り出す）。</summary>
        public static bool TakeDayAdvanceRequest()
        {
            var r = _dayAdvanceRequested;
            _dayAdvanceRequested = false;
            return r;
        }

        /// <summary>側画面の「今週を進める」共通処理。<paramref name="render"/> は進週後の再描画（省略可）。</summary>
        public static void FromSideScreen(System.Action render)
        {
            // 新チームの背番号を確定するまでは週を進めない（issue #954）。メンバー設定画面へ戻す。
            if (UniformNumberService.Pending)
            {
                ScreenRouter.Instance?.ShowDeferred(ScreenRouter.UniformNumberScreen);
                return;
            }
            if (GameSession.Current.InTournament)
            {
                _dayAdvanceRequested = true;
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
                return;
            }
            // 見せていないイベントの場面があるなら、次の週へ進めずホームで見せる（issue #1527）。進めると場面が
            // ホームに行くまで溜まり続け、その間セーブもできなかった（システムタブで画面が変わらず週だけ進んだ）。
            if (EventService.HasPending)
            {
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
                return;
            }
            // 背景の試合後処理中は、ホームと同じく週を進めない（メインスレッドの GameClock/ロスター変更と競合する, issue #138）。
            if (BackgroundGameOp.Running) return;
            // 年契約の更新が未確定なら、ホームへ戻して更新の画面から続ける（ホームの週送りと同じ待ち）。
            if (ContractRenewalService.Pending)
            {
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
                return;
            }
            // 週の中身（練習・怪我・成長・調子・週次イベント・各種フィード）はホームの「今週を進める」と同じ
            // 入口 HomeState.AdvanceWeek を通す（issue #1113）。クロックだけ進めると中身が丸ごと飛ぶ。
            KokoSim.Unity.Home.HomeState.Current.AdvanceWeek();
            // ホームの週送りと同じく、初めて部費で何か買えるようになった週の説明を積む（issue #1496）。
            TutorialService.RaisePurchasesIfAffordable();
            render?.Invoke();
            // この週に積まれた場面（週次イベント・説明）はホームで見せる。新チーム発足・開幕バナー・転任・年契約の
            // 更新は ScreenRouter が回送する（イベントだけ回送の対象に無かった）。
            if (EventService.HasPending) ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
        }
    }
}
