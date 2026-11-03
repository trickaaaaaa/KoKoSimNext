using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>ブロックリーグ戦の1試合（節・ブロック・対戦・結果）。未消化は <see cref="Played"/>=false。</summary>
public sealed record LeagueGameView(
    int Round, int Block, int MatchDay,
    int AId, string AName, int BId, string BName,
    bool Played, int AScore, int BScore, bool MercyEnded, bool InvolvesManager)
{
    public bool AWon => Played && AScore > BScore;
}

/// <summary>ブロック順位表の1行。<see cref="InQualifyingPlace"/>＝いまの順位が進出圏（上位 advance 校）。</summary>
public sealed record LeagueStandingView(
    int Rank, int SchoolId, string SchoolName, bool IsManager, int Wins, int Losses, int RunMargin, int Played,
    bool InQualifyingPlace);

/// <summary>1ブロックぶんの表示（順位表＋対戦）。</summary>
public sealed record LeagueBlockView(
    int Block, IReadOnlyList<LeagueStandingView> Standings, IReadOnlyList<LeagueGameView> Games, bool IsManagerBlock);

/// <summary>
/// 秋季地区予選のブロックリーグ戦を自校の試合で止めながら進める進行体（神奈川・issue #772）。
/// <see cref="TournamentRunner"/> と同じ契約（<see cref="ITournamentRunner"/>）で、Shell は同じ手順で回せる。
///
/// <para>地区の全校を <see cref="BlockDraw.Split"/> でブロックへ分け（夏の順位でシード分散）、
/// ブロックごとに円順列の総当たり（<see cref="BlockDraw.RoundRobinRounds"/>）を節単位で進める。
/// 1節＝全ブロックのその節の試合。自校の試合で止まり、ほかは背景で解く（フルシム or 集計モデル）。</para>
///
/// <para>勝ち抜き戦との違い: 負けても終わらない（全節を消化して自校の大会が終わる）／優勝が無い／
/// 終わった時点で全試合が消化済み（残りの背景消化が無い）。順位は <see cref="RoundRobin.Rank"/>
/// （勝数→直接対決→得失点差）。各ブロックの上位 <c>advancePerBlock</c> 校が県大会へ進む。</para>
///
/// <para>決定論: 試合は節×カード×再試合回で決まる Fork ストリームで解く＝観戦してもしなくても、
/// 背景の解き順が違っても同じ結果。集計モデル経路（resolver 無し）だけ本流を消費する。</para>
/// </summary>
public sealed class BlockLeagueRunner : ITournamentRunner
{
    /// <summary>
    /// リーグ戦の試合に付ける「残りラウンド数」。勝ち抜き戦の意味では序盤（市営級の球場・コールドあり・
    /// 観客まばら）。決勝(1)・準決勝(2)と取り違えない値にする＝通算戦績の最高成績・優勝判定に掛からない。
    /// </summary>
    public const int LeagueRoundsRemaining = 9;

    private const int NoManagerId = int.MinValue;

    private readonly NationCoefficients _coeff;
    private readonly IRandomSource _rng;
    private readonly TournamentSchedule _schedule;
    private readonly IPlayerMatchResolver? _playerResolver;
    private readonly IBackgroundMatchResolver? _backgroundResolver;
    private readonly Action<int, int, GameResult>? _onBackgroundMatch;
    private readonly int _managerId;
    private readonly int _advancePerBlock;

    private readonly List<List<School>> _blocks;
    private readonly List<IReadOnlyList<IReadOnlyList<(int A, int B)>>> _pairings;
    private readonly int _totalRounds;
    private readonly int _fieldSize;

    /// <summary>消化済みの試合（節・カード添字で引く）。</summary>
    private readonly Dictionary<(int Round, int Card), PlayedGame> _results = new();
    private readonly List<BracketMatch> _matches = new();
    private readonly TournamentPitchLedger _pitchLedger = new();

    private int _roundIndex;
    private School? _nextOpponent;
    private Pending? _pending;

    private sealed record PlayedGame(int AScore, int BScore, bool MercyEnded);
    private sealed record Card(int Index, int Block, School A, School B);
    private sealed record Pending(int Round, int Slot, School Mgr, School Opp, bool ManagerIsAway, LivePlayerMatch Live, int MatchDay);

