using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会内の日程（設計書05 §1.1・issue #583）。実際のトーナメント日程の形を持つ日程表。
///
/// <para><b>ラウンド番号ではなく残りラウンド数（roundsRemaining）で引く</b>。ラウンド数は県の出場校数で
/// 5〜9と変わるのに対し、「決勝・準決勝・準々決勝」は決勝から数えた位置で決まるため。
/// 1=決勝 / 2=準決勝 / 3=準々決勝 / 4以上=それ以前。</para>
///
/// <para>形は3段: <b>序盤は数日に分散</b>（1回戦の全カードを1日では消化できない＝自校の初戦日が
/// ブラケット位置で変わる）→<b>中盤は2日</b>→<b>準々決勝以降は同日開催</b>、そして
/// <b>準々決勝の後と準決勝の後に休養日</b>。既定（A案）で8ラウンド県は全18日・5ラウンド県は9日。</para>
///
/// <para><b>帯・決定論に効く</b>: 試合日は <see cref="Roster.AceRestSelector"/> の温存抽選シードに入り、
/// 球数台帳（<see cref="TournamentPitchLedger"/>）の休養日ウィンドウの意味も決める。
/// ここを触ると裏試合のエース選択が動く（OPEN-QUESTIONS Q23 →本 issue が置き換え）。</para>
/// </summary>
public sealed record TournamentSchedule
{
    /// <summary>初戦の試合日（開幕=0日目からの相対）。</summary>
    public int FirstRoundDay { get; init; } = 1;

    /// <summary>序盤ラウンド（残り <see cref="EarlyRoundsThreshold"/> 以上）のカードを分散させる日数。</summary>
    public int EarlyRoundSpreadDays { get; init; } = 3;
    /// <summary>中盤ラウンドのカードを分散させる日数。</summary>
    public int MiddleRoundSpreadDays { get; init; } = 2;
    /// <summary>終盤ラウンド（残り <see cref="LateRoundsThreshold"/> 以下＝準々決勝以降）は同日開催＝1。</summary>
    public int LateRoundSpreadDays { get; init; } = 1;

    /// <summary>この残りラウンド数以上を序盤とする（既定6＝8ラウンド県なら1〜3回戦）。</summary>
    public int EarlyRoundsThreshold { get; init; } = 6;
    /// <summary>この残りラウンド数以下を終盤とする（既定3＝準々決勝・準決勝・決勝）。</summary>
    public int LateRoundsThreshold { get; init; } = 3;

    /// <summary>準々決勝（残り3）の後に置く休養日。</summary>
    public int RestDaysAfterQuarterfinal { get; init; } = 1;
    /// <summary>準決勝（残り2）の後に置く休養日。</summary>
    public int RestDaysAfterSemifinal { get; init; } = 1;

    /// <summary>夏の地方大会の実開幕月（表示専用・issue #583）。</summary>
    public int SummerOpenMonth { get; init; } = 7;
    /// <summary>夏の地方大会の実開幕日。</summary>
    public int SummerOpenDay { get; init; } = 10;
    /// <summary>秋季県大会の実開幕月。</summary>
    public int AutumnOpenMonth { get; init; } = 9;
    /// <summary>秋季県大会の実開幕日。</summary>
    public int AutumnOpenDay { get; init; } = 12;

    /// <summary>夏の甲子園の実開幕月（設計書05 §1.1「夏の甲子園（8月）」）。</summary>
    public int KoshienOpenMonth { get; init; } = 8;
    /// <summary>夏の甲子園の実開幕日。</summary>
    public int KoshienOpenDay { get; init; } = 6;

    /// <summary>秋季地区大会の実開幕月（表示専用。前段の秋季県大会の後始末から即座連鎖するため
    /// 週アンカーとは無関係、設計書05 §1.5b）。</summary>
    public int RegionalOpenMonth { get; init; } = 10;
    /// <summary>秋季地区大会の実開幕日。</summary>
    public int RegionalOpenDay { get; init; } = 18;
    /// <summary>明治神宮大会の実開幕月（表示専用）。</summary>
    public int JinguOpenMonth { get; init; } = 11;
    /// <summary>明治神宮大会の実開幕日。</summary>
    public int JinguOpenDay { get; init; } = 15;
    /// <summary>春のセンバツ本戦の実開幕月（表示専用。選考発表週から即座連鎖するため
    /// 週アンカーとは無関係、設計書05 §1.5b）。</summary>
    public int SenbatsuOpenMonth { get; init; } = 3;
    /// <summary>春のセンバツ本戦の実開幕日。</summary>
    public int SenbatsuOpenDay { get; init; } = 19;

    /// <summary>残りラウンド数 <paramref name="roundsRemaining"/> のカードを分散させる日数。</summary>
    public int SpreadDaysFor(int roundsRemaining)
    {
        if (roundsRemaining <= LateRoundsThreshold) return Math.Max(1, LateRoundSpreadDays);
        if (roundsRemaining >= EarlyRoundsThreshold) return Math.Max(1, EarlyRoundSpreadDays);
        return Math.Max(1, MiddleRoundSpreadDays);
    }

    /// <summary>そのラウンドを消化した後に置く休養日（準々決勝後・準決勝後だけ）。</summary>
    public int RestDaysAfter(int roundsRemaining) => roundsRemaining switch
    {
        3 => Math.Max(0, RestDaysAfterQuarterfinal),
        2 => Math.Max(0, RestDaysAfterSemifinal),
        _ => 0,
    };

    /// <summary>ラウンド index（0基点＝初戦）の初日。以降のカードはここから分散する。</summary>
    public int RoundStartDay(int totalRounds, int roundIndex)
    {
        var day = FirstRoundDay;
        for (var r = 0; r < roundIndex; r++)
        {
            var rr = totalRounds - r;
            day += SpreadDaysFor(rr) + RestDaysAfter(rr);
        }
        return day;
    }

    /// <summary>
    /// ラウンド index <paramref name="roundIndex"/>・カード <paramref name="slot"/> の試合日。
    /// カードは <see cref="SpreadDaysFor"/> 日に均等割りする（slot が小さいほど早い日＝ブラケットの上から順に消化）。
    /// </summary>
    public int MatchDay(int totalRounds, int roundIndex, int slot, int cardsInRound)
    {
        var rr = Math.Max(1, totalRounds - roundIndex);
        var spread = SpreadDaysFor(rr);
        var cards = Math.Max(1, cardsInRound);
        var offset = (int)((long)Math.Max(0, slot) * spread / cards);
        if (offset >= spread) offset = spread - 1;
        return RoundStartDay(totalRounds, roundIndex) + offset;
    }

    /// <summary>大会の最終日（＝決勝の日）。消費する週数の算出に使う。</summary>
    public int FinalDay(int totalRounds) => RoundStartDay(totalRounds, Math.Max(0, totalRounds - 1));
}

/// <summary>
/// 自校の一戦を自動消化した結果（UIの結果表示に使う）。
/// Detail は詳細シム（IPlayerMatchResolver）で解決したときのみ非null＝成績集計の源。抽象シム時は null。
/// </summary>
public sealed record PlayerMatchOutcome(
    bool ManagerWon,
    string OpponentName,
    Tier OpponentTier,
    int ManagerScore,
    int OpponentScore,
    string RoundName,
    bool IsChampion,
    GameResult? Detail = null,
    bool ManagerWasAway = false,
    bool MercyEnded = false,
    // 引き分け再試合（設計書05 §1.3）で行われた再試合数。0＝初戦で決着。>0 なら Detail は決着した再試合の結果。
    int TieReplays = 0,
    // 相手校の学校ID（issue #634）。<see cref="Detail"/> の相手校側ボックススコアを全国成績
    // （NationTournamentStats）へ畳み込む帰属先。校名だけでは学校を同定できないため持つ。
    int OpponentId = -1);

