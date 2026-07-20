// 画面横断の共有ゲーム状態（大会モードの中枢）。UnityEngine 非依存（設計書06 §1）。
// 週/年度は GameClock、部員は RosterService が単一ソース。ここは大会モードの進行状態を保持する。
// HomeState は画面遷移のたびに作り直されるため、大会の進行（日・進行体・演出フラグ）は
// 静的なこのホルダーに置き、どの画面へ移動しても大会状態が失われないようにする。
using System;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    public enum GameMode { Normal, Tournament }

    /// <summary>大会モードの進行状態（現在の進行体・大会内経過日・演出フラグ）を全画面へ共有する。</summary>
    public sealed class GameSession
    {
        public static GameSession Current { get; } = new GameSession();

        public GameMode Mode { get; private set; } = GameMode.Normal;
        public TournamentKind Kind { get; private set; }
        public string Title { get; private set; } = "";
        public TournamentRunner Runner { get; private set; }
        public int Year { get; set; } = 1;

        /// <summary>大会内経過日（開幕=0日目）。日送りで +1。</summary>
        public int TournamentDay { get; private set; }
        /// <summary>開幕演出をUIが未表示か（表示したら ConsumeBanner で下ろす）。</summary>
        public bool BannerPending { get; private set; }
        /// <summary>直近の試合結果をUIが未表示か。</summary>
        public bool ResultPending { get; private set; }
        public PlayerMatchOutcome LastOutcome { get; private set; }

        /// <summary>
        /// 自校選手の成績ストア（通算＝永続／今大会＝大会ごとリセット）。純エンジンの集計器を横断状態として保持。
        /// 詳細シムで回った自校戦のボックススコアを PlayMatch で畳み込む。スタメン画面の成績欄はこれを引く。
        /// </summary>
        public PlayerStatStore Stats { get; } = new PlayerStatStore();

        /// <summary>
        /// 試合前スタメン設定（打順・守備位置・DH・先発）。試合開始フローのスタメン画面で確定して書き込む。
        /// null＝未設定（RosterTeamBuilder.Build の自動編成へフォールバック）。
        /// </summary>
        public LineupSpec Lineup { get; set; }

        /// <summary>
        /// 試合開始フローの中継フラグ。試合日「はい」でスタメン設定画面へ遷移する際に true。ホーム復帰時に
        /// これが立っていれば自校戦を消化する（スタメン画面OKで確定→ホームで試合実行）。キャンセルで false。
        /// </summary>
        public bool AwaitingMatchStart { get; set; }

        public bool InTournament => Mode == GameMode.Tournament && Runner != null;

        /// <summary>次戦までの残り日数（開催中のみ・負値は0）。</summary>
        public int DaysUntilNextMatch =>
            !InTournament || Runner.Finished ? 0 : Math.Max(0, Runner.NextMatchDay - TournamentDay);

        /// <summary>今日が試合日に到達したか（惰性スキップ防止＝到達日以降は真）。</summary>
        public bool ReachedMatchDay =>
            InTournament && !Runner.Finished && TournamentDay >= Runner.NextMatchDay;

        /// <summary>大会モードに入る（HomeState が構築した進行体を公開し、開幕演出を要求する）。</summary>
        public void EnterTournament(TournamentKind kind, string title, TournamentRunner runner)
        {
            Mode = GameMode.Tournament;
            Kind = kind;
            Title = title;
            Runner = runner;
            TournamentDay = 0;
            BannerPending = true;
            ResultPending = false;
            LastOutcome = null;
            Stats.StartTournament();   // 今大会成績をリセット（通算は保持）
        }

        public void ConsumeBanner() => BannerPending = false;

        /// <summary>日を1日進める。練習1週ぶん（7日）を跨いだら true を返す（呼び出し側が練習を適用）。</summary>
        public bool AdvanceDay()
        {
            if (!InTournament) return false;
            TournamentDay++;
            return TournamentDay % 7 == 0;
        }

        /// <summary>自校の次戦を自動消化する。結果を UI 表示待ちにする。</summary>
        public PlayerMatchOutcome PlayMatch()
        {
            LastOutcome = Runner.PlayNextPlayerMatch();
            // 詳細シムで回った自校戦なら、ボックススコアを通算/今大会成績へ畳み込む。
            if (LastOutcome.Detail is { } detail)
                Stats.FoldGame(detail, LastOutcome.ManagerWasAway);
            ResultPending = true;
            return LastOutcome;
        }

        /// <summary>
        /// 自校の次戦を「ライブ観戦」で始める。返す進行体をUIが打席単位で進め、終局後 <see cref="CompleteMatch"/> へ結果を戻す。
        /// PlayMatch（一括自動消化）と決定論的に同じ大会展開になる（観戦してもしなくても結果は同じ）。
        /// </summary>
        public LivePlayerMatch BeginMatch() => Runner.BeginNextPlayerMatch();

        /// <summary>
        /// ライブ観戦した自校戦の終局結果を大会へ反映する。ボックススコアを成績へ畳み込み、結果表示待ちにする。
        /// </summary>
        public PlayerMatchOutcome CompleteMatch(GameResult result)
        {
            LastOutcome = Runner.CompleteNextPlayerMatch(result);
            if (LastOutcome.Detail is { } detail)
                Stats.FoldGame(detail, LastOutcome.ManagerWasAway);
            ResultPending = true;
            return LastOutcome;
        }

        public void ConsumeResult() => ResultPending = false;

        /// <summary>通常モードへ戻す。</summary>
        public void ExitTournament()
        {
            Mode = GameMode.Normal;
            Runner = null;
            BannerPending = false;
            ResultPending = false;
        }
    }
}