    public string Title { get; }
    public bool PlayerActive { get; private set; } = true;
    public bool IsChampion => false;
    public bool Finished => !PlayerActive;
    public string? ChampionName => null;
    public School? NextOpponent => _nextOpponent;
    public int NextMatchDay => _schedule.LeagueMatchDay(_roundIndex);
    public int FieldSize => _fieldSize;
    public int TotalRounds => _totalRounds;
    public string RoundName => RoundNameFor(_roundIndex);
    /// <summary>各ブロックから県大会へ進む校数。</summary>
    public int AdvancePerBlock => _advancePerBlock;
    /// <summary>消化済みの節数。</summary>
    public int RoundIndex => _roundIndex;

    /// <param name="blocks">ブロック分け（<see cref="BlockDraw.Split"/> の結果）。自校を含むこと。</param>
    public BlockLeagueRunner(
        IReadOnlyList<IReadOnlyList<School>> blocks, School manager, int advancePerBlock,
        NationCoefficients coeff, IRandomSource rng, TournamentSchedule schedule, string title,
        IPlayerMatchResolver? playerResolver = null, IBackgroundMatchResolver? backgroundResolver = null,
        Action<int, int, GameResult>? onBackgroundMatch = null)
        : this(blocks, ManagerIdIn(blocks, manager), advancePerBlock, coeff, rng, schedule, title,
            playerResolver, backgroundResolver, onBackgroundMatch)
    {
    }

    /// <summary>
    /// 自校が出ない地区のリーグ戦（観るだけ＝全試合が背景）。生成直後から <see cref="BracketResolutionPending"/> が真で、
    /// <see cref="StepBracketResolution"/> 1回で1節進む。
    /// </summary>
    public static BlockLeagueRunner Spectated(
        IReadOnlyList<IReadOnlyList<School>> blocks, int advancePerBlock,
        NationCoefficients coeff, IRandomSource rng, TournamentSchedule schedule, string title,
        IBackgroundMatchResolver? backgroundResolver = null, Action<int, int, GameResult>? onBackgroundMatch = null)
        => new(blocks, NoManagerId, advancePerBlock, coeff, rng, schedule, title, null, backgroundResolver, onBackgroundMatch);

    private BlockLeagueRunner(
        IReadOnlyList<IReadOnlyList<School>> blocks, int managerId, int advancePerBlock,
        NationCoefficients coeff, IRandomSource rng, TournamentSchedule schedule, string title,
        IPlayerMatchResolver? playerResolver, IBackgroundMatchResolver? backgroundResolver,
        Action<int, int, GameResult>? onBackgroundMatch)
    {
        _coeff = coeff;
        _rng = rng;
        _schedule = schedule;
        _playerResolver = playerResolver;
        _backgroundResolver = backgroundResolver;
        _onBackgroundMatch = onBackgroundMatch;
        _managerId = managerId;
        _advancePerBlock = Math.Max(1, advancePerBlock);
        Title = title;
        _blocks = blocks.Select(b => b.ToList()).ToList();
        _pairings = _blocks.Select(b => BlockDraw.RoundRobinRounds(b.Count)).ToList();
        _totalRounds = _pairings.Count == 0 ? 0 : _pairings.Max(p => p.Count);
        _fieldSize = _blocks.Sum(b => b.Count);

        if (managerId == NoManagerId) { PlayerActive = false; return; }   // 観るだけ＝StepBracketResolution で刻む
        AdvanceUntilPlayerMatch();
    }

    private static int ManagerIdIn(IReadOnlyList<IReadOnlyList<School>> blocks, School manager)
    {
        if (!blocks.Any(b => b.Any(s => s.Id == manager.Id)))
            throw new ArgumentException("自校がどのブロックにも含まれていません。");
        return manager.Id;
    }

    // ===== 表示 =====

    /// <summary>自校のブロックの添字（自校が居なければ -1）。</summary>
    public int ManagerBlockIndex => _blocks.FindIndex(b => b.Any(s => s.Id == _managerId));