/// <summary>
/// ライブ観戦する自校戦の受け渡し（<see cref="TournamentRunner.BeginNextPlayerMatch"/> の戻り）。
/// UI は Progression を打席単位で進め、終局の GameResult を <see cref="TournamentRunner.CompleteNextPlayerMatch"/>
/// へ戻して大会を継続する。OpponentName/RoundName は観戦画面のスコアボード表示用。
/// OpponentId は <see cref="BracketMatch"/> の WinnerId/LoserId と同じ理由で持つ＝表示名だけでは学校を
/// 同定できず、チームカラー（設計書18 §3.5.1）のように School.Id で引く表示情報が作れないため。
/// </summary>
public sealed record LivePlayerMatch(
    Match.Timeline.Playback.MatchProgression Progression,
    bool ManagerIsAway,
    string OpponentName,
    string RoundName,
    int OpponentId);

/// <summary>
/// 大会概要の1試合行。ManagerInvolved で自校ハイライトを表現する。
/// MercyEnded はコールドゲーム（マーシールール）成立で打ち切られたか（設計書05 §1.3, Q18）。裏試合合成スコアは常にfalse。
/// WinnerId/LoserId は通算戦績（<see cref="SchoolRecordBook"/>, issue #84）の集計キー。表示名だけでは
/// 学校を同定できないため、大会終了時にブラケットから戦績を再集計できるよう School.Id を保持する。
/// </summary>
public sealed record BracketMatch(
    string RoundName,
    int RoundsRemaining,
    string WinnerName,
    string LoserName,
    int WinnerId,
    int LoserId,
    int WinnerScore,
    int LoserScore,
    bool ManagerInvolved,
    bool MercyEnded = false);

/// <summary>
/// 樹形図の1スロット（カードの上側/下側）。まだ校名が確定していない枠は TeamName=null（「Aブロック勝者」待ち）。
/// Score は当該カードが消化済みのときだけ非null。
/// </summary>
public sealed record BracketSlot(string? TeamName, bool IsManager, bool IsWinner, int? Score)
{
    /// <summary>校名が確定しているか（未消化ラウンドの空枠は false）。</summary>
    public bool IsDetermined => TeamName is not null;
}

/// <summary>
/// 樹形図の1カード（対戦枠）。Round は0基点（0＝初戦）、SlotIndex はそのラウンド内のカード位置。
/// 勝者は次ラウンドの SlotIndex/2 のカードに現れる（<see cref="BracketSlot"/> の上下は SlotIndex%2）。
/// </summary>
public sealed record BracketCard(
    int Round,
    int SlotIndex,
    string RoundName,
    BracketSlot Top,
    BracketSlot Bottom,
    bool IsBye,
    bool IsPlayed,
    bool MercyEnded = false);

/// <summary>樹形図の1ラウンド（カード列）。Round は0基点。</summary>
public sealed record BracketRound(int Round, string RoundName, IReadOnlyList<BracketCard> Cards);

/// <summary>大会概要（ブラケット状態＋自校の状況）。UIの「大会・全国」タブが描画する。</summary>
public sealed record TournamentBracketView(
    string Title,
    IReadOnlyList<BracketMatch> Matches,
    string? ChampionName,
    bool ManagerIsChampion,
    bool ManagerEliminated,
    IReadOnlyList<BracketRound> Rounds);

/// <summary>
/// 自校の大会進行体（大会モードの中核）。シード付きシングルエリミネーション（設計書05 §1.2）を、
/// 自校の一戦だけで停止できるようステートフルに刻む。自校以外の裏試合は AggregateMatch で即時決着し
/// （設計書05 §1.4 第3層＝自動消化）、大会概要に記録する。乱数は注入で決定論（不変条件#2）。
/// </summary>
public sealed class TournamentRunner
{
    private readonly NationCoefficients _coeff;
    private readonly IRandomSource _rng;
    private readonly TournamentSchedule _schedule;
    private readonly IPlayerMatchResolver? _playerResolver;
    private readonly IBackgroundMatchResolver? _backgroundResolver;
    private readonly int _managerId;
    private readonly int _totalRounds;
    /// <summary>
    /// 裏カードを<b>ランナーが記録した瞬間</b>に呼ぶフック（(先攻ID, 後攻ID, 結果)）。全国成績への畳み込みに使う。
    ///
    /// <para>畳み込みを <see cref="IBackgroundMatchResolver"/> の中（＝<b>計算時</b>）でやってはいけない。
    /// プレフェッチ（<see cref="PrefetchCurrentRoundBackgroundCards"/>）は同じ resolver で先に計算するので、
    /// 計算時に畳むと「まだランナーが記録していないカード」が成績へ入る。セッション内はメモ化が
    /// 「1カード1回」を守るので露見しないが、<b>大会中セーブ（issue #565）を挟むとメモが消えて二重計上になる</b>
    /// （実測 2026-08-10: ロード後の DTO が保存時より大きくなり、自県の裏カードが2回積まれていた）。
    /// ここ＝記録時に畳めば、成績とランナーの状態が常に一致する。</para>
    /// </summary>
    private readonly Action<int, int, GameResult>? _onBackgroundMatch;
    private readonly List<BracketMatch> _matches = new();
    /// <summary>大会中の投手球数台帳（issue #41）。エース温存判断（issue #42）の消耗入力として本大会内で共有する。</summary>
    private readonly TournamentPitchLedger _pitchLedger = new();

    /// <summary>
    /// 引き分け再試合（設計書05 §1.3, issue #253）の上限回数。延長上限（15回, <see cref="GameContext.MaxInnings"/>）
    /// で同点だった試合は別Fork の再試合で決着させるが、この回数を再試合しても決着しなければ規定
    /// （安打数→強さ→Id, <see cref="DecideByReplay"/>）で強制決着し有限終了を保証する（安全弁）。
    /// 4試合連続で15回引き分けになる確率は無視できるため、通常は 0〜1 回の再試合で決着する。
    /// </summary>
    private const int MaxTieReplays = 3;

    /// <summary>ラウンド r（0基点）開始時の各スロットの残存校。未到達ラウンドは null（＝校名未確定）。</summary>
    private readonly School?[]?[] _roundEntrants;
    /// <summary>消化済みカードの結果（ラウンド×スロット）。樹形図のスコア併記に使う。</summary>
    private readonly Dictionary<(int Round, int Slot), CardResult> _cardResults = new();

    /// <summary>樹形図用の1カード結果。TopWon は上側スロット（添字 2i）が勝ったか。</summary>
    private sealed record CardResult(bool TopWon, int WinnerScore, int LoserScore, bool MercyEnded = false);

    private School?[] _current;
    private int _roundIndex;          // 消化済みラウンド数（不戦勝も1ラウンドとして進む＝日程が進む）
    private School? _nextOpponent;
    private PendingLiveMatch? _pending;   // ライブ観戦中の自校戦（Begin〜Complete の間だけ非null）

    /// <summary>
    /// ライブ観戦中の自校戦の未確定状態。Begin で当該ラウンドを自校カードの直前まで解決して積み、
    /// Complete で終局結果を受けて自校カードと以降のカードを確定する。Next は自校スロット(Slot)と
    /// それ以降が未充填の途中状態（Complete で埋める）。本流RNGの消費順は自動 ResolveRound と同一。
    /// </summary>
    /// <remarks>
    /// Live は Begin が返したのと同一のライブ観戦ハンドル。UI が Complete し損ねた（観戦画面を離脱した等）
    /// ときに <see cref="ResumePendingLiveMatch"/> で同じ進行体へ復帰できるよう保持する。
    /// </remarks>
    private sealed record PendingLiveMatch(
        School?[] Cur, School?[] Next, int Slot, School Mgr, School Opp,
        string RoundName, int RoundsRemaining, bool ManagerIsAway, LivePlayerMatch Live, int MatchDay);

