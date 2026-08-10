// 画面横断の共有ゲーム状態（大会モードの中枢）。UnityEngine 非依存（設計書06 §1）。
// 週/年度は GameClock、部員は RosterService が単一ソース。ここは大会モードの進行状態を保持する。
// HomeState は画面遷移のたびに作り直されるため、大会の進行（日・進行体・演出フラグ）は
// 静的なこのホルダーに置き、どの画面へ移動しても大会状態が失われないようにする。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Practice;
using KokoSim.Engine.Saves;
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
        /// 自校敗退直後、残りブラケットの背景消化（<see cref="BracketFinishSim"/>, issue #529）が終わるまで
        /// 保持する進行体（優勝校未確定の間だけ非null）。<see cref="Runner"/> は敗退確定と同時に null化する
        /// （画面遷移をブロックしないため）が、通算戦績の畳み込み（<see cref="Records"/>.FoldTournament）は
        /// ブラケット全カード確定後でないと集計できないため、別途ここへ確定時点の Runner/Kind/Year を控えて
        /// 消化完了後に確定させる。
        /// </summary>
        private TournamentRunner _bracketFoldRunner;
        private TournamentKind _bracketFoldKind;
        private int _bracketFoldYear;

        /// <summary>
        /// 残りブラケットの背景消化中で、通算戦績（FoldTournament）がまだ確定していないか。
        /// true の間はセーブを抑止する（<see cref="SaveGameIO.CanSave"/>）。
        /// </summary>
        public bool BracketFoldPending => _bracketFoldRunner != null;

        /// <summary>
        /// 自校選手の成績ストア（通算＝永続／今大会＝大会ごとリセット）。純エンジンの集計器を横断状態として保持。
        /// 詳細シムで回った自校戦のボックススコアを PlayMatch で畳み込む。スタメン画面の成績欄はこれを引く。
        /// </summary>
        public PlayerStatStore Stats { get; private set; } = new PlayerStatStore();

        /// <summary>
        /// 学校ごとの通算戦績（公式戦勝敗・甲子園出場回数, issue #84）。Stats と同様に大会をまたいで永続する。
        /// 大会終了時にブラケット全試合（自校戦＋裏試合）を <see cref="ExitTournamentIfFinished"/> で畳み込む。
        /// </summary>
        public SchoolRecordBook Records { get; private set; } = new SchoolRecordBook();

        /// <summary>
        /// 試合前スタメン設定（打順・守備位置・DH・先発）。試合開始フローのスタメン画面で確定して書き込む。
        /// null＝未設定（RosterTeamBuilder.Build の自動編成へフォールバック）。
        /// </summary>
        public LineupSpec Lineup { get; set; }

        /// <summary>
        /// 試合開始フローの中継フラグ。試合日「はい」でスタメン設定画面へ遷移する際に true。対戦カード画面
        /// （MatchPreview）の「試合開始」が <see cref="MatchStartFlow.Begin"/> でこれを消費して自校戦を始める
        /// （issue #325: ホームは中継しない）。キャンセルで false。開始に失敗した場合（二重起動）は戻される。
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
            KickPrefetch();            // 初戦ラウンドの自県裏カードを暇な時間に先行フルシムして温める。
        }

        // ===== 自県裏カードのプレフェッチ（#試合開始前ロード短縮） =====
        // 自県の裏カード（自校非関与）は1球フルシムが重い（約0.79ms/球×305球＝約240ms/試合、1回戦で約100試合＝
        // 約25秒）。これを「試合開始の瞬間にメインスレッドで一気に」ではなく「開幕〜次戦までの暇な時間に背景スレッドで
        // 先に解いて温める」ことでフリーズを消す。プレフェッチと本解決は BeginMatch/PlayMatch 冒頭の Join で必ず
        // 時間的に排他し（同時に台帳/乱数へ触れない）、本解決は MemoizingBackgroundResolver のキャッシュ命中で即返る。
        // 決定論は不変（BackgroundPrefetchTests でプレフェッチ有無のバイト一致を担保）。
        private Task _prefetchTask;

        /// <summary>現ラウンドの自県裏カードを背景スレッドで先行解決（キャッシュを温める）。自校の実対戦カードは対象外。</summary>
        private void KickPrefetch()
        {
            var runner = Runner;
            if (runner == null || runner.Finished || !runner.PlayerActive) return;
            _prefetchTask = Task.Run(() =>
            {
                try { runner.PrefetchCurrentRoundBackgroundCards(); }
                catch { /* 個々の県カードの失敗は握り潰す（本解決がフォールバックで解く） */ }
            });
        }

        /// <summary>プレフェッチ完了を待つ（本解決の直前に呼び、背景と前景の同時アクセスを避ける）。通常は既に完了済み＝即返る。</summary>
        private void JoinPrefetch()
        {
            var t = _prefetchTask;
            if (t == null) return;
            try { t.Wait(); } catch { /* 上の catch と同様に無視 */ }
            _prefetchTask = null;
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

        // ===== 練習試合のライブ観戦（issue #269） =====
        // 練習試合は必ずライブ観戦で行う＝一括消化（観戦しない）経路は持たない。時間短縮は MatchLive の
        // 速度チップ（×0.5〜×4）と「スキップ（委任）」で代替する。エンジン側の一括経路
        // （PracticeMatchScheduler.Request）は「観戦しても一括でも同じ試合になる」契約のテスト基準として残す。

        /// <summary>
        /// 練習試合の申込が成立して、スタメン設定→対戦カード→ライブ観戦 のフローに入っている相手校。
        /// null＝大会戦のフロー。<see cref="PendingMatchIsPractice"/> の実体。
        /// </summary>
        public School PracticeOpponent { get; private set; }

        /// <summary>試合開始フローが練習試合か（false＝大会戦）。</summary>
        public bool PendingMatchIsPractice => PracticeOpponent != null;

        /// <summary>
        /// 練習試合の相手校が確定した週（絶対週）。開始時に同じ乱数種を作り直すために持つ
        /// （<see cref="PracticeMatchRng"/>）。
        /// </summary>
        private int _practiceWeek;

        /// <summary>開始した練習試合で自校が先攻か（<see cref="PlayerMatchResolver.ManagerIsAway"/> の結果を保持）。</summary>
        private bool _practiceManagerIsAway;

        /// <summary>
        /// 練習試合の申込成立を記録し、試合開始フロー（スタメン設定→対戦カード→MatchLive）へ入る。
        /// 費用・週枠はまだ消費しない（<see cref="BeginPracticeMatch"/> で確定する）。
        /// </summary>
        public void EnterPracticeMatchFlow(School opponent, int week)
        {
            PracticeOpponent = opponent;
            _practiceWeek = week;
            AwaitingMatchStart = true;
        }

        /// <summary>試合開始フローを大会戦へ戻す（キャンセル・終局後の後始末）。</summary>
        public void ClearPracticeMatchFlow() => PracticeOpponent = null;

        /// <summary>
        /// 練習試合の乱数種。相手校ID＋絶対週から決定論で導く（不変条件#2）。同じ週に同じ相手なら同じ試合になる。
        /// 申込時の受諾判定（<see cref="PracticeMatchScheduler.TryAccept"/>）と開始時
        /// （<see cref="PracticeMatchScheduler.BeginRequest"/>）で同じ種を作り直すことで、
        /// 受諾判定の消費が揃い、一括消化（<see cref="PracticeMatchScheduler.Request"/>）と同じ試合になる。
        /// </summary>
        public static IRandomSource PracticeMatchRng(int opponentId, int week)
            => new Xoshiro256Random(0x9E37_79B9UL ^ (ulong)(long)opponentId ^ ((ulong)(long)week * 0x85EB_CA6BUL));

        /// <summary>
        /// 練習試合をライブ観戦で開始する。ここで費用と週枠が確定する（申込成立＝費用発生）。
        /// 大会の進行状態（Runner/LastOutcome/ResultPending）は一切触らない。
        /// </summary>
        public PracticeMatchLiveOutcome BeginPracticeMatch()
        {
            var opponent = PracticeOpponent;
            if (opponent == null) return null;
            var outcome = ManagerService.Practice.BeginRequest(
                ManagerService.Manager, NationService.ManagerSchool(), opponent, _practiceWeek,
                new PlayerMatchResolver(), PracticeMatchRng(opponent.Id, _practiceWeek));
            // 先攻/後攻は終局後の成績畳み込み（CompletePracticeMatch）でも使うのでここで控える。
            if (outcome.Live is { } live) _practiceManagerIsAway = live.ManagerIsAway;
            return outcome;
        }

        /// <summary>
        /// ライブ観戦した練習試合の終局結果を反映する（issue #269）。中身は一括消化
        /// （<see cref="PracticeMatchScheduler.Request"/> 経路）の後処理と同一＝通算スコープにだけ積み（isOfficial=false）、
        /// 実戦成長・調子・怪我を適用する。<b>大会の <see cref="CompleteMatch"/> は呼ばない</b>
        /// ＝練習試合は大会の進行状態を触らない契約。
        /// </summary>
        public void CompletePracticeMatch(GameResult result)
        {
            // 先攻/後攻は BeginPracticeMatch で解決済みの値を使う（対戦カード表示・実試合と必ず一致する）。
            var managerIsAway = _practiceManagerIsAway;
            Stats.FoldGame(result, managerIsAway, isOfficial: false);
            ApplyMatchGrowth(result, managerIsAway);
            ApplyMatchCondition(result, managerIsAway);
            ApplyMatchInjuries(result, managerIsAway);
            ClearPracticeMatchFlow();
        }

        /// <summary>自校の次戦を自動消化する。結果を UI 表示待ちにする。</summary>
        public PlayerMatchOutcome PlayMatch()
        {
            JoinPrefetch();   // 現ラウンドのプレフェッチを完了させてから本解決（背景と前景を排他）。
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
            KickPrefetch();   // 次ラウンドの自県裏カードを先行して温める（未終了時のみ）。
            return LastOutcome;
        }

        /// <summary>
        /// 自校の次戦を「ライブ観戦」で始める。返す進行体をUIが打席単位で進め、終局後 <see cref="CompleteMatch"/> へ結果を戻す。
        /// PlayMatch（一括自動消化）と決定論的に同じ大会展開になる（観戦してもしなくても結果は同じ）。
        /// </summary>
        public LivePlayerMatch BeginMatch()
        {
            JoinPrefetch();   // 現ラウンドのプレフェッチを完了させてから本解決（キャッシュ命中で即返る／背景と前景を排他）。
            return Runner.BeginNextPlayerMatch();
        }

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
            KickPrefetch();   // 次ラウンドの自県裏カードを先行して温める（未終了時のみ）。
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
            var runner = Runner;

            // 大会別アーカイブ（issue #77）: 今大会成績を「当時の学年×大会枠」＋当時の背番号で確定する。
            // ロスター情報だけで完結する＝ブラケットの残り消化を待たずに確定してよい。
            // 現状フローは夏(県)=SummerPref・秋(県)=Autumn のみ。甲子園/センバツ枠は tournament Phase 3 で populate。
            ArchiveTournamentStats();
            // 自校の成績（勝敗・優勝したか）は LastOutcome.IsChampion で既に確定済み（敗退時は常に false）。
            // 「大会全体の優勝校」の確定を待たずに出せる（issue #529 Plan A）。
            PendingTournamentWrapUp = new TournamentWrapUp(Title, TournamentDay, LastOutcome.IsChampion);
            Mode = GameMode.Normal;
            Runner = null;
            BannerPending = false;
            // ResultPending はここでは変えない＝結果モーダルは引き続き表示する。

            if (runner.BracketResolutionPending)
            {
                // issue #529: 敗退直後は残りブラケット（自校非関与の裏カード）が未消化＝大会全体の優勝校が
                // まだ確定していない。通算戦績（FoldTournament）はブラケット全カードが必要なので、ここでは
                // 待たずに BracketFinishSim（NationBackgroundSim と同型のスライス駆動）へ渡し、完了後に
                // メインスレッドで確定する。画面遷移（大会モード離脱）は既に上で完了している＝ここから先は
                // 「閉じる」を押した側を待たせない。
                _bracketFoldRunner = runner;
                _bracketFoldKind = Kind;
                _bracketFoldYear = Year;
                BracketFinishSim.Start(runner, CompleteBracketFold);
            }
            else
            {
                // 優勝時（残り試合なし）はここで同期に畳み込む＝従来どおり即確定。
                Records.FoldTournament(runner.BuildBracketView().Matches, Kind, Year);
            }
        }

        /// <summary>
        /// 残りブラケットの背景消化（<see cref="BracketFinishSim"/>）が完了した後、通算戦績を確定する
        /// （issue #529。メインスレッドから <see cref="MainThreadDispatcher"/> 経由で呼ばれる）。
        /// </summary>
        private void CompleteBracketFold()
        {
            var runner = _bracketFoldRunner;
            if (runner == null) return;
            // ブラケット全試合（自校戦＋裏試合）を通算戦績へ畳み込む（issue #84）。夏の優勝校は甲子園出場として記録。
            Records.FoldTournament(runner.BuildBracketView().Matches, _bracketFoldKind, _bracketFoldYear);
            _bracketFoldRunner = null;
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

        // ===== セーブ往復（issue #507 F0 ／ 大会中は issue #565 F1） =====
        // 大会をまたぐ永続分（年度・成績・戦績）に加え、大会モードの進行（進行体・大会内経過日・出場校）も
        // 保存する。**試合と試合の間だけ**＝観戦中（MatchProgression を持つ）の中断は F2。
        // 演出フラグ（結果モーダル待ち）・試合フロー中継（スタメン設定〜試合開始）は保存しない
        // ＝それらの途中では保存させない（SaveGameIO.CanSave）。

        /// <summary>セーブ保存: 年度・成績ストア・戦績＋（大会中なら）大会の進行を DTO へ写す。</summary>
        public void Capture(SaveState s)
        {
            s.SessionYear = Year;
            s.Stats = Stats;
            s.Records = Records;
            s.Tournament = CaptureTournament();
        }

        /// <summary>大会モードの進行を DTO へ（通常モード・保存できない局面なら null）。</summary>
        private TournamentSaveState CaptureTournament()
        {
            if (!InTournament || !Runner.CanCaptureState) return null;
            var ids = new List<int>(Field.Count);
            foreach (var s in Field) ids.Add(s.Id);
            return new TournamentSaveState
            {
                Kind = Kind,
                Title = Title,
                TournamentDay = TournamentDay,
                BannerPending = BannerPending,
                FieldSchoolIds = ids,
                Runner = Runner.CaptureState(),
            };
        }

        /// <summary>
        /// セーブ復元: 年度・成績・戦績を据え、進行状態を初期化する。
        /// <b>大会の進行体はここでは組まない</b>＝復元には resolver 一式（Shell の部品）が要るので
        /// <see cref="TournamentEntry.RestoreRunner"/> が <see cref="RestoreTournament"/> 経由で押し戻す
        /// （engine 純度の都合ではなく、依存の向きを Shell→engine の一方向に保つため）。
        /// </summary>
        public void Restore(SaveState s)
        {
            Year = s.SessionYear;
            Stats = s.Stats;
            Records = s.Records;
            ClearTransient();
        }

        /// <summary>
        /// 大会モードへ復帰する（issue #565 F1。<see cref="TournamentEntry.RestoreRunner"/> から呼ぶ）。
        /// <see cref="EnterTournament"/> との違いは2つ＝<b>今大会成績をリセットしない</b>（保存された成績を
        /// そのまま使う）ことと、<b>大会内経過日を0へ戻さない</b>こと。
        /// </summary>
        public void RestoreTournament(TournamentSaveState t, TournamentRunner runner, IReadOnlyList<School> field)
        {
            Mode = GameMode.Tournament;
            Kind = t.Kind;
            Title = t.Title;
            Runner = runner;
            Field = field ?? Array.Empty<School>();
            TournamentDay = t.TournamentDay;
            BannerPending = t.BannerPending;
            ResultPending = false;
            LastOutcome = null;
            KickPrefetch();   // 次戦ラウンドの自県裏カードを温め直す（キャッシュはセーブに載らない）。
        }

        /// <summary>新規ゲームの完全リセット: 成績・戦績を新品にし、進行状態を初期化する。</summary>
        public void ResetForNewGame()
        {
            Stats = new PlayerStatStore();
            Records = new SchoolRecordBook();
            Year = 1;
            ClearTransient();
        }

        /// <summary>進行体・演出フラグ・試合フロー中継・通知バッファを初期状態へ（保存対象外の一時状態）。</summary>
        private void ClearTransient()
        {
            Mode = GameMode.Normal;
            Kind = default;
            Title = "";
            Runner = null;
            Field = Array.Empty<School>();
            TournamentDay = 0;
            BannerPending = false;
            ResultPending = false;
            LastOutcome = null;
            PendingTournamentWrapUp = null;
            Lineup = null;
            AwaitingMatchStart = false;
            PracticeOpponent = null;
            _prefetchTask = null;
            // 新規ゲーム/ロードは Records を丸ごと差し替える＝背景消化中だった旧大会の FoldTournament は
            // 適用先を失うので参照だけ切る（issue #529。CompleteBracketFold は null チェックで安全に無視する）。
            _bracketFoldRunner = null;
            _matchInjurySeq = 0;
            _injuryNotices.Clear();
        }
    }
}