    /// <summary>ブロックごとの順位表と対戦（表示専用・乱数を引かない）。</summary>
    public IReadOnlyList<LeagueBlockView> BuildBlockViews()
    {
        var views = new List<LeagueBlockView>(_blocks.Count);
        for (var b = 0; b < _blocks.Count; b++)
        {
            var games = new List<LeagueGameView>();
            foreach (var (round, card) in CardsOfBlock(b))
            {
                _results.TryGetValue((round, card.Index), out var r);
                games.Add(new LeagueGameView(round, b, _schedule.LeagueMatchDay(round),
                    card.A.Id, card.A.Name, card.B.Id, card.B.Name,
                    r is not null, r?.AScore ?? 0, r?.BScore ?? 0, r?.MercyEnded ?? false,
                    card.A.Id == _managerId || card.B.Id == _managerId));
            }
            var standings = Standings(b)
                .Select((s, i) => new LeagueStandingView(i + 1, s.School.Id, s.School.Name, s.School.Id == _managerId,
                    s.Wins, s.Losses, s.RunMargin, s.Wins + s.Losses, i < _advancePerBlock))
                .ToList();
            views.Add(new LeagueBlockView(b, standings, games, _blocks[b].Any(s => s.Id == _managerId)));
        }
        return views;
    }

    /// <summary>
    /// 県大会へ進む学校（各ブロックの上位 <see cref="AdvancePerBlock"/> 校・ブロック順→順位順）。
    /// 全節を消化する前に呼ぶと途中の順位で返す（呼び出し側は <see cref="Finished"/> 後に使う）。
    /// </summary>
    public IReadOnlyList<School> Qualifiers()
    {
        var list = new List<School>();
        for (var b = 0; b < _blocks.Count; b++) list.AddRange(Standings(b).Take(_advancePerBlock).Select(s => s.School));
        return list;
    }

    /// <summary>自校が県大会へ進んだか（全節消化後に意味を持つ）。</summary>
    public bool ManagerQualified => Qualifiers().Any(s => s.Id == _managerId);

    private IReadOnlyList<Standing> Standings(int block)
    {
        var teams = _blocks[block];
        var wins = teams.ToDictionary(t => t.Id, _ => 0);
        var losses = teams.ToDictionary(t => t.Id, _ => 0);
        var margin = teams.ToDictionary(t => t.Id, _ => 0);
        var head = new Dictionary<(int, int), int>();
        foreach (var (round, card) in CardsOfBlock(block))
        {
            if (!_results.TryGetValue((round, card.Index), out var r)) continue;
            var aWon = r.AScore > r.BScore;
            var w = aWon ? card.A : card.B;
            var l = aWon ? card.B : card.A;
            var m = Math.Abs(r.AScore - r.BScore);
            wins[w.Id]++; losses[l.Id]++;
            margin[w.Id] += m; margin[l.Id] -= m;
            head[(Math.Min(w.Id, l.Id), Math.Max(w.Id, l.Id))] = w.Id;
        }
        return RoundRobin.Rank(teams, wins, losses, margin, head);
    }

    public TournamentBracketView BuildBracketView()
    {
        var rounds = new List<BracketRound>(_totalRounds);
        for (var r = 0; r < _totalRounds; r++)
        {
            var name = RoundNameFor(r);
            var cards = new List<BracketCard>();
            foreach (var c in CardsOf(r))
            {
                _results.TryGetValue((r, c.Index), out var res);
                var played = res is not null;
                var aWon = played && res!.AScore > res.BScore;
                cards.Add(new BracketCard(r, c.Index, name,
                    new BracketSlot(c.A.Name, c.A.Id == _managerId, aWon, played ? res!.AScore : null),
                    new BracketSlot(c.B.Name, c.B.Id == _managerId, played && !aWon, played ? res!.BScore : null),
                    IsBye: false, IsPlayed: played, MercyEnded: res?.MercyEnded ?? false));
            }
            rounds.Add(new BracketRound(r, name, cards));
        }
        return new TournamentBracketView(Title, _matches.ToList(), ChampionName, IsChampion, !PlayerActive, rounds);
    }

    // ===== 自校の試合 =====

    public PlayerMatchOutcome PlayNextPlayerMatch()
    {
        if (Finished) throw new InvalidOperationException("大会は自校にとって終了しています。");
        if (_nextOpponent is null) throw new InvalidOperationException("次戦相手が未確定です。");
        var outcome = ResolveRound(_roundIndex);
        if (outcome is null) throw new InvalidOperationException("自校の試合が解決されませんでした。");
        _roundIndex++;
        AdvanceUntilPlayerMatch();
        return outcome;
    }