    public string Title { get; }
    public bool PlayerActive { get; private set; } = true;
    public bool IsChampion { get; private set; }
    /// <summary>自校にとって大会が終了したか（優勝 or 敗退）。</summary>
    public bool Finished => IsChampion || !PlayerActive;
    public string? ChampionName { get; private set; }

    private readonly bool _isNationalTournament;

    public TournamentRunner(
        IReadOnlyList<School> entrants,
        School manager,
        NationCoefficients coeff,
        IRandomSource rng,
        TournamentSchedule schedule,
        string title,
        IPlayerMatchResolver? playerResolver = null,
        IBackgroundMatchResolver? backgroundResolver = null,
        bool isNationalTournament = false,
        Action<int, int, GameResult>? onBackgroundMatch = null)
        : this(entrants, ManagerIdIn(entrants, manager), coeff, rng, schedule, title,
               playerResolver, backgroundResolver, isNationalTournament, onBackgroundMatch)
    {
    }

    /// <summary>
    /// どの学校にも一致しない自校ID（<see cref="Spectated"/> 専用）。実在の校IDは 0 以上か自校の -1 なので衝突しない。
    /// </summary>
    private const int NoManagerId = int.MinValue;

    /// <summary>参加校に自校が居ることを確かめて自校IDを返す（居なければ投げる＝取り違えを早期に落とす）。</summary>
    private static int ManagerIdIn(IReadOnlyList<School> entrants, School manager)
    {
        if (entrants.Count == 0) throw new ArgumentException("参加校が空です。");
        if (entrants.All(s => s.Id != manager.Id)) throw new ArgumentException("自校が参加校に含まれていません。");
        return manager.Id;
    }

    /// <summary>
    /// <b>自校が出場していない大会</b>の進行体を組む（設計書05 §1.1a）。自校が県予選で敗退した年の
    /// 夏の甲子園がこれで、プレイヤーは観るだけ＝全カードが裏試合として解決される。
    ///
    /// <para>自校IDをどの学校にも一致しない値にするので、生成直後に <c>PlayerActive=false</c>
    /// （＝<see cref="BracketResolutionPending"/> が true）になる。呼び出し側は
    /// <see cref="StepBracketResolution"/> を日程に合わせて刻めばよい。<b>nominal manager として
    /// 出場校の1つを渡してはいけない</b>＝その校が樹形図で「自校」として強調されてしまう。</para>
    /// </summary>
    public static TournamentRunner Spectated(
        IReadOnlyList<School> entrants,
        NationCoefficients coeff,
        IRandomSource rng,
        TournamentSchedule schedule,
        string title,
        IBackgroundMatchResolver? backgroundResolver = null,
        bool isNationalTournament = false,
        Action<int, int, GameResult>? onBackgroundMatch = null)
    {
        if (entrants.Count == 0) throw new ArgumentException("参加校が空です。");
        return new TournamentRunner(entrants, NoManagerId, coeff, rng, schedule, title,
            playerResolver: null, backgroundResolver, isNationalTournament, onBackgroundMatch);
    }

    private TournamentRunner(
        IReadOnlyList<School> entrants,
        int managerId,
        NationCoefficients coeff,
        IRandomSource rng,
        TournamentSchedule schedule,
        string title,
        IPlayerMatchResolver? playerResolver,
        IBackgroundMatchResolver? backgroundResolver,
        bool isNationalTournament,
        Action<int, int, GameResult>? onBackgroundMatch)
    {
        _coeff = coeff;
        _rng = rng;
        _schedule = schedule;
        _playerResolver = playerResolver;
        _backgroundResolver = backgroundResolver;
        _managerId = managerId;
        _isNationalTournament = isNationalTournament;
        _onBackgroundMatch = onBackgroundMatch;
        Title = title;

        // 組み合わせ抽選（issue #631）: 上位K校をシードとして対角へ、残りは抽選で空きへ。不足分は不戦勝(null)。
        // 抽選は Fork した専用ストリーム＝本編の乱数列を進めない（試合結果・統計帯は動かない）。
        // rng は大会ごとに（母種＾年＾種別で）違うので、同じ年でも夏と秋で別の表になる。
        _current = TournamentDraw.Arrange(entrants, rng.Fork(TournamentDraw.DrawStreamId));
        _totalRounds = TournamentRecorder.RoundsRemaining(_current.Length);

        _roundEntrants = new School?[]?[_totalRounds + 1];
        _roundEntrants[0] = (School?[])_current.Clone();   // 初期シード配置は確定済み＝樹形図の起点。

        AdvanceUntilPlayerMatch();
    }

    // ===== セーブ往復（issue #565） =====

    /// <summary>
    /// セーブから進行体を組み直す（issue #565）。<b>係数・日程・resolver・自校は呼び出し側が渡し直す</b>
    /// ＝保存するのは「進行して変化したもの」だけ（<see cref="TournamentRunnerState"/> の注記）。
    ///
    /// <para><paramref name="field"/> は Id→School の解決表としてだけ使う（順序は問わない）。
    /// シード配置は組み直さず保存された並びを使う＝自校の Strength が大会中の実戦成長で動いても
    /// 樹形図がずれない。<paramref name="manager"/> は <paramref name="field"/> に含めても含めなくてもよい
    /// （どちらでも自校 Id で引けるよう先に登録する）。</para>
    /// </summary>
    /// <exception cref="ArgumentException">保存された Id が <paramref name="field"/> に見つからないとき。</exception>
    public static TournamentRunner FromSaved(
        TournamentRunnerState state,
        IReadOnlyList<School> field,
        School manager,
        NationCoefficients coeff,
        TournamentSchedule schedule,
        IPlayerMatchResolver? playerResolver = null,
        IBackgroundMatchResolver? backgroundResolver = null,
        Action<int, int, GameResult>? onBackgroundMatch = null)
        => new(state, field, manager, coeff, schedule, playerResolver, backgroundResolver, onBackgroundMatch);

    private TournamentRunner(
        TournamentRunnerState state,
        IReadOnlyList<School> field,
        School manager,
        NationCoefficients coeff,
        TournamentSchedule schedule,
        IPlayerMatchResolver? playerResolver,
        IBackgroundMatchResolver? backgroundResolver,
        Action<int, int, GameResult>? onBackgroundMatch)
    {
        _coeff = coeff;
        _rng = Xoshiro256Random.FromState(state.RngState);
        _schedule = schedule;
        _playerResolver = playerResolver;
        _backgroundResolver = backgroundResolver;
        _managerId = manager.Id;
        _isNationalTournament = state.IsNationalTournament;
        _onBackgroundMatch = onBackgroundMatch;
        Title = state.Title;

        var byId = new Dictionary<int, School> { [manager.Id] = manager };
        foreach (var s in field) byId[s.Id] = s;

        School?[] Resolve(List<int?> ids)
        {
            var arr = new School?[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                if (ids[i] is not { } id) continue;              // 不戦勝の空き枠
                if (!byId.TryGetValue(id, out var school))
                    throw new ArgumentException($"セーブの学校 Id={id} が出場校に見つかりません。");
                arr[i] = school;
            }
            return arr;
        }

        if (state.RoundEntrants.Count == 0 || state.RoundEntrants[0] is null)
            throw new ArgumentException("セーブに初期シード配置がありません。");
        _totalRounds = TournamentRecorder.RoundsRemaining(state.RoundEntrants[0]!.Count);
        _roundEntrants = new School?[]?[_totalRounds + 1];
        for (var r = 0; r < state.RoundEntrants.Count && r < _roundEntrants.Length; r++)
            if (state.RoundEntrants[r] is { } row) _roundEntrants[r] = Resolve(row);

        _current = Resolve(state.Current);
        // 現在ラウンドの行は _current と同じ配列にしておく（保存前の実体と同じ関係にする）。
        var curRound = RoundIndexOf(_current);
        if (curRound >= 0 && curRound < _roundEntrants.Length) _roundEntrants[curRound] = _current;

        _roundIndex = state.RoundIndex;
        PlayerActive = state.PlayerActive;
        IsChampion = state.IsChampion;
        ChampionName = state.ChampionName;

        foreach (var c in state.Cards)
            _cardResults[(c.Round, c.Slot)] = new CardResult(c.TopWon, c.WinnerScore, c.LoserScore, c.MercyEnded);
        _matches.AddRange(state.Matches);
        _pitchLedger.RestoreState(state.PitchLedger);

        // 次戦相手は状態から導く（保存しない＝保存値と実体が食い違う余地を作らない）。
        // AdvanceUntilPlayerMatch は呼ばない: 呼ぶと不戦勝ラウンドを「もう一度」解決して乱数を余計に
        // 消費する（保存時点で消化済みのはず）。
        _nextOpponent = DeriveNextOpponent();
    }

