// 画面横断の共有ゲーム状態（大会モードの中枢）。UnityEngine 非依存（設計書06 §1）。
// 週/年度は GameClock、部員は RosterService が単一ソース。ここは大会モードの進行状態を保持する。
// HomeState は画面遷移のたびに作り直されるため、大会の進行（日・進行体・演出フラグ）は
// 静的なこのホルダーに置き、どの画面へ移動しても大会状態が失われないようにする。
using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Practice;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    public enum GameMode { Normal, Tournament }

    /// <summary>終了した大会の後始末に必要な情報（issue #139: Runner が null化された後も参照できるようにする）。</summary>
    public sealed class TournamentWrapUp
    {
        public string Title { get; }
        public int TournamentDay { get; }
        public bool IsChampion { get; }

        public TournamentWrapUp(string title, int tournamentDay, bool isChampion)
        {
            Title = title;
            TournamentDay = tournamentDay;
            IsChampion = isChampion;
        }
    }

    /// <summary>大会モードの進行状態（現在の進行体・大会内経過日・演出フラグ）を全画面へ共有する。</summary>
    public sealed class GameSession
    {
        public static GameSession Current { get; } = new GameSession();

        public GameMode Mode { get; private set; } = GameMode.Normal;
        public TournamentKind Kind { get; private set; }
        public string Title { get; private set; } = "";
        public TournamentRunner Runner { get; private set; }
        public int Year { get; set; } = 1;

        /// <summary>
        /// 今大会の出場校（自校を含む）。TournamentRunner は entrants を外部公開しないため、
        /// 大会展望（優勝候補・注目選手・登録メンバー）が実際の出場校を引けるようここで保持する。
        /// </summary>
        public IReadOnlyList<School> Field { get; private set; } = Array.Empty<School>();

        /// <summary>大会内経過日（開幕=0日目）。日送りで +1。</summary>
        public int TournamentDay { get; private set; }
        /// <summary>開幕演出をUIが未表示か（表示したら ConsumeBanner で下ろす）。</summary>
        public bool BannerPending { get; private set; }
        /// <summary>直近の試合結果をUIが未表示か。</summary>
        public bool ResultPending { get; private set; }
        public PlayerMatchOutcome LastOutcome { get; private set; }

        /// <summary>
        /// 直前に終了した大会の後始末（週送り・通知フィード）がまだ済んでいなければその情報を持つ（issue #139）。
        /// モード自体は敗退/優勝を検知した瞬間に <see cref="Mode"/>=Normal へ戻すが、時計送りと通知は
        /// 従来どおり結果モーダルを閉じたタイミング（<see cref="ConsumeTournamentWrapUp"/>）で行う。
        /// </summary>
        public TournamentWrapUp? PendingTournamentWrapUp { get; private set; }

        /// <summary>
        /// 自校選手の成績ストア（通算＝永続／今大会＝大会ごとリセット）。純エンジンの集計器を横断状態として保持。
        /// 詳細シムで回った自校戦のボックススコアを PlayMatch で畳み込む。スタメン画面の成績欄はこれを引く。
        /// </summary>
        public PlayerStatStore Stats { get; } = new PlayerStatStore();

        /// <summary>
        /// 学校ごとの通算戦績（公式戦勝敗・甲子園出場回数, issue #84）。Stats と同様に大会をまたいで永続する。
        /// 大会終了時にブラケット全試合（自校戦＋裏試合）を <see cref="ExitTournamentIfFinished"/> で畳み込む。
        /// </summary>
        public SchoolRecordBook Records { get; } = new SchoolRecordBook();

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
        /// <param name="field">出場校（自校を含む）。大会展望が実際の出場校を引くために保持する。</param>
        public void EnterTournament(TournamentKind kind, string title, TournamentRunner runner,
            IReadOnlyList<School> field = null)
        {
            Mode = GameMode.Tournament;
            Kind = kind;
            Title = title;
            Runner = runner;
            Field = field ?? Array.Empty<School>();
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

        // 実戦成長（設計書02 §5.3a, Q8）用の暦・係数。HomeState の練習適用と同じ既定値運用。
        private static readonly SeasonCalendar GrowthCalendar = new SeasonCalendar();
        private static readonly GrowthStageTable GrowthStages = new GrowthStageTable();
        private static readonly TrainingCoefficients GrowthTraining = new TrainingCoefficients();
        // 調子（設計書02 §3.3）用の係数。週次AR(1)（HomeState）と同じ既定値運用。
        private static readonly KokoSim.Engine.Players.FormCoefficients FormCoeff =
            new KokoSim.Engine.Players.FormCoefficients();

        /// <summary>
        /// 実戦成長ループ（Q8）: 詳細シムの自校戦1試合ぶん、出場者の精神力/走塁判断/捕手リードを伸ばす。
        /// 成績畳み込み（FoldGame）と同じ合流点で適用（PlayMatch/CompleteMatch の双方から1回ずつ）。
        /// </summary>
        private void ApplyMatchGrowth(GameResult detail, bool managerWasAway)
            => MatchGrowthModel.Apply(detail, managerWasAway, RosterService.Roster,
                GameClock.Week, GrowthCalendar, GrowthStages, GrowthTraining);

        /// <summary>
        /// 試合結果による調子フィードバック（設計書02 §3.3, issue #46）: 詳細シムの自校戦1試合ぶん、
        /// 出場者の ConditionValue を好投/好打で+、被弾/大敗で−へ動かす。実戦成長と同じ合流点で適用。
        /// </summary>
        private void ApplyMatchCondition(GameResult detail, bool managerWasAway)
            => MatchConditionModel.Apply(detail, managerWasAway, RosterService.Roster, FormCoeff);

        // 怪我（設計書03 §3.5）。週次処理（HomeState.RunInjuryWeek）と同じ既定係数を使う。
        private static readonly InjuryCoefficients InjuryCoeff = new InjuryCoefficients();
        private int _matchInjurySeq;

        /// <summary>
        /// 試合後の怪我処理（issue #29 B/C）: 試合中に発生した受傷をロスターへ反映し、
        /// 怪我を押して出場した選手の悪化・全治延長を判定する。実戦成長と同じ合流点で1試合1回だけ呼ぶ。
        /// 結果は通知フィード用に貯め、ホーム画面が <see cref="DrainInjuryNotices"/> で引き取る。
        /// </summary>
        private void ApplyMatchInjuries(GameResult detail, bool managerWasAway)
        {
            // 試合ごとに独立した決定論ストリーム（週・年度・試合連番から導出）。
            var rng = new Xoshiro256Random(
                0x2913_0000UL ^ (ulong)(GameClock.YearIndex * 10000 + GameClock.Week * 100 + (++_matchInjurySeq)));
            var outcomes = MatchInjuryLedger.Apply(detail, managerWasAway, RosterService.Roster, rng, InjuryCoeff);
            if (outcomes.Count > 0) _injuryNotices.AddRange(outcomes);
        }

        private readonly List<MatchInjuryOutcome> _injuryNotices = new List<MatchInjuryOutcome>();

        /// <summary>試合後の怪我処理の結果を取り出して空にする（通知フィードへ流す用）。</summary>
        public List<MatchInjuryOutcome> DrainInjuryNotices()
        {
            var list = new List<MatchInjuryOutcome>(_injuryNotices);
            _injuryNotices.Clear();
            return list;
        }

        /// <summary>
        /// 練習試合を申し込み、成立したら消化する（設計書03 §週ターン③）。週1制約・資金・受諾判定は
        /// エンジン（<see cref="PracticeMatchScheduler"/>）が持つ。成績は通算スコープにだけ積み
        /// （isOfficial=false ＝ 公式戦通算・今大会には残さない）、実戦成長は公式戦と同じく発生させる。
        /// 大会の進行状態（LastOutcome/ResultPending）は触らない＝大会の演出フローと混ざらない。
        /// </summary>
        public PracticeMatchOutcome PlayPracticeMatch(School managerSchool, School opponent, IRandomSource rng)
        {
            var outcome = ManagerService.Practice.Request(
                ManagerService.Manager, managerSchool, opponent, ManagerService.AbsoluteWeek,
                new PlayerMatchResolver(), rng);

            if (outcome.Detail is { } detail)
            {
                Stats.FoldGame(detail.Result, detail.ManagerIsAway, isOfficial: false);
                ApplyMatchGrowth(detail.Result, detail.ManagerIsAway);
                ApplyMatchCondition(detail.Result, detail.ManagerIsAway);
                ApplyMatchInjuries(detail.Result, detail.ManagerIsAway);
            }
            return outcome;
        }

        /// <summary>自校の次戦を自動消化する。結果を UI 表示待ちにする。</summary>
        public PlayerMatchOutcome PlayMatch()
        {
            LastOutcome = Runner.PlayNextPlayerMatch();
            // 詳細シムで回った自校戦なら、ボックススコアを通算/今大会成績へ畳み込む。
            if (LastOutcome.Detail is { } detail)
            {
                Stats.FoldGame(detail, LastOutcome.ManagerWasAway);
                ApplyMatchGrowth(detail, LastOutcome.ManagerWasAway);
                ApplyMatchCondition(detail, LastOutcome.ManagerWasAway);
                ApplyMatchInjuries(detail, LastOutcome.ManagerWasAway);
            }
            ResultPending = true;
            ExitTournamentIfFinished();
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
            {
                Stats.FoldGame(detail, LastOutcome.ManagerWasAway);
                ApplyMatchGrowth(detail, LastOutcome.ManagerWasAway);
                ApplyMatchCondition(detail, LastOutcome.ManagerWasAway);
                ApplyMatchInjuries(detail, LastOutcome.ManagerWasAway);
            }
            ResultPending = true;
            ExitTournamentIfFinished();
            return LastOutcome;
        }

        public void ConsumeResult() => ResultPending = false;

        /// <summary>
        /// 大会が自校にとって終了していれば（優勝 or 敗退）、結果モーダルの表示状態はそのまま保って
        /// 大会モードだけ即座に抜ける（issue #139）。以前はこの離脱が結果モーダルのOKクリック
        /// （<see cref="Unity.Home.HomeState.DismissResult"/>）だけに依存しており、OKへ辿り着けない限り
        /// <see cref="Mode"/> が Tournament のまま残り、終了済みランナーの下で日送りがループし続けた
        /// （<see cref="TournamentDay"/> だけが進み、共有クロック <see cref="GameClock"/> は進まない）。
        /// 時計送り・通知フィードは従来どおり <see cref="ConsumeTournamentWrapUp"/> 経由でOKクリック時に行う。
        /// </summary>
        private void ExitTournamentIfFinished()
        {
            if (Runner == null || !Runner.Finished) return;
            // ブラケット全試合（自校戦＋裏試合）を通算戦績へ畳み込む（issue #84）。夏の優勝校は甲子園出場として記録。
            Records.FoldTournament(Runner.BuildBracketView().Matches, Kind, Year);
            // 大会別アーカイブ（issue #77）: 今大会成績を「当時の学年×大会枠」＋当時の背番号で確定する。
            // 現状フローは夏(県)=SummerPref・秋(県)=Autumn のみ。甲子園/センバツ枠は tournament Phase 3 で populate。
            ArchiveTournamentStats();
            PendingTournamentWrapUp = new TournamentWrapUp(Title, TournamentDay, LastOutcome.IsChampion);
            Mode = GameMode.Normal;
            Runner = null;
            BannerPending = false;
            // ResultPending はここでは変えない＝結果モーダルは引き続き表示する。
        }

        /// <summary>
        /// 今大会成績を大会別アーカイブへ確定する（issue #77）。TournamentKind→TournamentSlot を写し、
        /// 在籍部員の (当時の学年, 当時の背番号) を sourceId=DevelopingPlayer.Id で引いて渡す。
        /// </summary>
        private void ArchiveTournamentStats()
        {
            var slot = Kind == TournamentKind.Autumn ? TournamentSlot.Autumn : TournamentSlot.SummerPref;
            var info = new Dictionary<int, (int Grade, int UniformNumber)>();
            foreach (var p in RosterService.Roster)
                if (p.Id > 0) info[p.Id] = (p.Grade, p.UniformNumber);
            Stats.ArchiveCurrentTournament(slot, info);
        }

        /// <summary>大会終了の後始末情報を取り出して消費する（結果モーダルのOKクリックから1回だけ呼ぶ）。</summary>
        public TournamentWrapUp? ConsumeTournamentWrapUp()
        {
            var w = PendingTournamentWrapUp;
            PendingTournamentWrapUp = null;
            return w;
        }
    }
}