    public LivePlayerMatch BeginNextPlayerMatch()
    {
        if (_pending is not null) return _pending.Live;
        if (Finished) throw new InvalidOperationException("大会は自校にとって終了しています。");
        if (_nextOpponent is null) throw new InvalidOperationException("次戦相手が未確定です。");
        if (_playerResolver is null) throw new InvalidOperationException("ライブ観戦には IPlayerMatchResolver が必要です。");

        var round = _roundIndex;
        foreach (var c in CardsOf(round))
        {
            if (Involves(c))
            {
                ConsumeLegacyManagerCardRng(c.A, c.B);
                var mgr = c.A.Id == _managerId ? c.A : c.B;
                var opp = c.A.Id == _managerId ? c.B : c.A;
                var matchDay = _schedule.LeagueMatchDay(round);
                var live = _playerResolver.BeginLive(mgr, opp, _rng.Fork(PlayerStream(round, c.Index)),
                    MercyRuleEnabled, new TournamentMatchContext(LeagueRoundsRemaining, matchDay, _pitchLedger));
                var handle = new LivePlayerMatch(live.Progression, live.ManagerIsAway, opp.Name, RoundNameFor(round), opp.Id)
                    { RoundsRemaining = LeagueRoundsRemaining };
                _pending = new Pending(round, c.Index, mgr, opp, live.ManagerIsAway, handle, matchDay);
                return handle;
            }
            ResolveBackgroundCard(round, c);
        }
        throw new InvalidOperationException("この節に自校の試合がありません。");
    }

    public PlayerMatchOutcome CompleteNextPlayerMatch(GameResult result)
    {
        if (_pending is null) throw new InvalidOperationException("進行中のライブ自校戦がありません。");
        var p = _pending;
        _pending = null;
        var card = CardsOf(p.Round).First(c => c.Index == p.Slot);
        var outcome = FinishManagerCard(p.Round, card, p.Mgr, p.Opp, p.ManagerIsAway, p.MatchDay, result);
        foreach (var c in CardsOf(p.Round).Where(c => c.Index > p.Slot)) ResolveBackgroundCard(p.Round, c);
        _roundIndex++;
        AdvanceUntilPlayerMatch();
        return outcome;
    }

    public bool HasPendingLiveMatch => _pending is not null;

    public LivePlayerMatch ResumePendingLiveMatch()
        => _pending?.Live ?? throw new InvalidOperationException("進行中のライブ自校戦がありません。");

    public void PrefetchCurrentRoundBackgroundCards()
    {
        if (_backgroundResolver is null || Finished || _roundIndex >= _totalRounds) return;
        var matchDay = _schedule.LeagueMatchDay(_roundIndex);
        foreach (var c in CardsOf(_roundIndex))
        {
            if (Involves(c)) continue;
            var context = new TournamentMatchContext(LeagueRoundsRemaining, matchDay, _pitchLedger);
            _backgroundResolver.Resolve(c.A, c.B, _rng.Fork(BackgroundStream(_roundIndex, c.Index)), context);
        }
    }

    // ===== 自校が出ない（観るだけ）の消化 =====

    /// <summary>観るだけのリーグ戦で、まだ消化していない節が残っているか（自校の出るリーグ戦では常に偽）。</summary>
    public bool BracketResolutionPending => _managerId == NoManagerId && _roundIndex < _totalRounds;
    public int RemainingBracketRounds => BracketResolutionPending ? _totalRounds - _roundIndex : 0;

    public void StepBracketResolution()
    {
        if (!BracketResolutionPending) return;
        ResolveRound(_roundIndex);
        _roundIndex++;
    }

    public void DrainBracketResolution()
    {
        while (BracketResolutionPending) StepBracketResolution();
    }

    // ===== 内部進行 =====

    /// <summary>自校の試合がある節まで進める（自校が休みの節は全試合を背景で解く）。全節を終えたら自校の大会は終わり。</summary>
    private void AdvanceUntilPlayerMatch()
    {
        while (_roundIndex < _totalRounds)
        {
            var mine = CardsOf(_roundIndex).FirstOrDefault(Involves);
            if (mine is not null)
            {
                _nextOpponent = mine.A.Id == _managerId ? mine.B : mine.A;
                return;
            }
            ResolveRound(_roundIndex);   // 自校は休み
            _roundIndex++;
        }
        _nextOpponent = null;
        PlayerActive = false;
    }