    /// <summary>
    /// 現在の残存校から次戦相手を導く。<see cref="AdvanceUntilPlayerMatch"/> が返る時点の
    /// <see cref="_nextOpponent"/> と同じ値になる（自校が居て相方が居れば相方、それ以外は null）。
    /// </summary>
    private School? DeriveNextOpponent()
    {
        if (!PlayerActive || IsChampion || _current.Length < 2) return null;
        var idx = IndexOfManager(_current);
        return idx < 0 ? null : _current[idx ^ 1];
    }

    /// <summary>
    /// 状態を保存できるか。観戦中の自校戦（<see cref="HasPendingLiveMatch"/>）がある間は false
    /// ＝試合の途中で中断して保存するのは別段階（issue #565 F2）。
    /// </summary>
    public bool CanCaptureState => _pending is null;

    /// <summary>
    /// 進行体の可変状態をセーブ DTO へ写す（issue #565）。
    /// <see cref="CanCaptureState"/> が false のときは投げる（呼び出し側が先に抑止する契約）。
    /// </summary>
    public TournamentRunnerState CaptureState()
    {
        if (!CanCaptureState)
            throw new InvalidOperationException("観戦中の自校戦がある間は保存できません（issue #565 F2）。");

        static List<int?> Ids(School?[] arr)
        {
            var list = new List<int?>(arr.Length);
            foreach (var s in arr) list.Add(s?.Id);
            return list;
        }

        var state = new TournamentRunnerState
        {
            Title = Title,
            IsNationalTournament = _isNationalTournament,
            PlayerActive = PlayerActive,
            IsChampion = IsChampion,
            ChampionName = ChampionName,
            RoundIndex = _roundIndex,
            RngState = ((Xoshiro256Random)_rng).CaptureState(),
            Current = Ids(_current),
            Matches = new List<BracketMatch>(_matches),
            PitchLedger = _pitchLedger.CaptureState(),
        };

        foreach (var row in _roundEntrants) state.RoundEntrants.Add(row is null ? null : Ids(row));

        // 列挙順は Dictionary 任せにせず (ラウンド, スロット) 昇順で固定する（往復のバイト一致・差分の読みやすさ）。
        foreach (var kv in _cardResults.OrderBy(k => k.Key.Round).ThenBy(k => k.Key.Slot))
            state.Cards.Add(new SavedBracketCard(
                kv.Key.Round, kv.Key.Slot, kv.Value.TopWon, kv.Value.WinnerScore, kv.Value.LoserScore, kv.Value.MercyEnded));

        return state;
    }

    /// <summary>自校の次戦相手（不戦勝続き/敗退済み/優勝は null）。</summary>
    public School? NextOpponent => _nextOpponent;
    /// <summary>次戦相手のランク（NextOpponent が null のときは意味を持たない）。</summary>
    public Tier NextOpponentTier => _nextOpponent?.Tier ?? Tier.G;
    /// <summary>次戦の試合日（大会開幕からの相対日）。</summary>
    public int NextMatchDay =>
        _schedule.MatchDay(_totalRounds, _roundIndex, ManagerCardSlot(_current), Math.Max(1, _current.Length / 2));
    /// <summary>次戦のラウンド名（決勝/準決勝/…/N回戦）。</summary>
    public string RoundName => RoundNameFor(TournamentRecorder.RoundsRemaining(_current.Length));

    /// <summary>自校の次戦を自動消化する。勝てば次戦へ進み、負ければ大会終了（残りは背景消化）。</summary>
    public PlayerMatchOutcome PlayNextPlayerMatch()
    {
        if (Finished) throw new InvalidOperationException("大会は自校にとって終了しています。");
        if (_nextOpponent is null) throw new InvalidOperationException("次戦相手が未確定です。");

        var roundsRemaining = TournamentRecorder.RoundsRemaining(_current.Length);
        var roundName = RoundNameFor(roundsRemaining);

        _current = ResolveRound(_current, roundName, roundsRemaining, out var outcome);
        _roundIndex++;

        if (outcome is null) throw new InvalidOperationException("自校の試合が解決されませんでした。");

        if (!outcome.ManagerWon)
        {
            PlayerActive = false;
            _nextOpponent = null;
            // 残りブラケットはここでは消化しない（issue #529）。呼び出し側が BracketResolutionPending を見て
            // StepBracketResolution をスライス駆動で回す（優勝校の確定は消化完了後）。
            // 例外: 決勝で負けた場合は ResolveRound で既に _current.Length==1（残り試合なし）＝
            // BracketResolutionPending は最初から false になるため、ここで確定させないと ChampionName が
            // 永久に確定しない。
            if (_current.Length == 1) ChampionName = _current[0]?.Name;
            return outcome;
        }

        if (_current.Length == 1)          // 決勝を勝った＝優勝。
        {
            IsChampion = true;
            ChampionName = _current[0]!.Name;
            _nextOpponent = null;
            return outcome with { IsChampion = true };
        }

        AdvanceUntilPlayerMatch();
        return outcome;
    }

    /// <summary>
    /// 自校の次戦を「ライブ観戦（打席単位）」で始める。当該ラウンドを自校カードの直前まで本流で解決し、
    /// 自校カードは詳細を隔離Fork のライブ進行体に委ねて一時停止する（この時点では勝敗未確定）。
    /// UI が進行体を進めて終局したら <see cref="CompleteNextPlayerMatch"/> に結果を戻すこと。
    /// 本流RNGの消費は <see cref="PlayNextPlayerMatch"/> とバイト一致（＝観戦してもしなくても同じ大会展開）。
    ///
    /// 未完了のライブ自校戦が残っている（前回 Complete まで到達せず画面を離れた）ときは、新たに Begin せず
    /// その進行体をそのまま返す＝再開する。Begin だけが積み上がって大会が進めなくなる状態を作らないため。
    /// </summary>
    public LivePlayerMatch BeginNextPlayerMatch()
    {
        if (_pending is not null) return _pending.Live;   // 取り残されたライブ戦へ復帰（例外で詰ませない）。
        if (Finished) throw new InvalidOperationException("大会は自校にとって終了しています。");
        if (_nextOpponent is null) throw new InvalidOperationException("次戦相手が未確定です。");
        if (_playerResolver is null) throw new InvalidOperationException("ライブ観戦には IPlayerMatchResolver が必要です。");

        var roundsRemaining = TournamentRecorder.RoundsRemaining(_current.Length);
        var roundName = RoundNameFor(roundsRemaining);
        return BeginRound(_current, roundName, roundsRemaining);
    }

