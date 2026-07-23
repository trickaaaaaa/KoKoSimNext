using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>大会内の日程（抽象日カウント, 設計書03/05）。初戦日とラウンド間の中○日を持つ。YAML駆動。</summary>
public sealed record TournamentSchedule
{
    /// <summary>初戦の試合日（開幕=0日目からの相対）。</summary>
    public int FirstRoundDay { get; init; } = 1;
    /// <summary>ラウンド間の日数間隔（＝中(RoundGapDays−1)日）。既定2＝中1日＝地方予選の連戦感（8ラウンド×2日＝決勝は15日目≒2週強）。
    /// MatchDay に直結し、AceRestSelector の温存抽選ストリーム(seed に MatchDay を含む)を左右するため帯/決定論baselineに効く（OPEN-QUESTIONS Q23）。</summary>
    public int RoundGapDays { get; init; } = 2;

    /// <summary>ラウンド index（0基点＝初戦）の試合日を返す。</summary>
    public int MatchDay(int roundIndex) => FirstRoundDay + roundIndex * RoundGapDays;
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
    bool MercyEnded = false);

/// <summary>
/// ライブ観戦する自校戦の受け渡し（<see cref="TournamentRunner.BeginNextPlayerMatch"/> の戻り）。
/// UI は Progression を打席単位で進め、終局の GameResult を <see cref="TournamentRunner.CompleteNextPlayerMatch"/>
/// へ戻して大会を継続する。OpponentName/RoundName は観戦画面のスコアボード表示用。
/// </summary>
public sealed record LivePlayerMatch(
    Match.Timeline.Playback.MatchProgression Progression,
    bool ManagerIsAway,
    string OpponentName,
    string RoundName);

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
    private readonly List<BracketMatch> _matches = new();
    /// <summary>大会中の投手球数台帳（issue #41）。エース温存判断（issue #42）の消耗入力として本大会内で共有する。</summary>
    private readonly TournamentPitchLedger _pitchLedger = new();

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
        bool isNationalTournament = false)
    {
        if (entrants.Count == 0) throw new ArgumentException("参加校が空です。");
        if (entrants.All(s => s.Id != manager.Id)) throw new ArgumentException("自校が参加校に含まれていません。");

        _coeff = coeff;
        _rng = rng;
        _schedule = schedule;
        _playerResolver = playerResolver;
        _backgroundResolver = backgroundResolver;
        _managerId = manager.Id;
        _isNationalTournament = isNationalTournament;
        Title = title;

        // 強さ順にシード（同値は Id で決定化）。不足分は不戦勝(null)。
        var seeded = entrants.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).ToList();
        var size = TournamentEngine.NextPowerOfTwo(seeded.Count);
        _totalRounds = TournamentRecorder.RoundsRemaining(size);
        var order = TournamentEngine.SeedOrder(size);
        _current = new School?[size];
        for (var slot = 0; slot < size; slot++)
        {
            var seed = order[slot] - 1;
            _current[slot] = seed < seeded.Count ? seeded[seed] : null;
        }

        _roundEntrants = new School?[]?[_totalRounds + 1];
        _roundEntrants[0] = (School?[])_current.Clone();   // 初期シード配置は確定済み＝樹形図の起点。

        AdvanceUntilPlayerMatch();
    }

    /// <summary>自校の次戦相手（不戦勝続き/敗退済み/優勝は null）。</summary>
    public School? NextOpponent => _nextOpponent;
    /// <summary>次戦相手のランク（NextOpponent が null のときは意味を持たない）。</summary>
    public Tier NextOpponentTier => _nextOpponent?.Tier ?? Tier.G;
    /// <summary>次戦の試合日（大会開幕からの相対日）。</summary>
    public int NextMatchDay => _schedule.MatchDay(_roundIndex);
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
            FinishBracketInBackground();   // 残りは背景で最後まで消化（概要に優勝校を残す）。
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
            FinishBracketInBackground();
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

    private void RecordCard(int round, int slot, bool topWon, int winnerScore, int loserScore, bool mercyEnded = false)
        => _cardResults[(round, slot)] = new CardResult(topWon, winnerScore, loserScore, mercyEnded);

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

    /// <summary>自校敗退後、残りブラケットを最後まで自動消化する（大会概要用）。</summary>
    private void FinishBracketInBackground()
    {
        while (_current.Length > 1)
        {
            var roundsRemaining = TournamentRecorder.RoundsRemaining(_current.Length);
            _current = ResolveRound(_current, RoundNameFor(roundsRemaining), roundsRemaining, out _);
        }
        ChampionName = _current[0]?.Name;
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
                var matchDay = _schedule.MatchDay(round);
                var context = new TournamentMatchContext(roundsRemaining, matchDay, _pitchLedger);
                var detail = _playerResolver.Resolve(mgr, opp, _rng.Fork(PlayerMatchStream(roundsRemaining, i)),
                    MercyRuleEnabledFor(roundsRemaining), context);
                var r = detail.Result;
                var mgrRuns = detail.ManagerIsAway ? r.AwayRuns : r.HomeRuns;
                var oppRuns = detail.ManagerIsAway ? r.HomeRuns : r.AwayRuns;
                var managerWon = mgrRuns > oppRuns;
                var mw = managerWon ? mgr : opp;
                var ml = managerWon ? opp : mgr;
                var mws = Math.Max(mgrRuns, oppRuns);
                var mls = Math.Min(mgrRuns, oppRuns);
                outcome = new PlayerMatchOutcome(
                    managerWon, opp.Name, opp.Tier, mgrRuns, oppRuns, roundName, IsChampion: false,
                    Detail: r, ManagerWasAway: detail.ManagerIsAway, MercyEnded: r.MercyEnded);
                _matches.Add(new BracketMatch(roundName, roundsRemaining, mw.Name, ml.Name, mw.Id, ml.Id, mws, mls, true, r.MercyEnded));
                RecordCard(round, i, mw.Id == a.Id, mws, mls, r.MercyEnded);
                RecordOutings(r, detail.ManagerIsAway ? mgr : opp, detail.ManagerIsAway ? opp : mgr, matchDay);
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
                    roundName, IsChampion: false);
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
                var matchDay = _schedule.MatchDay(round);
                var context = new TournamentMatchContext(roundsRemaining, matchDay, _pitchLedger);
                var live = _playerResolver!.BeginLive(mgr, opp, _rng.Fork(PlayerMatchStream(roundsRemaining, i)),
                    MercyRuleEnabledFor(roundsRemaining), context);
                var handle = new LivePlayerMatch(live.Progression, live.ManagerIsAway, opp.Name, roundName);
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

        var mgrRuns = p.ManagerIsAway ? result.AwayRuns : result.HomeRuns;
        var oppRuns = p.ManagerIsAway ? result.HomeRuns : result.AwayRuns;
        var managerWon = mgrRuns > oppRuns;
        var winner = managerWon ? p.Mgr : p.Opp;
        var loser = managerWon ? p.Opp : p.Mgr;
        var winScore = Math.Max(mgrRuns, oppRuns);
        var loseScore = Math.Min(mgrRuns, oppRuns);
        var round = RoundIndexOf(p.Cur);
        _matches.Add(new BracketMatch(
            p.RoundName, p.RoundsRemaining, winner.Name, loser.Name, winner.Id, loser.Id, winScore, loseScore, true, result.MercyEnded));
        RecordCard(round, p.Slot, winner.Id == p.Cur[2 * p.Slot]!.Id, winScore, loseScore, result.MercyEnded);
        RecordOutings(result, p.ManagerIsAway ? p.Mgr : p.Opp, p.ManagerIsAway ? p.Opp : p.Mgr, p.MatchDay);
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
            Detail: result, ManagerWasAway: p.ManagerIsAway, MercyEnded: result.MercyEnded);
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
            var matchDay = _schedule.MatchDay(_totalRounds - roundsRemaining);
            var context = new TournamentMatchContext(roundsRemaining, matchDay, _pitchLedger);
            var r = _backgroundResolver.Resolve(a, b, _rng.Fork(BackgroundStream(roundsRemaining, slot)), context);
            RecordOutings(r, a, b, matchDay);
            return DecideFromRuns(a, b, r);
        }
        var (winner, loser, margin) = AggregateMatch.PlayDetailed(a, b, _coeff, _rng);
        var (ws, ls) = SynthesizeScore(margin);
        return (winner, loser, ws, ls);
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

    private void RecordSide(IReadOnlyList<PitchingLine> lines, int schoolId, int matchDay)
    {
        foreach (var l in lines)
        {
            var key = l.SourceId is int sid ? PitcherLedgerKey.ForPlayer(sid) : PitcherLedgerKey.ForOpponent(schoolId, l.UniformNumber);
            _pitchLedger.Record(key, l.Pitches, matchDay);
        }
    }

    /// <summary>
    /// フルシム結果（a=先攻・b=後攻）から勝敗と表示スコアを決める。引き分け（延長上限）は安打数→強さ→Id で
    /// 決定論的に解決する（knockout は勝者が要る）。
    /// </summary>
    private static (School Winner, School Loser, int WinScore, int LoseScore) DecideFromRuns(
        School a, School b, GameResult r)
    {
        var aWon = r.AwayRuns > r.HomeRuns
            || (r.AwayRuns == r.HomeRuns && (r.AwayHits > r.HomeHits
                || (r.AwayHits == r.HomeHits && (a.Strength > b.Strength || (a.Strength >= b.Strength && a.Id < b.Id)))));
        var winner = aWon ? a : b;
        var loser = aWon ? b : a;
        return (winner, loser, Math.Max(r.AwayRuns, r.HomeRuns), Math.Min(r.AwayRuns, r.HomeRuns));
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

    /// <summary>裏試合フルシム用 Fork ストリームID（ラウンド×スロットで一意・決定論）。自校戦とも本流とも別系列。</summary>
    private static ulong BackgroundStream(int roundsRemaining, int slot)
        => 0xB6B6_0000UL ^ ((ulong)roundsRemaining << 16) ^ (uint)slot;

    /// <summary>自校戦の詳細シム用 Fork ストリームID（ラウンド×スロットで一意・決定論）。本流とは別系列。</summary>
    private static ulong PlayerMatchStream(int roundsRemaining, int slot)
        => 0xA5A50000UL ^ ((ulong)roundsRemaining << 16) ^ (uint)slot;

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