    /// <summary>1節の全試合を解く。自校が試合したら結果を返す。</summary>
    private PlayerMatchOutcome? ResolveRound(int round)
    {
        PlayerMatchOutcome? outcome = null;
        foreach (var c in CardsOf(round))
        {
            if (Involves(c) && _playerResolver is not null)
            {
                ConsumeLegacyManagerCardRng(c.A, c.B);
                var mgr = c.A.Id == _managerId ? c.A : c.B;
                var opp = c.A.Id == _managerId ? c.B : c.A;
                var matchDay = _schedule.LeagueMatchDay(round);
                var first = _playerResolver.Resolve(mgr, opp, _rng.Fork(PlayerStream(round, c.Index)),
                    MercyRuleEnabled, new TournamentMatchContext(LeagueRoundsRemaining, matchDay, _pitchLedger));
                outcome = FinishManagerCard(round, c, mgr, opp, first.ManagerIsAway, matchDay, first.Result);
                continue;
            }
            var (aScore, bScore) = ResolveBackgroundCard(round, c);
            if (Involves(c))
            {
                // 自校戦を resolver 無しで解いた（テスト・CLI の集計モデル）
                var mgrIsA = c.A.Id == _managerId;
                var opp = mgrIsA ? c.B : c.A;
                var mgrRuns = mgrIsA ? aScore : bScore;
                var oppRuns = mgrIsA ? bScore : aScore;
                outcome = new PlayerMatchOutcome(mgrRuns > oppRuns, opp.Name, opp.Tier, mgrRuns, oppRuns,
                    RoundNameFor(round), IsChampion: false, OpponentId: opp.Id);
            }
        }
        return outcome;
    }

    /// <summary>自校戦の結果（引き分けは別Forkの再試合で決着）を記録して結果を返す。</summary>
    private PlayerMatchOutcome FinishManagerCard(
        int round, Card c, School mgr, School opp, bool managerIsAway, int matchDay, GameResult firstResult)
    {
        var away = managerIsAway ? mgr : opp;
        var home = managerIsAway ? opp : mgr;
        var (r, awayWon, replays) = TournamentRunner.DecideByReplay(away, home, attempt =>
            attempt == 0 ? firstResult
                : _playerResolver!.Resolve(mgr, opp, _rng.Fork(PlayerStream(round, c.Index, attempt)),
                    MercyRuleEnabled, new TournamentMatchContext(LeagueRoundsRemaining, matchDay, _pitchLedger, attempt)).Result);
        var managerWon = managerIsAway ? awayWon : !awayWon;
        var mgrRuns = managerIsAway ? r.AwayRuns : r.HomeRuns;
        var oppRuns = managerIsAway ? r.HomeRuns : r.AwayRuns;
        // 同点のまま規定で決着した試合も勝者に1点上乗せせず記録する（勝敗は awayWon が正）
        var aIsMgr = c.A.Id == mgr.Id;
        var aScore = aIsMgr ? mgrRuns : oppRuns;
        var bScore = aIsMgr ? oppRuns : mgrRuns;
        if (aScore == bScore) { if (aIsMgr == managerWon) aScore++; else bScore++; }
        Record(round, c, aScore, bScore, r.MercyEnded, involvesManager: true);
        RecordOutings(r, away, home, matchDay);
        return new PlayerMatchOutcome(managerWon, opp.Name, opp.Tier, mgrRuns, oppRuns, RoundNameFor(round),
            IsChampion: false, Detail: r, ManagerWasAway: managerIsAway, MercyEnded: r.MercyEnded,
            TieReplays: replays, OpponentId: opp.Id);
    }