    /// <summary>
    /// ライブ観戦した自校戦の終局結果を受けて、自校カードと当該ラウンドの残りカードを確定し、大会を進める。
    /// 勝てば次戦へ（不戦勝は消化）、負ければ大会終了（残りは背景消化）。<see cref="PlayNextPlayerMatch"/> の後処理と同一。
    /// </summary>
    public PlayerMatchOutcome CompleteNextPlayerMatch(GameResult result)
    {
        if (_pending is null) throw new InvalidOperationException("進行中のライブ自校戦がありません。");

        var outcome = CompleteRound(result);
        _roundIndex++;

        if (!outcome.ManagerWon)
        {
            PlayerActive = false;
            _nextOpponent = null;
            // 残りブラケットはここでは消化しない（issue #529、上の PlayNextPlayerMatch と同じ理由）。
            // 決勝で負けた場合は _current.Length==1 が既に確定しているのでここで ChampionName を確定する
            // （PlayNextPlayerMatch と同じ例外）。
            if (_current.Length == 1) ChampionName = _current[0]?.Name;
            return outcome;
        }

        if (_current.Length == 1)
        {
            IsChampion = true;
            ChampionName = _current[0]!.Name;
            _nextOpponent = null;
            return outcome with { IsChampion = true };
        }

        AdvanceUntilPlayerMatch();
        return outcome;
    }

    /// <summary>
    /// ライブ観戦中の自校戦が Complete されないまま残っているか。UI が観戦画面から離脱した場合の検知に使う。
    /// </summary>
    public bool HasPendingLiveMatch => _pending is not null;

    /// <summary>
    /// 取り残されたライブ自校戦へ復帰する（<see cref="BeginNextPlayerMatch"/> が返したのと同一の進行体）。
    /// 本流RNGは一切消費しない＝復帰しても大会展開は変わらない。
    /// </summary>
    public LivePlayerMatch ResumePendingLiveMatch()
        => _pending?.Live ?? throw new InvalidOperationException("進行中のライブ自校戦がありません。");

    public TournamentBracketView BuildBracketView()
        => new(Title, _matches.ToList(), ChampionName, IsChampion, !PlayerActive, BuildRounds());

    /// <summary>
    /// 樹形図（ラウンド×スロット）を組み立てる。未到達ラウンドのカードは空枠（TeamName=null）として返し、
    /// 片側だけ確定している枠は片側だけ埋まる。表示専用＝RNGを消費しない（不変条件#2）。
    /// </summary>
    private IReadOnlyList<BracketRound> BuildRounds()
    {
        var rounds = new List<BracketRound>(_totalRounds);
        for (var r = 0; r < _totalRounds; r++)
        {
            var entrants = _roundEntrants[r];
            var cardCount = (_roundEntrants[0]!.Length >> r) / 2;
            var roundName = RoundNameFor(_totalRounds - r);
            var cards = new List<BracketCard>(cardCount);
            for (var i = 0; i < cardCount; i++)
            {
                var a = entrants?[2 * i];
                var b = entrants?[2 * i + 1];
                _cardResults.TryGetValue((r, i), out var result);
                var topWon = result?.TopWon ?? false;
                cards.Add(new BracketCard(
                    r, i, roundName,
                    SlotOf(a, result is not null && topWon, result is null ? null : (topWon ? result.WinnerScore : result.LoserScore)),
                    SlotOf(b, result is not null && !topWon, result is null ? null : (topWon ? result.LoserScore : result.WinnerScore)),
                    IsBye: entrants is not null && (a is null) != (b is null),
                    IsPlayed: result is not null,
                    MercyEnded: result?.MercyEnded ?? false));
            }
            rounds.Add(new BracketRound(r, roundName, cards));
        }
        return rounds;
    }

    private BracketSlot SlotOf(School? school, bool isWinner, int? score)
        => new(school?.Name, school is not null && school.Id == _managerId, isWinner, score);

    /// <summary>残存校配列のサイズからラウンド添字（0基点）を導く。_roundIndex とは独立＝背景消化でも正しい。</summary>
    private int RoundIndexOf(School?[] cur) => _totalRounds - TournamentRecorder.RoundsRemaining(cur.Length);

    /// <summary>
    /// 残りラウンド数とカード添字から試合日を引く<b>唯一の入口</b>（issue #583）。序盤ラウンドはカードが
    /// 数日に分散するので、同じラウンドでもカードごとに日が違う。プレフェッチ（<see cref="PrefetchCurrentRoundBackgroundCards"/>）と
    /// 本解決が別の日を渡すと、メモ化 resolver が別条件で温めた結果を本解決が拾ってしまうため、必ずここを通す。
    /// </summary>
    private int MatchDayOf(int roundsRemaining, int slot)
        => _schedule.MatchDay(
            _totalRounds, _totalRounds - roundsRemaining, slot, 1 << Math.Max(0, roundsRemaining - 1));

    /// <summary>現ラウンドで自校が入っているカードの添字（＝自校の試合日を決める位置）。居なければ0。</summary>
    private int ManagerCardSlot(School?[] cur)
    {
        for (var i = 0; i < cur.Length; i++)
            if (cur[i] is { } s && s.Id == _managerId) return i / 2;
        return 0;
    }

    private void RecordCard(int round, int slot, bool topWon, int winnerScore, int loserScore, bool mercyEnded = false)
        => _cardResults[(round, slot)] = new CardResult(topWon, winnerScore, loserScore, mercyEnded);

    /// <summary>
    /// 現ラウンドの自校非関与カードを「先に解いてキャッシュへ温める」プレフェッチ（#試合開始前ロード短縮）。
    /// <see cref="ResolveRound"/> の裏カード分岐と同一の fork キー・同一 context で <see cref="_backgroundResolver"/>
    /// を呼ぶだけで、ランナー状態（<see cref="_current"/>/<see cref="_matches"/>/<see cref="_cardResults"/>/
    /// <see cref="_pitchLedger"/> 等）は一切変更しない＝<b>読み取り専用</b>。よって背景スレッドから安全に呼べ、
    /// あとで本流の <see cref="BeginRound"/>/<see cref="ResolveRound"/> が同じカードを解くときはキャッシュ命中で即返る
    /// （<see cref="_backgroundResolver"/> が <see cref="MemoizingBackgroundResolver"/> のとき）。
    ///
    /// <para>呼び出し規約: このラウンドの直前ラウンドまでが本流で解決済み（＝<see cref="_pitchLedger"/> が
    /// このラウンドの試合日以前の登板を確定済み）の状態で呼ぶこと。Shell はプレフェッチ完了を join してから
    /// 本解決へ入り、プレフェッチ（背景）と本解決（前景）が時間的に重ならないようにする＝台帳・rng の同時
    /// アクセスを避ける。自校が敗退/優勝済み（<see cref="Finished"/>）や自校が実対戦を持つ前の不戦勝途中では何もしない。</para>
    /// </summary>
    public void PrefetchCurrentRoundBackgroundCards()
    {
        if (_backgroundResolver is null || Finished) return;
        var cur = _current;
        if (cur.Length < 2) return;
        var roundsRemaining = TournamentRecorder.RoundsRemaining(cur.Length);
        for (var i = 0; i < cur.Length / 2; i++)
        {
            var a = cur[2 * i];
            var b = cur[2 * i + 1];
            if (a is null || b is null) continue;                       // 不戦勝（裏カードなし）
            if (a.Id == _managerId || b.Id == _managerId) continue;     // 自校カードはライブ解決＝プレフェッチしない
            // 試合日はカードごとに違う（序盤は数日に分散・issue #583）。本解決と<b>必ず同じ日</b>で
            // 呼ばないと、メモ化 resolver が別条件で温めた結果を本解決が拾ってしまう。
            var context = new TournamentMatchContext(roundsRemaining, MatchDayOf(roundsRemaining, i), _pitchLedger);
            // ResolveRound の裏カード分岐と同一呼び出し（結果は捨てる＝メモ化 resolver が温める）。
            _backgroundResolver.Resolve(a, b, _rng.Fork(BackgroundStream(roundsRemaining, i)), context);
        }
    }

    // ===== 内部進行 =====

