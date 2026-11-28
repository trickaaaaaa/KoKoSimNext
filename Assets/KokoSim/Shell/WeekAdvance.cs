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
        // ブラウザ版で、裏の仕事を回し切るのを待っている週送りがあるか（#1588）。
        private static bool _sideAdvanceDeferred;

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
            // 体験版は2年目の夏が全部終わったところで止める（#1323）。週を進めず、ホームで終了画面を出す。
            if (DemoGate.StopBeforeAdvance)
            {
                DemoGate.RequestEndScreen();
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
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
            if (BackgroundGameOp.Running || LoadingJob.Running) return;
            // ブラウザ版は裏の仕事を回し切ってから進める（#1588・ホームの週送りと同じ）。
            if (BackgroundWork.Busy)
            {
                if (!_sideAdvanceDeferred)   // 連打しても1回だけ積む
                {
                    _sideAdvanceDeferred = true;
                    BackgroundWork.DeferUntilIdle(() => { _sideAdvanceDeferred = false; FromSideScreen(render); });
                }
                return;
            }
            // 年契約の更新が未確定なら、ホームへ戻して更新の画面から続ける（ホームの週送りと同じ待ち）。
            if (ContractRenewalService.Pending)
            {
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
                return;
            }
            // 重い節目の週（年度替わり・大会入り・AI校の節目成長など）は、ロードのポップアップを出し、裏シムが止まるのを
            // コマをまたいで待ってから進める（ホームの週送りと同じ判定）。普段の週はそのまま進める。
            if (GameClock.NextWeekIsHeavy())
            {
                var message = GameClock.NextWeekIsNewYear() ? "新年度の準備をしています" : null;
                LoadingJob.Run("進行中", message, HeavyRoutine(render));
                return;
            }
            Step(render);
        }

        private static System.Collections.IEnumerator HeavyRoutine(System.Action render)
        {
            yield return LoadingJob.WaitBackgroundIdle();
            Step(render);
        }

        private static void Step(System.Action render)
        {
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