    /// <summary>自校が出ない試合を解いて記録する（フルシムは Fork・集計モデルは本流）。</summary>
    private (int AScore, int BScore) ResolveBackgroundCard(int round, Card c)
    {
        int aScore, bScore;
        var mercy = false;
        if (_backgroundResolver is not null)
        {
            var matchDay = _schedule.LeagueMatchDay(round);
            var (g, awayWon, _) = TournamentRunner.DecideByReplay(c.A, c.B, attempt =>
            {
                var context = new TournamentMatchContext(LeagueRoundsRemaining, matchDay, _pitchLedger, attempt);
                var game = _backgroundResolver.Resolve(c.A, c.B, _rng.Fork(BackgroundStream(round, c.Index, attempt)), context);
                RecordOutings(game, c.A, c.B, matchDay);
                _onBackgroundMatch?.Invoke(c.A.Id, c.B.Id, game);
                return game;
            });
            aScore = g.AwayRuns;
            bScore = g.HomeRuns;
            if (aScore == bScore) { if (awayWon) aScore++; else bScore++; }
            mercy = g.MercyEnded;
        }
        else
        {
            var (w, _, margin) = AggregateMatch.PlayDetailed(c.A, c.B, _coeff, _rng);
            var loser = _rng.NextInt(0, 4);
            var win = loser + Math.Max(1, margin);
            (aScore, bScore) = w.Id == c.A.Id ? (win, loser) : (loser, win);
        }
        Record(round, c, aScore, bScore, mercy, Involves(c));
        return (aScore, bScore);
    }

    private void Record(int round, Card c, int aScore, int bScore, bool mercy, bool involvesManager)
    {
        _results[(round, c.Index)] = new PlayedGame(aScore, bScore, mercy);
        var aWon = aScore > bScore;
        var w = aWon ? c.A : c.B;
        var l = aWon ? c.B : c.A;
        _matches.Add(new BracketMatch(RoundNameFor(round), LeagueRoundsRemaining, w.Name, l.Name, w.Id, l.Id,
            Math.Max(aScore, bScore), Math.Min(aScore, bScore), involvesManager, mercy));
    }

    private void RecordOutings(GameResult r, School away, School home, int matchDay)
    {
        RecordSide(r.AwayPitching, away.Id, matchDay);
        RecordSide(r.HomePitching, home.Id, matchDay);
    }

    private void RecordSide(IReadOnlyList<PitchingLine> lines, int schoolId, int matchDay)
    {
        var isManagerSchool = schoolId == _managerId;
        foreach (var l in lines)
            _pitchLedger.Record(PitcherLedgerKey.For(isManagerSchool, schoolId, l.SourceId, l.UniformNumber), l.Pitches, matchDay);
    }

    private void ConsumeLegacyManagerCardRng(School a, School b)
    {
        if (_backgroundResolver is not null) return;
        var (_, _, _) = AggregateMatch.PlayDetailed(a, b, _coeff, _rng);
        _rng.NextInt(0, 4);
    }

    private bool Involves(Card c) => c.A.Id == _managerId || c.B.Id == _managerId;

    /// <summary>第 round 節（0基点）の全カード（ブロック順→ブロック内の組み順）。添字は節の中で0から通し。</summary>
    private List<Card> CardsOf(int round)
    {
        var cards = new List<Card>();
        for (var b = 0; b < _blocks.Count; b++)
        {
            if (round >= _pairings[b].Count) continue;
            foreach (var (i, j) in _pairings[b][round]) cards.Add(new Card(cards.Count, b, _blocks[b][i], _blocks[b][j]));
        }
        return cards;
    }

    private IEnumerable<(int Round, Card Card)> CardsOfBlock(int block)
    {
        for (var r = 0; r < _totalRounds; r++)
            foreach (var c in CardsOf(r))
                if (c.Block == block) yield return (r, c);
    }

    /// <summary>リーグ戦は地区予選＝コールドあり（勝ち抜き戦の決勝以外と同じ）。</summary>
    private const bool MercyRuleEnabled = true;

    private static string RoundNameFor(int round) => "リーグ第" + (round + 1) + "戦";

    private static ulong PlayerStream(int round, int card, int attempt = 0)
        => 0xA7A7_0000UL ^ ((ulong)(uint)round << 16) ^ (uint)card ^ ((ulong)(uint)attempt << 40);

    private static ulong BackgroundStream(int round, int card, int attempt = 0)
        => 0xB7B7_0000UL ^ ((ulong)(uint)round << 16) ^ (uint)card ^ ((ulong)(uint)attempt << 40);

    // ===== セーブ往復 =====

    public bool CanCaptureState => _pending is null;