    /// <summary>自校が実対戦相手を得るまでラウンドを進める（不戦勝は解決して次へ）。</summary>
    private void AdvanceUntilPlayerMatch()
    {
        while (true)
        {
            if (_current.Length == 1)      // 1校まで縮んだ＝優勝者確定。
            {
                var champ = _current[0]!;
                ChampionName = champ.Name;
                _nextOpponent = null;
                if (champ.Id == _managerId) IsChampion = true;
                else PlayerActive = false;
                return;
            }

            var idx = IndexOfManager(_current);
            if (idx < 0)                    // 自校が残っていない（背景消化後など）。
            {
                _nextOpponent = null;
                PlayerActive = false;
                return;
            }

            var opponent = _current[idx ^ 1];   // 同一ペア（2i, 2i+1）の相方。
            if (opponent is not null)
            {
                _nextOpponent = opponent;   // 実対戦相手が確定＝UIの入力待ち。
                return;
            }

            // 自校は不戦勝: このラウンドを解決して次ラウンドへ。
            var roundsRemaining = TournamentRecorder.RoundsRemaining(_current.Length);
            _current = ResolveRound(_current, RoundNameFor(roundsRemaining), roundsRemaining, out _);
            _roundIndex++;
        }
    }

    /// <summary>
    /// 自校敗退後、優勝校がまだ未確定（残りブラケットの消化が残っている）か。優勝時は決勝を勝った瞬間に
    /// <c>_current.Length==1</c> で確定済み（残り試合が存在しない）ため常に false。現役中も false。
    /// </summary>
    public bool BracketResolutionPending => !PlayerActive && !IsChampion && _current.Length > 1;

    /// <summary>
    /// 残りブラケットの消化に必要なラウンド数（<see cref="StepBracketResolution"/> を呼ぶべき回数）。
    /// <see cref="BracketResolutionPending"/> が false のときは0（進捗バーの母数、issue #529）。
    /// </summary>
    public int RemainingBracketRounds => BracketResolutionPending ? TournamentRecorder.RoundsRemaining(_current.Length) : 0;

    /// <summary>
    /// 自校敗退後、残りブラケットを1ラウンドぶんだけ消化する（issue #529）。
    /// 旧 <c>FinishBracketInBackground</c> は名前に反して同期の while ループで、一回戦負け（残り試合数が
    /// 最大）のとき呼び出し側を長時間ブロックしていた。呼び出し側（Unity Shell の背景消化ループ）が
    /// 1呼び出し=1ラウンドの粒度でメインスレッドへ制御を返せるよう刻む（Unity Shell の NationBackgroundSim
    /// と同型のスライス駆動、#208）。全ラウンド消化し終えたら ChampionName を確定する。
    /// <see cref="BracketResolutionPending"/> が false のときは何もしない。
    /// </summary>
    public void StepBracketResolution()
    {
        if (!BracketResolutionPending) return;
        var roundsRemaining = TournamentRecorder.RoundsRemaining(_current.Length);
        _current = ResolveRound(_current, RoundNameFor(roundsRemaining), roundsRemaining, out _);
        if (_current.Length == 1) ChampionName = _current[0]?.Name;
    }

    /// <summary>
    /// 残りブラケットを最後まで同期で消化する（テスト・CLI 等、スライス分割が不要な呼び出し元向けの薄いヘルパ）。
    /// <see cref="StepBracketResolution"/> を <see cref="BracketResolutionPending"/> が false になるまで回すだけ。
    /// Unity Shell はこれを使わず、1ラウンドずつ背景消化する（issue #529。旧 FinishBracketInBackground と
    /// 結果は同一だが、呼び出し元がここへ来るまで自校敗退の事実だけを先に確定できる）。
    /// </summary>
    public void DrainBracketResolution()
    {
        while (BracketResolutionPending) StepBracketResolution();
    }

    /// <summary>1ラウンド全カードを解決し、記録する。自校が試合したら outcome に結果を返す。</summary>
    private School?[] ResolveRound(School?[] cur, string roundName, int roundsRemaining, out PlayerMatchOutcome? outcome)
    {
        outcome = null;
        var round = RoundIndexOf(cur);
        var next = new School?[cur.Length / 2];
        for (var i = 0; i < next.Length; i++)
        {
            var a = cur[2 * i];
            var b = cur[2 * i + 1];
            if (a is null) { next[i] = b; continue; }   // 不戦勝（記録しない）。
            if (b is null) { next[i] = a; continue; }

            var involvesManager = a.Id == _managerId || b.Id == _managerId;

            if (involvesManager && _playerResolver is not null)
            {
                // 自校戦は詳細シム（Fork 隔離ストリームのみ使用）。集計モデル経路では、背景カードと同量の
                // 本流消費を保つためダミー消費する（フルシムは背景も Fork ＝消費ゼロで自動整合）。
                ConsumeLegacyManagerCardRng(a, b);
                var mgr = a.Id == _managerId ? a : b;
                var opp = a.Id == _managerId ? b : a;
                var matchDay = MatchDayOf(roundsRemaining, i);
                var mercy = MercyRuleEnabledFor(roundsRemaining);
                // 初戦（再試合回0）は従来と完全一致（同一Fork・同一context）＝非引き分け試合は決定論baseline不変。
                var first = _playerResolver.Resolve(mgr, opp, _rng.Fork(PlayerMatchStream(roundsRemaining, i)),
                    mercy, new TournamentMatchContext(roundsRemaining, matchDay, _pitchLedger));
                var managerIsAway = first.ManagerIsAway;
                var awaySchool = managerIsAway ? mgr : opp;
                var homeSchool = managerIsAway ? opp : mgr;
                var (decisive, awayWon, replays) = DecideByReplay(awaySchool, homeSchool, attempt =>
                    attempt == 0 ? first.Result
                        : _playerResolver.Resolve(mgr, opp, _rng.Fork(PlayerMatchStream(roundsRemaining, i, attempt)),
                            mercy, new TournamentMatchContext(roundsRemaining, matchDay, _pitchLedger, attempt)).Result);
                var r = decisive;
                var managerWon = managerIsAway ? awayWon : !awayWon;
                var mgrRuns = managerIsAway ? r.AwayRuns : r.HomeRuns;
                var oppRuns = managerIsAway ? r.HomeRuns : r.AwayRuns;
                var mw = managerWon ? mgr : opp;
                var ml = managerWon ? opp : mgr;
                var mws = Math.Max(mgrRuns, oppRuns);
                var mls = Math.Min(mgrRuns, oppRuns);
                outcome = new PlayerMatchOutcome(
                    managerWon, opp.Name, opp.Tier, mgrRuns, oppRuns, roundName, IsChampion: false,
                    Detail: r, ManagerWasAway: managerIsAway, MercyEnded: r.MercyEnded, TieReplays: replays,
                    OpponentId: opp.Id);
                _matches.Add(new BracketMatch(roundName, roundsRemaining, mw.Name, ml.Name, mw.Id, ml.Id, mws, mls, true, r.MercyEnded));
                RecordCard(round, i, mw.Id == a.Id, mws, mls, r.MercyEnded);
                RecordOutings(r, managerIsAway ? mgr : opp, managerIsAway ? opp : mgr, matchDay);
                next[i] = mw;
                continue;
            }

            // 背景カード（フルシム or 集計モデル）。自校が resolver 無しで関与する場合もここ。
            var (winner, loser, winScore, loseScore) = ResolveBackgroundCard(a, b, roundsRemaining, i);
            _matches.Add(new BracketMatch(
                roundName, roundsRemaining, winner.Name, loser.Name, winner.Id, loser.Id, winScore, loseScore, involvesManager));
            RecordCard(round, i, winner.Id == a.Id, winScore, loseScore);

            if (involvesManager)
            {
                var managerWon = winner.Id == _managerId;
                var opponent = managerWon ? loser : winner;
                outcome = new PlayerMatchOutcome(
                    managerWon, opponent.Name, opponent.Tier,
                    managerWon ? winScore : loseScore,
                    managerWon ? loseScore : winScore,
                    roundName, IsChampion: false, OpponentId: opponent.Id);
            }

            next[i] = winner;
        }
        _roundEntrants[round + 1] = next;
        return next;
    }

