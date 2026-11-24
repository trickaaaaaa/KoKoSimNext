using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 体験版の区切り（2年目の夏まで・steam-roadmap 段階4・#1323）。範囲の判定は engine の
    /// <see cref="DemoScope"/>、ここは「体験版のビルドか」と「今の進行」を渡すだけ。
    /// 週を進める入口（ホームの <c>OnAdvance</c>・側画面の <see cref="WeekAdvance.FromSideScreen"/>）が
    /// <see cref="StopBeforeAdvance"/> を見て、止めるときは週を進めず終了画面を出す。
    /// </summary>
    public static class DemoGate
    {
        /// <summary>体験版のビルドか（KOKOSIM_DEMO）。</summary>
        public static bool Enabled
        {
#if KOKOSIM_DEMO
            get { return true; }
#elif UNITY_EDITOR
            get { return ForceInEditor; }
#else
            get { return false; }
#endif
        }

#if UNITY_EDITOR
        /// <summary>【エディタ限定】体験版として振る舞わせる（撮影・テスト用）。</summary>
        public static bool ForceInEditor;

        /// <summary>【エディタ限定・撮影用】暦だけを据える（区切りの直前の週を2年ぶん回さずに作る）。</summary>
        public static void PutClockForCapture(int yearIndex, int week)
            => GameClock.Restore(new KokoSim.Engine.Saves.SaveState { YearIndex = yearIndex, Week = week });
#endif

        /// <summary>
        /// 製品版のストアページ。ストアページを公開したら入れる。空の間は終了画面にボタンを出さない
        /// （2026-11-24 オーナー決定）。
        /// </summary>
        public const string StoreUrl = "";

        private static readonly SeasonCalendar Calendar = new SeasonCalendar();
        private static bool _endScreenRequested;

        /// <summary>夏の大会（県大会・甲子園・観るだけの甲子園）がまだ動いているか。</summary>
        private static bool SummerOngoing
            => GameSession.Current.InTournament || GameSession.Current.HasSpectatedKoshien;

        /// <summary>体験版で、週を進める前に止める（＝終了画面を出す）か。</summary>
        public static bool StopBeforeAdvance
            => Enabled && DemoScope.StopBeforeAdvance(GameClock.YearIndex, GameClock.Week, SummerOngoing, Calendar);

        /// <summary>側画面で止めたとき、ホームに着いたら終了画面を出すよう頼む。</summary>
        public static void RequestEndScreen() => _endScreenRequested = true;

        /// <summary>終了画面を頼まれていたか（ホームが着いたときに1回だけ取り出す）。</summary>
        public static bool TakeEndScreenRequest()
        {
            var r = _endScreenRequested;
            _endScreenRequested = false;
            return r;
        }
    }
}
