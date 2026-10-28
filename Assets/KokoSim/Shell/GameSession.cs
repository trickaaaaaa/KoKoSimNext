// 画面横断の共有ゲーム状態（大会モードの中枢）。UnityEngine 非依存（設計書06 §1）。
// 週/年度は GameClock、部員は RosterService が単一ソース。ここは大会モードの進行状態を保持する。
// HomeState は画面遷移のたびに作り直されるため、大会の進行（日・進行体・演出フラグ）は
// 静的なこのホルダーに置き、どの画面へ移動しても大会状態が失われないようにする。
using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// 「大会・全国」タブが描く大会（設計書05 §1.1a）。自校が対話的に消化している大会と、
    /// 自校が出場していない甲子園（観るだけ）の<b>どちらも同じ形</b>で渡す＝画面側が2種類を分岐しない。
    /// </summary>
    public sealed class TournamentDisplay
    {
        public TournamentKind Kind { get; }
        public string Title { get; }
        public TournamentRunner Runner { get; }
        public IReadOnlyList<School> Field { get; }
        /// <summary>自校が出場していて対話的に消化中か（false＝観るだけ）。</summary>
        public bool IsLive { get; }

        public TournamentDisplay(TournamentKind kind, string title, TournamentRunner runner,
            IReadOnlyList<School> field, bool isLive)
        {
            Kind = kind;
            Title = title;
            Runner = runner;
            Field = field ?? Array.Empty<School>();
            IsLive = isLive;
        }
    }

    /// <summary>終了した大会の後始末に必要な情報（issue #139: Runner が null化された後も参照できるようにする）。</summary>
    public sealed class TournamentWrapUp
    {
        public string Title { get; }
        public int TournamentDay { get; }
        public bool IsChampion { get; }
        /// <summary>終わった大会の種別。夏の県大会なら後始末から甲子園へつなぐ（設計書05 §1.1a）。</summary>
        public TournamentKind Kind { get; }

        public TournamentWrapUp(string title, int tournamentDay, bool isChampion, TournamentKind kind)
        {
            Title = title;
            TournamentDay = tournamentDay;
            IsChampion = isChampion;
            Kind = kind;
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
        /// 自校の歴代大会結果（issue #600・設計書06 §3.9）。<see cref="Records"/> が累計へ畳んで捨てる
        /// 「年度ごとの1行」を残す台帳＝記録タブの「歴代大会結果」の源。
        /// </summary>
        public TournamentHistory History { get; private set; } = new TournamentHistory();

        /// <summary>
        /// 試合前スタメン設定（打順・守備位置・DH・先発）。試合開始フローのスタメン画面で確定して書き込む。
        /// null＝未設定（RosterTeamBuilder.Build の自動編成へフォールバック）。
        /// </summary>
        public LineupSpec Lineup { get; set; }

        /// <summary>
        /// 前の試合で確定したスタメンの記憶（issue lineup-carry-over-previous-game）。スタメン設定画面が
        /// 既定として復元する（打順・守備位置・DH・先発）。<see cref="Lineup"/> と違い選手IDで持つ純データで
        /// **セーブに載る**（v14）＝ロード後も引き継ぐ。null＝まだ一度も確定していない（自動シード）。
        /// </summary>
        public LineupMemory LastLineup { get; set; }

        /// <summary>
        /// 試合開始フローの中継フラグ。試合日「はい」でスタメン設定画面へ遷移する際に true。対戦カード画面
        /// （MatchPreview）の「試合開始」が <see cref="MatchStartFlow.Begin"/> でこれを消費して自校戦を始める
        /// （issue #325: ホームは中継しない）。キャンセルで false。開始に失敗した場合（二重起動）は戻される。
        /// </summary>
        public bool AwaitingMatchStart { get; set; }

        public bool InTournament => Mode == GameMode.Tournament && Runner != null;

        // ===== 観るだけの甲子園（自校が出場していない年・設計書05 §1.1a） =====
        // 自校が県予選で敗退しても甲子園は開催され、展望とトーナメント表は閲覧できる（オーナー決定 2026-08-21）。
        // 対話モード（Mode=Tournament）には入らない＝プレイヤーは通常どおり週を進め、その経過日ぶんだけ
        // ブラケットが消化される。進行体はセーブに載る（載せないとロード後に優勝校が永久に決まらない）。

        /// <summary>観るだけの甲子園の進行体。null＝開催していない（自校が出場した年は <see cref="Runner"/> 側）。</summary>
        public TournamentRunner KoshienRunner { get; private set; }

        /// <summary>観るだけの甲子園の出場校（49代表）。</summary>
        public IReadOnlyList<School> KoshienField { get; private set; } = Array.Empty<School>();

        /// <summary>観るだけの甲子園の大会名。</summary>
        public string KoshienTitle { get; private set; } = "";

        /// <summary>観るだけの甲子園の大会内経過日（開幕=0日目）。週送りで +7。</summary>
        public int KoshienDay { get; private set; }

        /// <summary>観るだけの甲子園の総ラウンド数（日程を引くための母数）。</summary>
        private int _koshienTotalRounds;

        /// <summary>観るだけの甲子園が開催中か（展望・トーナメント表の閲覧可否）。</summary>
        public bool HasSpectatedKoshien => KoshienRunner != null;

        // ===== 観るだけのセンバツ（自校が選ばれていない年・設計書05 §1.5b） =====
        // 神宮大会が終わった時点で32校は確定済み（<see cref="PendingSenbatsuFieldIds"/>）だが、実際の
        // 大会モードへの遷移は選考発表週（<see cref="SeasonCalendar.SenbatsuAnnounceWeek"/>）から。
        // 観戦は夏の甲子園と同型（週送りで日程どおりブラケットが埋まる）。

        /// <summary>選考発表を待つセンバツ32校（校ID・順位は問わない）。空＝未確定 or 発表済み。</summary>
        public List<int> PendingSenbatsuFieldIds { get; private set; } = new List<int>();

        /// <summary>選考発表時点の神宮枠地区（表示用）。空文字＝該当なし。</summary>
        public string PendingSenbatsuJinguRegion { get; private set; } = "";

        /// <summary>観るだけのセンバツの進行体。null＝開催していない（自校が出場した年は <see cref="Runner"/> 側）。</summary>
        public TournamentRunner SenbatsuRunner { get; private set; }

        /// <summary>観るだけのセンバツの出場校（32代表）。</summary>
        public IReadOnlyList<School> SenbatsuField { get; private set; } = Array.Empty<School>();

        /// <summary>観るだけのセンバツの大会名。</summary>
        public string SenbatsuTitle { get; private set; } = "";

        /// <summary>観るだけのセンバツの大会内経過日（開幕=0日目）。週送りで +7。</summary>
        public int SenbatsuDay { get; private set; }

        private int _senbatsuTotalRounds;

        /// <summary>観るだけのセンバツが開催中か（展望・トーナメント表の閲覧可否）。</summary>
        public bool HasSpectatedSenbatsu => SenbatsuRunner != null;

        /// <summary>
        /// 観るだけのセンバツが決着した直後に1回だけ立つ優勝校名（<see cref="ConsumeKoshienResult"/> と同型）。
        /// </summary>
        public string PendingSenbatsuChampion { get; private set; }

        /// <summary>センバツ選考結果を控える（<see cref="JinguEntry"/> から呼ぶ。乱数は消費しない）。</summary>
        public void SetPendingSenbatsuField(IReadOnlyList<int> fieldIds, string jinguRegion)
        {
            PendingSenbatsuFieldIds = new List<int>(fieldIds);
            PendingSenbatsuJinguRegion = jinguRegion ?? "";
        }

        /// <summary>観るだけのセンバツを開く（<see cref="SenbatsuEntry"/> から呼ぶ）。対話モードには入らない。</summary>
        public void EnterSpectatedSenbatsu(string title, TournamentRunner runner, IReadOnlyList<School> field)
        {
            SenbatsuTitle = title;
            SenbatsuRunner = runner;
            SenbatsuField = field ?? Array.Empty<School>();
            SenbatsuDay = 0;
            _senbatsuTotalRounds = runner.RemainingBracketRounds;
            PendingSenbatsuChampion = null;
        }

        /// <summary>セーブから観るだけのセンバツへ復帰する（<see cref="SenbatsuEntry.RestoreSpectated"/> から）。</summary>
        public void RestoreSpectatedSenbatsu(TournamentSaveState t, TournamentRunner runner, IReadOnlyList<School> field)
        {
            SenbatsuTitle = t.Title;
            SenbatsuRunner = runner;
            SenbatsuField = field ?? Array.Empty<School>();
            SenbatsuDay = t.TournamentDay;
            _senbatsuTotalRounds = RoundsFor(SenbatsuField.Count);
        }

        /// <summary>観るだけのセンバツを <paramref name="days"/> 日ぶん進める（<see cref="AdvanceSpectatedKoshien"/> と同型）。</summary>
        public void AdvanceSpectatedSenbatsu(int days)
        {
            var runner = SenbatsuRunner;
            if (runner == null) return;
            SenbatsuDay += Math.Max(0, days);

            while (runner.BracketResolutionPending)
            {
                var roundIndex = _senbatsuTotalRounds - runner.RemainingBracketRounds;
                var cards = Math.Max(1, 1 << Math.Max(0, _senbatsuTotalRounds - roundIndex - 1));
                var lastCardDay = TournamentEntry.Schedule.MatchDay(_senbatsuTotalRounds, roundIndex, cards - 1, cards);
                if (SenbatsuDay < lastCardDay) return;
                runner.StepBracketResolution();
            }

            FoldTournamentRecords(runner, TournamentKind.SenbatsuKoshien, Year);
            PendingSenbatsuChampion = runner.ChampionName;
            SenbatsuRunner = null;
            SenbatsuField = Array.Empty<School>();
            SenbatsuTitle = "";
            SenbatsuDay = 0;
        }

        /// <summary>観るだけのセンバツの優勝校を取り出して消費する（<see cref="ConsumeKoshienResult"/> と同型）。</summary>
        public string ConsumeSenbatsuResult()
        {
            var champion = PendingSenbatsuChampion;
            PendingSenbatsuChampion = null;
            return champion;
        }

        /// <summary>
        /// 直近に終わった夏の県大会の優勝校ID（＝自県の甲子園代表）。null＝まだ決勝が消化されていない。
        /// <see cref="KoshienEntry"/> だけが読む中継値。
        ///
        /// <para><b>セーブに載る</b>（v19）。かつては「この値が必要な窓では <see cref="SaveGameIO.CanSave"/> が
        /// 保存自体を抑止している」ため一時値でよかったが、issue #565 F1 で試合と試合の間の保存が
        /// 解禁された時点でその前提は崩れていた＝大会連鎖の途中でロードすると後段が動かなくなる
        /// （issue autumn-chain-placements-not-saved）。</para>
        /// </summary>
        public int? LastPrefChampionId { get; private set; }

        /// <summary>
        /// 直近に終わった秋季地区予選の優勝校ID（＝秋季県大会の自地区代表）。null＝まだ地区予選の決勝が
        /// 消化されていない、または自校の県が地区予選を持たない（<see cref="DistrictEntry"/> だけが読む
        /// 中継値。<see cref="LastPrefChampionId"/> と同じ理由でセーブに載る・v20）。
        /// </summary>
        public int? LastDistrictChampionId { get; private set; }

        /// <summary>
        /// 直近に終わった秋季地区予選の県大会進出校ID（順位順の上位 advance_per_group 校・v22・issue #688）。
        /// null＝未確定（旧セーブ含む。その場合 <see cref="DistrictEntry"/> は優勝校1校だけを進出校とする）。
        /// </summary>
        public IReadOnlyList<int> LastDistrictQualifierIds { get; private set; }

        /// <summary>
        /// 直近に終わった秋季県大会の順位付き出場校ID（＝地区大会の出場枠判定の材料）。
        /// null＝まだ決勝が消化されていない。<see cref="RegionalEntry"/> だけが読む中継値
        /// （<see cref="LastPrefChampionId"/> と同じ理由でセーブに載る・v19）。
        /// </summary>
        public IReadOnlyList<int> LastAutumnPlacementIds { get; private set; }

        /// <summary>
        /// 自校が出場しなかった秋季県大会（地区予選の進出圏外・issue #688）を同期解決した順位を控える。
        /// 出場した年は <see cref="FoldTournamentRecords"/> が同じ値を畳み込みで入れる（地区大会の判定の材料）。
        /// </summary>
        public void SetSpectatedAutumnPlacement(IReadOnlyList<int> placementIds)
            => LastAutumnPlacementIds = placementIds != null ? new List<int>(placementIds) : null;

        /// <summary>
        /// 直近に終わった地区大会の優勝校ID（＝明治神宮大会の自地区代表）。null＝地区大会が決勝まで
        /// 消化されていない（＝自校が地区大会に出場しなかった年。その場合は
        /// <see cref="RegionalEntry"/> が同期解決した結果を <see cref="JinguEntry"/> へ直接渡す）。
        /// </summary>
        public int? LastRegionalChampionId { get; private set; }

        /// <summary>直近に終わった地区大会の順位付き出場校ID（＝センバツ選考の自地区ぶんの材料）。
        /// 自校が地区大会に出場しなかった年（単独県開催の地区・出場枠外）は null のまま＝
        /// <see cref="MyRegionPlacementIds"/> が <see cref="LastAutumnPlacementIds"/> へ自動で落ちる。</summary>
        public IReadOnlyList<int> LastRegionalPlacementIds { get; private set; }

        /// <summary>自地区の順位付き出場校ID。単独県開催の地区（東京・北海道）は地区大会を開かないため
        /// 秋季県大会の順位をそのまま使う（<see cref="RegionalEntry"/> と同じフォールバック規則）。</summary>
        public IReadOnlyList<int> MyRegionPlacementIds => LastRegionalPlacementIds ?? LastAutumnPlacementIds;

        /// <summary>直近に終わった明治神宮大会の優勝校ID（＝センバツ選考の神宮枠）。null＝自校が
        /// 神宮大会に出場しなかった年（その場合は <see cref="JinguEntry"/> が同期解決した結果を
        /// <see cref="SenbatsuEntry"/> へ直接渡す）。</summary>
        public int? LastJinguChampionId { get; private set; }

        /// <summary>
        /// 観るだけの甲子園が決着した直後に1回だけ立つ優勝校名（UIが通知フィードへ流したら
        /// <see cref="ConsumeKoshienResult"/> で下ろす）。開幕バナー（BannerPending）と同じ「遷移とUI提示の分離」。
        /// </summary>
        public string PendingKoshienChampion { get; private set; }

        /// <summary>次戦までの残り日数（開催中のみ・負値は0）。</summary>
        public int DaysUntilNextMatch =>
            !InTournament || Runner.Finished ? 0 : Math.Max(0, Runner.NextMatchDay - TournamentDay);

        /// <summary>今日が試合日に到達したか（惰性スキップ防止＝到達日以降は真）。</summary>
        public bool ReachedMatchDay =>
            InTournament && !Runner.Finished && TournamentDay >= Runner.NextMatchDay;

        /// <summary>
        /// 大会中の実月日の小書き（"夏の大会 7月13日"）。大会外は空文字（issue #583）。
        /// ホームも側画面のトップバーもここを読む＝書式を画面ごとに作らない単一ソース。
        /// </summary>
        public string DateSuffix =>
            !InTournament ? "" : KindLabel(Kind) + " " + TournamentDateLabel;

        /// <summary>
        /// 大会中の実月日そのもの（"7月13日"。大会名を含まない）。大会外は空文字。
        /// <see cref="GameClock.Week"/> は大会中は進まず凍結する（週暦は大会内実日付を触らない・issue #583）ため、
        /// 大会中の週表示（ScoreboardStrip の週マス）はこちらを使う＝週マスが凍結週と実日付で食い違わないようにする
        /// （大会日程と実際の日程が合っていないように見えるバグの修正）。
        /// </summary>
        public string TournamentDateLabel =>
            !InTournament ? "" : TournamentDate.Label(TournamentEntry.Schedule, Kind, TournamentDay);

        /// <summary>大会種別の短い呼び名（小書き用）。</summary>
        private static string KindLabel(TournamentKind kind)
        {
            if (kind == TournamentKind.SummerKoshien) return "甲子園";
            return (kind == TournamentKind.Summer ? "夏" : "秋") + "の大会";
        }

        /// <summary>大会中の次戦日の実月日（"7月16日"）。大会外・終了済みは空文字（issue #583）。</summary>
        public string NextMatchDateLabel =>
            !InTournament || Runner.Finished
                ? ""
                : TournamentDate.Label(TournamentEntry.Schedule, Kind, Runner.NextMatchDay);

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

        // 大舞台での化け（issue #694・設計書03 §5.5）。公式戦1試合ぶん、好打・好投の出場者に大会の格で決まる確率で起きる。
        private static readonly GrowthEventCoefficients GrowthEventsCoeff = new GrowthEventCoefficients();
        private int _bigStageSeq;
        private readonly List<GrowthNotice> _bigStageNotices = new List<GrowthNotice>();

        /// <summary>
        /// 公式戦の後に大舞台イベントを判定する（練習試合は呼ばない）。乱数は試合ごとの独立ストリーム
        /// （怪我と同じ作法）＝大会の展開・ほかの抽選の乱数列はずれない。
        /// </summary>
        private void ApplyBigStage(GameResult detail, bool managerWasAway)
        {
            var prob = GrowthEventsCoeff.BigStageProb(Kind);
            if (prob <= 0.0) return;
            var rng = new Xoshiro256Random(
                0x8167_0000UL ^ (ulong)(GameClock.YearIndex * 10000 + GameClock.Week * 100 + (++_bigStageSeq)));
            _bigStageNotices.AddRange(GrowthEventModel.BigStage(
                detail, managerWasAway, RosterService.Roster, prob, rng, GrowthEventsCoeff));
        }

        /// <summary>大舞台イベントの結果を取り出して空にする（通知フィード・ノベルへ流す用）。</summary>
        public List<GrowthNotice> DrainBigStageNotices()
        {
            var list = new List<GrowthNotice>(_bigStageNotices);
            _bigStageNotices.Clear();
            return list;
        }

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
            // 相手校側も全国成績へ（issue #634）。自校の通算（Career）が練習試合を含むので、
            // 相手校の通算も同じ意味＝両校のHUDに出る「通算」が同じ範囲を指す。
            if (PracticeOpponent is { } practiceOpponent)
                NationService.TournamentStats.FoldSchool(
                    practiceOpponent.Id, result, schoolIsAway: !managerIsAway);
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
                FoldOpponentIntoNation(detail, LastOutcome);
                ApplyMatchGrowth(detail, LastOutcome.ManagerWasAway);
                ApplyMatchCondition(detail, LastOutcome.ManagerWasAway);
                ApplyMatchInjuries(detail, LastOutcome.ManagerWasAway);
                ApplyBigStage(detail, LastOutcome.ManagerWasAway);
            }
            ResultPending = true;
            ExitTournamentIfFinished();
            KickPrefetch();   // 次ラウンドの自県裏カードを先行して温める（未終了時のみ）。
            return LastOutcome;
        }

        /// <summary>
        /// 自校戦の<b>相手校側</b>のボックススコアを全国成績へ畳み込む（issue #634）。裏試合は
        /// <see cref="TournamentEntry"/> の onBackgroundMatch が畳むが、自校が出る一戦だけはその経路を
        /// 通らないため、相手校の通算からこの試合が抜け落ちる（＝次に同じ校と当たったとき成績が食い違う）。
        /// 自校側は <see cref="Stats"/> が持つので全国側へは積まない（帰属先は1つ）。
        /// </summary>
        private static void FoldOpponentIntoNation(
            KokoSim.Engine.Match.Game.GameResult detail, PlayerMatchOutcome outcome)
        {
            if (outcome.OpponentId == NationService.ManagerSchoolId) return;   // 保険（自校IDは積まない）
            NationService.TournamentStats.FoldSchool(
                outcome.OpponentId, detail, schoolIsAway: !outcome.ManagerWasAway);
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
                FoldOpponentIntoNation(detail, LastOutcome);
                ApplyMatchGrowth(detail, LastOutcome.ManagerWasAway);
                ApplyMatchCondition(detail, LastOutcome.ManagerWasAway);
                ApplyMatchInjuries(detail, LastOutcome.ManagerWasAway);
                ApplyBigStage(detail, LastOutcome.ManagerWasAway);
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
            PendingTournamentWrapUp = new TournamentWrapUp(Title, TournamentDay, LastOutcome.IsChampion, Kind);
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
                FoldTournamentRecords(runner, Kind, Year);
            }
        }

        /// <summary>
        /// 残りブラケットの背景消化（<see cref="BracketFinishSim"/>）が完了した後、通算戦績を確定する
        /// （issue #529。メインスレッドから <see cref="MainThreadDispatcher"/> 経由で呼ばれる）。
        /// </summary>
        /// <summary>
        /// 残りブラケットの畳み込みが保留中なら<b>その場で</b>確定する（<see cref="KoshienEntry"/> 専用）。
        ///
        /// <para><see cref="BracketFinishSim.EnsureCompleted"/> が返っても、完了通知は
        /// <see cref="MainThreadDispatcher"/> のキューに積まれただけで<b>まだ走っていない</b>
        /// （待っているのがそのメインスレッド自身だから）。甲子園は自県の代表＝この畳み込みが記録する
        /// 「夏の県大会の優勝校」を入力に取るので、待った直後に自分で確定させる必要がある。
        /// 走った後はキューの通知が来ても null チェックで無視される＝二重には畳まれない。</para>
        /// </summary>
        public void FlushPendingBracketFold() => CompleteBracketFold();

        private void CompleteBracketFold()
        {
            var runner = _bracketFoldRunner;
            if (runner == null) return;
            // ブラケット全試合（自校戦＋裏試合）を通算戦績へ畳み込む（issue #84）。夏の優勝校は甲子園出場として記録。
            FoldTournamentRecords(runner, _bracketFoldKind, _bracketFoldYear);
            _bracketFoldRunner = null;
        }

        /// <summary>
        /// 通算戦績（issue #84）と歴代大会結果（issue #600）をブラケットから確定する。同期・非同期の
        /// 2経路が同じ処理を書かないための単一の入口。<b>乱数を引かない</b>＝統計帯は不変。
        /// 優勝校は通算戦績の畳み込みが返す値をそのまま渡す（決勝カードを2度走査しない）。
        /// </summary>
        private void FoldTournamentRecords(TournamentRunner runner, TournamentKind kind, int year)
        {
            var matches = runner.BuildBracketView().Matches;
            var champion = Records.FoldTournament(matches, kind, year);
            // 自県の代表＝甲子園の出場校の1つ。ここが「夏の県大会の優勝校」を知る唯一の地点なので控える
            // （Runner は敗退確定と同時に null 化されるので、後から拾い直せない）。
            if (kind == TournamentKind.Summer) LastPrefChampionId = champion?.Id;
            // 秋季地区予選の優勝校＝秋季県大会の自地区代表。自校の県が地区予選を持つ年だけここを通る
            // （持たない県は DistrictEntry を経由せず TournamentEntry.Enter が直接 Autumn を開くため）。
            if (kind == TournamentKind.AutumnDistrict)
            {
                LastDistrictChampionId = champion?.Id;
                // 自地区の県大会進出校＝トーナメント順位の上位N校（issue #688）。N は YAML の advance_per_group。
                var byId = (System.Func<int, School>)(id => id == NationService.ManagerSchoolId
                    ? NationService.ManagerSchool() : NationService.SchoolById(id));
                LastDistrictQualifierIds = KnockoutPlacement.FromMatches(matches, byId)
                    .Take(DistrictPlanProvider.AdvancePerDistrictOf(NationService.PrefectureId))
                    .Select(s => s.Id).ToList();
            }
            // 秋季県大会の順位＝地区大会の出場枠判定の材料。夏と違い優勝校だけでは足りない
            // （県によっては2〜3位も地区大会へ進む）ので、順位付きプレースメントを丸ごと控える。
            if (kind == TournamentKind.Autumn)
            {
                var byId = (System.Func<int, School>)(id => id == NationService.ManagerSchoolId
                    ? NationService.ManagerSchool() : NationService.SchoolById(id));
                LastAutumnPlacementIds = KnockoutPlacement.FromMatches(matches, byId)
                    .Select(s => s.Id).ToList();
            }
            // 地区大会の優勝校＝自地区の明治神宮大会代表。自校が地区大会に出場した年だけここを通る
            // （出場しなかった年は RegionalEntry が同期解決した結果を JinguEntry へ直接渡す）。
            if (kind == TournamentKind.Regional)
            {
                LastRegionalChampionId = champion?.Id;
                var byId = (System.Func<int, School>)(id => id == NationService.ManagerSchoolId
                    ? NationService.ManagerSchool() : NationService.SchoolById(id));
                LastRegionalPlacementIds = KnockoutPlacement.FromMatches(matches, byId)
                    .Select(s => s.Id).ToList();
            }
            // 明治神宮大会の優勝校＝センバツ選考の神宮枠。自校が神宮大会に出場した年だけここを通る。
            if (kind == TournamentKind.Jingu) LastJinguChampionId = champion?.Id;
            // 歴代大会結果は「自校の年度別の1行」なので、自校が1試合も出ていない大会は積まない
            // （積むと甲子園に出ていない年が毎年「不参加」の行で埋まる）。
            // 秋季地区予選（AutumnDistrict）は積まない＝この直後に続く秋季県大会（Autumn）が
            // 同じ季節の「本当の1行」を積む。地区予選ぶんまで積むと同じ秋に2行できてしまう
            // （個人成績は Stats.FoldMatch 側で別途積まれるので、ここで捨てても打率等は失われない）。
            var managerPlayed = false;
            foreach (var m in matches) if (m.ManagerInvolved) { managerPlayed = true; break; }
            if (managerPlayed && kind != TournamentKind.AutumnDistrict)
                History.Fold(matches, kind, year, NationService.ManagerSchoolId, champion);
            // AI校の年次進化の材料（issue wire-ai-school-evolve-into-game）: 自県ブラケットの校別勝数を台帳へ。
            // 自校の勝ちも積まれるが Nation に自校は居ないので進化には効かない（自校の強さは共有ロスター由来）。
            NationService.YearLedger.AddMatches(matches);
            // 甲子園の王者は年度末の進化で「全国制覇」の上乗せを受ける（8月に決まり進化は翌4月＝台帳が持ち回る）。
            // センバツ（3月）も同じ枠を使う＝同年度に夏春とも王者が決まれば後勝ち（既知の簡略化）。
            if (kind is TournamentKind.SummerKoshien or TournamentKind.SenbatsuKoshien && champion is { } c)
                NationService.YearLedger.SetChampion(c.Id);
            // 番狂わせの名声デルタ（issue #170）もここで積む＝通算戦績と同じ「ブラケット確定」の単一入口。
            ManagerService.RecordTournamentUpsets(matches);
        }

        // ===== 観るだけの甲子園（自校が出場していない年） =====

        /// <summary>
        /// 観るだけの甲子園を開く（<see cref="KoshienEntry"/> から呼ぶ）。対話モードには入らない。
        /// </summary>
        public void EnterSpectatedKoshien(string title, TournamentRunner runner, IReadOnlyList<School> field)
        {
            KoshienTitle = title;
            KoshienRunner = runner;
            KoshienField = field ?? Array.Empty<School>();
            KoshienDay = 0;
            _koshienTotalRounds = runner.RemainingBracketRounds;   // 生成直後＝まだ1ラウンドも消化していない
            PendingKoshienChampion = null;
        }

        /// <summary>セーブから観るだけの甲子園へ復帰する（<see cref="KoshienEntry.RestoreSpectated"/> から）。</summary>
        public void RestoreSpectatedKoshien(TournamentSaveState t, TournamentRunner runner, IReadOnlyList<School> field)
        {
            KoshienTitle = t.Title;
            KoshienRunner = runner;
            KoshienField = field ?? Array.Empty<School>();
            KoshienDay = t.TournamentDay;
            // 総ラウンド数は「今の残り＋消化済み」では出せないので出場校数から数え直す（抽選の並びに依存しない）。
            _koshienTotalRounds = RoundsFor(KoshienField.Count);
        }

        /// <summary>出場校数から総ラウンド数（＝ブラケットの段数）を数える。</summary>
        private static int RoundsFor(int entrants)
        {
            var rounds = 0;
            var size = 1;
            while (size < Math.Max(1, entrants)) { size <<= 1; rounds++; }
            return Math.Max(1, rounds);
        }

        /// <summary>
        /// 観るだけの甲子園を <paramref name="days"/> 日ぶん進める（週送りから1回・7日）。
        /// 日程表（<see cref="TournamentSchedule"/>）で「そのラウンドの最終カードの試合日」を過ぎたラウンドだけ
        /// 消化する＝実際の日程どおりに勝ち上がりが埋まっていく。決着したら通算戦績へ畳んで優勝校を控える。
        /// </summary>
        public void AdvanceSpectatedKoshien(int days)
        {
            var runner = KoshienRunner;
            if (runner == null) return;
            KoshienDay += Math.Max(0, days);

            while (runner.BracketResolutionPending)
            {
                var roundIndex = _koshienTotalRounds - runner.RemainingBracketRounds;
                var cards = Math.Max(1, 1 << Math.Max(0, _koshienTotalRounds - roundIndex - 1));
                var lastCardDay = TournamentEntry.Schedule.MatchDay(_koshienTotalRounds, roundIndex, cards - 1, cards);
                if (KoshienDay < lastCardDay) return;   // まだそのラウンドの日に届いていない
                runner.StepBracketResolution();
            }

            // 決着（優勝校が確定）。通算戦績・最高成績・年次進化の材料をここで一度だけ確定する。
            FoldTournamentRecords(runner, TournamentKind.SummerKoshien, Year);
            PendingKoshienChampion = runner.ChampionName;
            KoshienRunner = null;
            KoshienField = Array.Empty<School>();
            KoshienTitle = "";
            KoshienDay = 0;
        }

        /// <summary>観るだけの甲子園の優勝校を取り出して消費する（通知フィードへ流したら下ろす）。</summary>
        public string ConsumeKoshienResult()
        {
            var champion = PendingKoshienChampion;
            PendingKoshienChampion = null;
            return champion;
        }

        /// <summary>
        /// 「大会・全国」タブが描く大会（対話中のものを優先し、無ければ観るだけの甲子園）。
        /// どちらも無ければ null＝空状態。画面がこの1箇所だけを見る＝2種類の大会を画面側で分岐しない。
        /// </summary>
        public TournamentDisplay Display
        {
            get
            {
                if (InTournament) return new TournamentDisplay(Kind, Title, Runner, Field, true);
                if (HasSpectatedKoshien)
                    return new TournamentDisplay(
                        TournamentKind.SummerKoshien, KoshienTitle, KoshienRunner, KoshienField, false);
                return null;
            }
        }

        /// <summary>
        /// 今大会成績を大会別アーカイブへ確定する（issue #77）。TournamentKind→TournamentSlot を写し、
        /// 在籍部員の (当時の学年, 当時の背番号) を sourceId=DevelopingPlayer.Id で引いて渡す。
        /// </summary>
        private void ArchiveTournamentStats()
        {
            var slot = Kind switch
            {
                TournamentKind.AutumnDistrict or TournamentKind.Autumn or TournamentKind.Regional
                    or TournamentKind.Jingu => TournamentSlot.Autumn,
                TournamentKind.SummerKoshien => TournamentSlot.SummerKoshien,
                TournamentKind.SenbatsuKoshien => TournamentSlot.Senbatsu,
                _ => TournamentSlot.SummerPref,
            };
            var info = new Dictionary<int, (int Grade, int UniformNumber)>();
            foreach (var p in RosterService.Roster)
                if (p.Id > 0) info[p.Id] = (p.Grade, p.UniformNumber);
            Stats.ArchiveCurrentTournament(slot, info, Year);
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
            s.TournamentHistory = History;
            s.Tournament = CaptureTournament();
            s.Koshien = CaptureSpectatedKoshien();
            s.Senbatsu = CaptureSpectatedSenbatsu();
            s.PendingSenbatsuFieldIds = new List<int>(PendingSenbatsuFieldIds);
            s.PendingSenbatsuJinguRegion = PendingSenbatsuJinguRegion;
            // 大会連鎖の中継値（v19）。null は「未確定」の意味を持つので、空リストへ潰して保存し
            // Restore 側で null へ戻す（MyRegionPlacementIds の ?? フォールバックが効かなくなるため）。
            s.LastPrefChampionId = LastPrefChampionId;
            s.LastDistrictChampionId = LastDistrictChampionId;
            s.LastDistrictQualifierIds = LastDistrictQualifierIds != null
                ? new List<int>(LastDistrictQualifierIds) : new List<int>();
            s.LastAutumnPlacementIds = LastAutumnPlacementIds != null
                ? new List<int>(LastAutumnPlacementIds) : new List<int>();
            s.LastRegionalChampionId = LastRegionalChampionId;
            s.LastRegionalPlacementIds = LastRegionalPlacementIds != null
                ? new List<int>(LastRegionalPlacementIds) : new List<int>();
            s.LastJinguChampionId = LastJinguChampionId;
            s.LastLineup = LastLineup;
        }

        /// <summary>観るだけのセンバツを DTO へ（<see cref="CaptureSpectatedKoshien"/> と同型）。</summary>
        private TournamentSaveState CaptureSpectatedSenbatsu()
        {
            var runner = SenbatsuRunner;
            if (runner == null || !runner.CanCaptureState) return null;
            var ids = new List<int>(SenbatsuField.Count);
            foreach (var s in SenbatsuField) ids.Add(s.Id);
            return new TournamentSaveState
            {
                Kind = TournamentKind.SenbatsuKoshien,
                Title = SenbatsuTitle,
                TournamentDay = SenbatsuDay,
                BannerPending = false,
                FieldSchoolIds = ids,
                Runner = runner.CaptureState(),
            };
        }

        /// <summary>
        /// 観るだけの甲子園を DTO へ（開催していなければ null）。<see cref="CaptureTournament"/> と同じ器を使う
        /// ＝進行体の保存様式を2つ作らない。<c>BannerPending</c> は使わない（開幕演出を出さないため常に false）。
        /// </summary>
        private TournamentSaveState CaptureSpectatedKoshien()
        {
            var runner = KoshienRunner;
            if (runner == null || !runner.CanCaptureState) return null;
            var ids = new List<int>(KoshienField.Count);
            foreach (var s in KoshienField) ids.Add(s.Id);
            return new TournamentSaveState
            {
                Kind = TournamentKind.SummerKoshien,
                Title = KoshienTitle,
                TournamentDay = KoshienDay,
                BannerPending = false,
                FieldSchoolIds = ids,
                Runner = runner.CaptureState(),
            };
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
            History = s.TournamentHistory;
            ClearTransient();
            LastLineup = s.LastLineup;   // ClearTransient の後（一時状態と違いセーブに載る＝ロードで戻す）
            PendingSenbatsuFieldIds = s.PendingSenbatsuFieldIds != null
                ? new List<int>(s.PendingSenbatsuFieldIds) : new List<int>();
            PendingSenbatsuJinguRegion = s.PendingSenbatsuJinguRegion ?? "";
            // 大会連鎖の中継値（v19）。**空リストは null へ戻す**＝MyRegionPlacementIds の
            // 「地区大会の順位が無ければ県大会の順位を使う」フォールバックは ?? で書かれており、
            // 空リストのまま据えると「順位0件の地区大会があった」ことになって連鎖が切れる。
            LastPrefChampionId = s.LastPrefChampionId;
            LastDistrictChampionId = s.LastDistrictChampionId;
            LastDistrictQualifierIds = s.LastDistrictQualifierIds != null && s.LastDistrictQualifierIds.Count > 0
                ? new List<int>(s.LastDistrictQualifierIds) : null;
            LastAutumnPlacementIds = s.LastAutumnPlacementIds != null && s.LastAutumnPlacementIds.Count > 0
                ? new List<int>(s.LastAutumnPlacementIds) : null;
            LastRegionalChampionId = s.LastRegionalChampionId;
            LastRegionalPlacementIds = s.LastRegionalPlacementIds != null && s.LastRegionalPlacementIds.Count > 0
                ? new List<int>(s.LastRegionalPlacementIds) : null;
            LastJinguChampionId = s.LastJinguChampionId;
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
            History = new TournamentHistory();
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
            KoshienRunner = null;
            KoshienField = Array.Empty<School>();
            KoshienTitle = "";
            KoshienDay = 0;
            _koshienTotalRounds = 0;
            PendingKoshienChampion = null;
            SenbatsuRunner = null;
            SenbatsuField = Array.Empty<School>();
            SenbatsuTitle = "";
            SenbatsuDay = 0;
            _senbatsuTotalRounds = 0;
            PendingSenbatsuChampion = null;
            PendingSenbatsuFieldIds = new List<int>();
            PendingSenbatsuJinguRegion = "";
            LastPrefChampionId = null;
            LastDistrictChampionId = null;
            LastDistrictQualifierIds = null;
            LastAutumnPlacementIds = null;
            LastRegionalChampionId = null;
            LastRegionalPlacementIds = null;
            LastJinguChampionId = null;
            Lineup = null;
            LastLineup = null;   // 新規ゲームは白紙。ロードは Restore がこの後で書き戻す
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