    /// <summary>
    /// ライブ観戦: 当該ラウンドを「自校カードの直前」まで解決して一時停止し、自校戦のライブ進行体を返す。
    /// 本流RNGの消費は <see cref="ResolveRound"/> と同順（各カードで AggregateMatch＋SynthesizeScore、
    /// 自校カードでは加えて隔離Fork を1本）。自校カードで停止するため、それ以降のカードは Complete で解決する。
    /// </summary>
    private LivePlayerMatch BeginRound(School?[] cur, string roundName, int roundsRemaining)
    {
        var round = RoundIndexOf(cur);
        var next = new School?[cur.Length / 2];
        for (var i = 0; i < next.Length; i++)
        {
            var a = cur[2 * i];
            var b = cur[2 * i + 1];
            if (a is null) { next[i] = b; continue; }
            if (b is null) { next[i] = a; continue; }

            var involvesManager = a.Id == _managerId || b.Id == _managerId;
            if (involvesManager)
            {
                // 自校カード: 詳細は隔離Fork のライブ進行体へ。ここで停止し、Complete で確定する。
                // ResolveRound と同一の本流消費順を保つ（集計モデル経路のみダミー消費・フルシムは消費なし）。
                ConsumeLegacyManagerCardRng(a, b);
                var mgr = a.Id == _managerId ? a : b;
                var opp = a.Id == _managerId ? b : a;
                var matchDay = MatchDayOf(roundsRemaining, i);
                var context = new TournamentMatchContext(roundsRemaining, matchDay, _pitchLedger);
                var live = _playerResolver!.BeginLive(mgr, opp, _rng.Fork(PlayerMatchStream(roundsRemaining, i)),
                    MercyRuleEnabledFor(roundsRemaining), context);
                var handle = new LivePlayerMatch(live.Progression, live.ManagerIsAway, opp.Name, roundName, opp.Id);
                _pending = new PendingLiveMatch(
                    cur, next, i, mgr, opp, roundName, roundsRemaining, live.ManagerIsAway, handle, matchDay);
                return handle;
            }

            var (winner, loser, winScore, loseScore) = ResolveBackgroundCard(a, b, roundsRemaining, i);
            _matches.Add(new BracketMatch(roundName, roundsRemaining, winner.Name, loser.Name, winner.Id, loser.Id, winScore, loseScore, false));
            RecordCard(round, i, winner.Id == a.Id, winScore, loseScore);
            next[i] = winner;
        }
        throw new InvalidOperationException("このラウンドに自校の試合がありません（不戦勝は AdvanceUntilPlayerMatch で消化済みのはず）。");
    }

    /// <summary>
    /// ライブ観戦: 終局結果から自校カードを確定し、当該ラウンドの残りカード（自校スロット以降）を本流で解決する。
    /// 残りカードの消費順も ResolveRound と一致（自校戦の隔離Fork はライブ中に消費済み・本流は不変）。
    /// </summary>
    private PlayerMatchOutcome CompleteRound(GameResult result)
    {
        var p = _pending!;
        _pending = null;

        // 観戦した試合（再試合回0）が延長上限で引き分けなら、別Fork の再試合で決着させる（設計書05 §1.3, issue #253）。
        // 再試合は観戦せず本流で自動解決するため、非観戦（自動消化）の ResolveRound と同一の決着関数・同一 Fork を通す
        // ＝観戦してもしなくても大会展開は不変（決定論契約）。観戦した引き分け試合は結果画面で「再試合」と明示する。
        var awaySchool = p.ManagerIsAway ? p.Mgr : p.Opp;
        var homeSchool = p.ManagerIsAway ? p.Opp : p.Mgr;
        var mercy = MercyRuleEnabledFor(p.RoundsRemaining);
        var (decisive, awayWon, replays) = DecideByReplay(awaySchool, homeSchool, attempt =>
            attempt == 0 ? result
                : _playerResolver!.Resolve(p.Mgr, p.Opp, _rng.Fork(PlayerMatchStream(p.RoundsRemaining, p.Slot, attempt)),
                    mercy, new TournamentMatchContext(p.RoundsRemaining, p.MatchDay, _pitchLedger, attempt)).Result);
        var r = decisive;
        var managerWon = p.ManagerIsAway ? awayWon : !awayWon;
        var mgrRuns = p.ManagerIsAway ? r.AwayRuns : r.HomeRuns;
        var oppRuns = p.ManagerIsAway ? r.HomeRuns : r.AwayRuns;
        var winner = managerWon ? p.Mgr : p.Opp;
        var loser = managerWon ? p.Opp : p.Mgr;
        var winScore = Math.Max(mgrRuns, oppRuns);
        var loseScore = Math.Min(mgrRuns, oppRuns);
        var round = RoundIndexOf(p.Cur);
        _matches.Add(new BracketMatch(
            p.RoundName, p.RoundsRemaining, winner.Name, loser.Name, winner.Id, loser.Id, winScore, loseScore, true, r.MercyEnded));
        RecordCard(round, p.Slot, winner.Id == p.Cur[2 * p.Slot]!.Id, winScore, loseScore, r.MercyEnded);
        RecordOutings(r, awaySchool, homeSchool, p.MatchDay);
        p.Next[p.Slot] = winner;

        // 自校スロット以降の残りカードを本流で解決（このラウンドの自校戦は1つだけ＝以降は非自校カード）。
        for (var i = p.Slot + 1; i < p.Next.Length; i++)
        {
            var a = p.Cur[2 * i];
            var b = p.Cur[2 * i + 1];
            if (a is null) { p.Next[i] = b; continue; }
            if (b is null) { p.Next[i] = a; continue; }

            var (w, l, ws, ls) = ResolveBackgroundCard(a, b, p.RoundsRemaining, i);
            _matches.Add(new BracketMatch(p.RoundName, p.RoundsRemaining, w.Name, l.Name, w.Id, l.Id, ws, ls, false));
            RecordCard(round, i, w.Id == a.Id, ws, ls);
            p.Next[i] = w;
        }

        _roundEntrants[round + 1] = p.Next;
        _current = p.Next;
        return new PlayerMatchOutcome(
            managerWon, p.Opp.Name, p.Opp.Tier, mgrRuns, oppRuns, p.RoundName, IsChampion: false,
            Detail: r, ManagerWasAway: p.ManagerIsAway, MercyEnded: r.MercyEnded, TieReplays: replays,
            OpponentId: p.Opp.Id);
    }

    /// <summary>得点差(margin)から表示用スコアを合成する（敗者0〜3点＋差）。決定論（注入乱数）。</summary>
    private (int WinnerScore, int LoserScore) SynthesizeScore(int margin)
    {
        var loser = _rng.NextInt(0, 4);
        return (loser + Math.Max(1, margin), loser);
    }

    /// <summary>
    /// 裏試合カード（自校非関与）を解決する。フルシム有効なら両校ロスターを <see cref="GameEngine"/> で解いて
    /// 実スコアを返し（＋全国成績へ畳み込み）、無効なら従来の集計モデル（<see cref="AggregateMatch"/>）を使う。
    /// フルシムは本流 _rng を消費せず Fork 隔離ストリームのみ使う（決定論・背景試合の独立性）。
    /// </summary>
    private (School Winner, School Loser, int WinScore, int LoseScore) ResolveBackgroundCard(
        School a, School b, int roundsRemaining, int slot)
    {
        if (_backgroundResolver is not null)
        {
            var matchDay = MatchDayOf(roundsRemaining, slot);
            // 引き分け再試合（設計書05 §1.3, issue #253）。初戦（attempt 0）は従来と同一 Fork・同一 context ＝
            // 非引き分け試合は決定論baseline不変。延長上限で引き分けたときだけ別Fork の再試合を積む。
            // 各試合（引き分け含む）を全国成績へ畳み込み・台帳へ記録する（再試合＝両試合とも公式試合という現実準拠）。
            var (decisive, awayWon, _) = DecideByReplay(a, b, attempt =>
            {
                var context = new TournamentMatchContext(roundsRemaining, matchDay, _pitchLedger, attempt);
                var g = _backgroundResolver.Resolve(a, b, _rng.Fork(BackgroundStream(roundsRemaining, slot, attempt)), context);
                RecordOutings(g, a, b, matchDay);
                _onBackgroundMatch?.Invoke(a.Id, b.Id, g);   // 全国成績への畳み込みは**記録時**（上のコメント参照）。
                return g;
            });
            var winner = awayWon ? a : b;
            var loser = awayWon ? b : a;
            return (winner, loser, Math.Max(decisive.AwayRuns, decisive.HomeRuns), Math.Min(decisive.AwayRuns, decisive.HomeRuns));
        }
        var (w, l, margin) = AggregateMatch.PlayDetailed(a, b, _coeff, _rng);
        var (ws, ls) = SynthesizeScore(margin);
        return (w, l, ws, ls);
    }