    public TournamentRunnerState CaptureState()
    {
        if (!CanCaptureState)
            throw new InvalidOperationException("観戦中の自校戦がある間は保存できません（issue #565 F2）。");
        var league = new LeagueRunnerState
        {
            AdvancePerBlock = _advancePerBlock,
            Spectated = _managerId == NoManagerId,
            Blocks = _blocks.Select(b => b.Select(s => s.Id).ToList()).ToList(),
        };
        foreach (var kv in _results.OrderBy(k => k.Key.Round).ThenBy(k => k.Key.Card))
            league.Games.Add(new SavedLeagueGame(kv.Key.Round, kv.Key.Card, kv.Value.AScore, kv.Value.BScore, kv.Value.MercyEnded));
        return new TournamentRunnerState
        {
            Title = Title,
            PlayerActive = PlayerActive,
            RoundIndex = _roundIndex,
            RngState = ((Xoshiro256Random)_rng).CaptureState(),
            Matches = new List<BracketMatch>(_matches),
            PitchLedger = _pitchLedger.CaptureState(),
            League = league,
        };
    }

    /// <summary>セーブから組み直す（<see cref="TournamentRunner.FromSaved"/> と同じ流儀＝係数・日程・resolver は渡し直す）。</summary>
    public static BlockLeagueRunner FromSaved(
        TournamentRunnerState state, IReadOnlyList<School> field, School manager,
        NationCoefficients coeff, TournamentSchedule schedule,
        IPlayerMatchResolver? playerResolver = null, IBackgroundMatchResolver? backgroundResolver = null,
        Action<int, int, GameResult>? onBackgroundMatch = null)
    {
        var league = state.League ?? throw new ArgumentException("リーグ戦の状態がありません。");
        var byId = new Dictionary<int, School> { [manager.Id] = manager };
        foreach (var s in field) byId[s.Id] = s;
        var blocks = league.Blocks.Select(ids => (IReadOnlyList<School>)ids.Select(id =>
            byId.TryGetValue(id, out var s) ? s : throw new ArgumentException($"セーブの学校 Id={id} が出場校に見つかりません。")).ToList()).ToList();
        return new BlockLeagueRunner(state, blocks, league, league.Spectated ? NoManagerId : manager.Id,
            coeff, schedule, playerResolver, backgroundResolver, onBackgroundMatch);
    }

    private BlockLeagueRunner(
        TournamentRunnerState state, IReadOnlyList<IReadOnlyList<School>> blocks, LeagueRunnerState league, int managerId,
        NationCoefficients coeff, TournamentSchedule schedule,
        IPlayerMatchResolver? playerResolver, IBackgroundMatchResolver? backgroundResolver,
        Action<int, int, GameResult>? onBackgroundMatch)
    {
        _coeff = coeff;
        _rng = Xoshiro256Random.FromState(state.RngState);
        _schedule = schedule;
        _playerResolver = playerResolver;
        _backgroundResolver = backgroundResolver;
        _onBackgroundMatch = onBackgroundMatch;
        _managerId = managerId;
        _advancePerBlock = Math.Max(1, league.AdvancePerBlock);
        Title = state.Title;
        _blocks = blocks.Select(b => b.ToList()).ToList();
        _pairings = _blocks.Select(b => BlockDraw.RoundRobinRounds(b.Count)).ToList();
        _totalRounds = _pairings.Count == 0 ? 0 : _pairings.Max(p => p.Count);
        _fieldSize = _blocks.Sum(b => b.Count);
        _roundIndex = state.RoundIndex;
        PlayerActive = state.PlayerActive;
        foreach (var g in league.Games) _results[(g.Round, g.Card)] = new PlayedGame(g.AScore, g.BScore, g.MercyEnded);
        _matches.AddRange(state.Matches);
        _pitchLedger.RestoreState(state.PitchLedger);
        // 次戦相手は状態から導く（AdvanceUntilPlayerMatch は呼ばない＝消化済みの節を解き直さない）
        if (PlayerActive && _roundIndex < _totalRounds)
        {
            var mine = CardsOf(_roundIndex).FirstOrDefault(Involves);
            _nextOpponent = mine is null ? null : mine.A.Id == _managerId ? mine.B : mine.A;
        }
    }
}