    /// <summary>
    /// 試合結果から両軍投手の球数を大会台帳（#41）へ記録する（エース温存判断, issue #42 の消耗入力）。
    /// 自校投手は SourceId、相手校生成投手は (校ID, 背番号) をキーにする（<see cref="PitcherLedgerKey"/>）。
    /// </summary>
    private void RecordOutings(GameResult r, School awaySchool, School homeSchool, int matchDay)
    {
        RecordSide(r.AwayPitching, awaySchool.Id, matchDay);
        RecordSide(r.HomePitching, homeSchool.Id, matchDay);
    }

    /// <summary>
    /// 片チームぶんの登板を台帳へ積む。
    ///
    /// <para>⚠ <b>キーの選択を「SourceId を持つか」で分けてはいけない</b>（OPEN-QUESTIONS Q26・2026-08-12 修正）。
    /// AI校の選手も <c>AiRosterFactory.Bootstrap</c> が <c>nextId = 1</c> から振った<b>校内選手ID</b>を
    /// SourceId に焼くので、旧実装（<c>SourceId is int sid ? ForPlayer(sid) : …</c>）では
    /// <b>全AI校の「選手ID 5」が同じキー</b>に潰れていた（実測: 64校フルシム大会で79登板→19キー）。
    /// さらに唯一の読み手 <see cref="Roster.AceRestSelector"/> は <c>ForOpponent(校ID, 背番号)</c> で引くため、
    /// AI校のエース温存は<b>台帳の消耗を一度も見ていなかった</b>（<c>fatigueLoad</c> が常に0）。</para>
    ///
    /// <para>したがって<b>自校かどうか</b>で分ける: 自校は育成選手ID（セーブ内で一意・年をまたいでも安定）、
    /// AI校は (校ID, 背番号)＝読み手の引き方と揃える。校IDは全国で一意、背番号は校内で一意なので衝突しない。</para>
    /// </summary>
    private void RecordSide(IReadOnlyList<PitchingLine> lines, int schoolId, int matchDay)
    {
        var isManagerSchool = schoolId == _managerId;
        foreach (var l in lines)
        {
            var key = isManagerSchool && l.SourceId is int sid
                ? PitcherLedgerKey.ForPlayer(sid)
                : PitcherLedgerKey.ForOpponent(schoolId, l.UniformNumber);
            _pitchLedger.Record(key, l.Pitches, matchDay);
        }
    }

    /// <summary>
    /// 引き分け再試合の共通決着関数（設計書05 §1.3, issue #253）。自校戦（自動消化 <see cref="ResolveRound"/>／
    /// 観戦 <see cref="CompleteRound"/>）と裏試合（<see cref="ResolveBackgroundCard"/>）はすべてここを通る（経路一致）。
    /// <paramref name="playGame"/>(attempt) で1試合を得、延長上限で引き分け（<see cref="GameResult.Tied"/>）なら
    /// 別Fork の再試合を最大 <see cref="MaxTieReplays"/> 回まで繰り返す。全再試合でも引き分けなら規定
    /// （安打数→強さ→Id）で強制決着し、有限終了を保証する（安全弁。4試合連続15回引き分けは無視できる確率）。
    /// 各再試合は attempt を変えた別 Fork ＝決定論。<paramref name="away"/>/<paramref name="home"/> は
    /// GameResult の先攻/後攻に対応する School（規定決着の強さ・Id 比較に使う）。
    /// 戻り値: Decisive＝決着した（最後の）試合、AwayWon＝先攻が勝ったか、Replays＝行った再試合数（0＝初戦決着）。
    /// </summary>
    private static (GameResult Decisive, bool AwayWon, int Replays) DecideByReplay(
        School away, School home, Func<int, GameResult> playGame)
    {
        var r = playGame(0);
        var replays = 0;
        while (r.Tied && replays < MaxTieReplays)
            r = playGame(++replays);
        var awayWon = r.AwayRuns > r.HomeRuns
            || (r.AwayRuns == r.HomeRuns && (r.AwayHits > r.HomeHits
                || (r.AwayHits == r.HomeHits && (away.Strength > home.Strength
                    || (away.Strength >= home.Strength && away.Id < home.Id)))));
        return (r, awayWon, replays);
    }

    /// <summary>
    /// フルシム有効時、自校カードでも「背景カードと同量の本流消費」を保つためのダミー消費（集計モデル経路のみ）。
    /// フルシムでは背景も Fork のため消費ゼロ＝自校カードも消費ゼロで整合する（何もしない）。
    /// </summary>
    private void ConsumeLegacyManagerCardRng(School a, School b)
    {
        if (_backgroundResolver is not null) return;   // フルシム: 背景も Fork ＝消費なしで揃う
        var (_, _, margin) = AggregateMatch.PlayDetailed(a, b, _coeff, _rng);
        SynthesizeScore(margin);   // 破棄（本流消費だけ従来と一致させる）
    }

    /// <summary>
    /// 裏試合フルシム用 Fork ストリームID（ラウンド×スロット×再試合回で一意・決定論）。自校戦とも本流とも別系列。
    /// <paramref name="attempt"/>（引き分け再試合回, issue #253）は上位ビットへ載せる＝ attempt=0 は従来のIDと完全一致。
    /// </summary>
    private static ulong BackgroundStream(int roundsRemaining, int slot, int attempt = 0)
        => 0xB6B6_0000UL ^ ((ulong)roundsRemaining << 16) ^ (uint)slot ^ ((ulong)(uint)attempt << 40);

    /// <summary>
    /// 自校戦の詳細シム用 Fork ストリームID（ラウンド×スロット×再試合回で一意・決定論）。本流とは別系列。
    /// <paramref name="attempt"/>（引き分け再試合回, issue #253）は上位ビットへ載せる＝ attempt=0 は従来のIDと完全一致。
    /// </summary>
    private static ulong PlayerMatchStream(int roundsRemaining, int slot, int attempt = 0)
        => 0xA5A50000UL ^ ((ulong)roundsRemaining << 16) ^ (uint)slot ^ ((ulong)(uint)attempt << 40);

    private int IndexOfManager(School?[] cur)
    {
        for (var i = 0; i < cur.Length; i++)
            if (cur[i]?.Id == _managerId) return i;
        return -1;
    }

    /// <summary>
    /// コールドゲーム（マーシールール）の有無（設計書05 §1.3, OPEN-QUESTIONS Q18）。
    /// 甲子園本大会（<see cref="_isNationalTournament"/>）＋地方大会の決勝（roundsRemaining==1）はOFF、
    /// それ以外の地方大会はON。
    /// </summary>
    private bool MercyRuleEnabledFor(int roundsRemaining) => !_isNationalTournament && roundsRemaining != 1;

    private string RoundNameFor(int roundsRemaining) => roundsRemaining switch
    {
        1 => "決勝",
        2 => "準決勝",
        3 => "準々決勝",
        _ => (_totalRounds - roundsRemaining + 1) + "回戦",
    };
}
