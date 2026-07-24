using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>1試合の結果（ボックススコア要約＋速報記録）。</summary>
public sealed record GameResult
{
    public required string AwayName { get; init; }
    public required string HomeName { get; init; }
    public required int AwayRuns { get; init; }
    public required int HomeRuns { get; init; }
    public required int InningsPlayed { get; init; }
    /// <summary>コールドゲーム（マーシールール）成立で打ち切られたか（設計書05 §1.3, OPEN-QUESTIONS Q18）。</summary>
    public bool MercyEnded { get; init; }
    public required int TotalPitches { get; init; }
    public required int PitcherChanges { get; init; }
    /// <summary>選手交代の回数（代打・代走・守備固め・DH解除。設計書09 §6, 無指示/控え空なら0）。</summary>
    public int AwaySubstitutions { get; init; }
    public int HomeSubstitutions { get; init; }
    /// <summary>本塁クロスプレーで刺された走者の総数（両軍計。バックホーム憤死, 設計書12 §3 F2。統計参考値）。</summary>
    public int HomePlayOuts { get; init; }
    /// <summary>単打の一塁→三塁レースで三塁憤死した走者の総数（両軍計。Issue #89, 設計書12 §3.5。統計参考値）。</summary>
    public int ThirdPlayOuts { get; init; }

    // ===== design-14 第1段（P1）新プレー発生数（両軍計。統計参考値） =====
    public int FieldersChoiceCount { get; init; }
    public int DroppedThirdStrikeCount { get; init; }
    public int ErrorExtraAdvanceCount { get; init; }
    public int PickoffCount { get; init; }
    public int IntentionalWalkCount { get; init; }
    public int DoubleStealThirdBreakCount { get; init; }
    public int WildPitchCount { get; init; }

    /// <summary>イニング別得点（先攻/後攻）。要素数はチームが攻撃したイニング数。</summary>
    public IReadOnlyList<int> AwayLineScore { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> HomeLineScore { get; init; } = Array.Empty<int>();
    public int AwayHits { get; init; }
    public int HomeHits { get; init; }
    public int AwayErrors { get; init; }
    public int HomeErrors { get; init; }

    /// <summary>打席ごとのプレー記録（テキスト速報用）。</summary>
    public IReadOnlyList<PlayLogEntry> Log { get; init; } = Array.Empty<PlayLogEntry>();

    /// <summary>個人打撃成績（打順順9人）。</summary>
    public IReadOnlyList<BattingLine> AwayBatting { get; init; } = Array.Empty<BattingLine>();
    public IReadOnlyList<BattingLine> HomeBatting { get; init; } = Array.Empty<BattingLine>();
    /// <summary>個人投手成績（登板順）。</summary>
    public IReadOnlyList<PitchingLine> AwayPitching { get; init; } = Array.Empty<PitchingLine>();
    public IReadOnlyList<PitchingLine> HomePitching { get; init; } = Array.Empty<PitchingLine>();
    /// <summary>個人守備成績（失策があった選手のみ。issue #91）。合計は AwayErrors/HomeErrors と一致する。</summary>
    public IReadOnlyList<FieldingLine> AwayFielding { get; init; } = Array.Empty<FieldingLine>();
    public IReadOnlyList<FieldingLine> HomeFielding { get; init; } = Array.Empty<FieldingLine>();

    /// <summary>采配の集計（盗塁・犠打・スクイズ。無指示＝全ゼロ）。設計書09/11。</summary>
    public TacticsTally AwayTactics { get; init; } = new();
    public TacticsTally HomeTactics { get; init; } = new();

    /// <summary>
    /// 試合中に発生した怪我（設計書03 §3.5, 両軍・発生順）。観測データで試合結果には影響しない。
    /// 自校分は試合後に <see cref="Season.MatchInjuryLedger"/> がロスターへ反映する。
    /// </summary>
    public IReadOnlyList<MatchInjuryEvent> Injuries { get; init; } = Array.Empty<MatchInjuryEvent>();

    /// <summary>
    /// デバッグの強制発動（設計書17 §6.1, F4）を1回でも使ったか。真の試合は<b>結果が人為的</b>なので、
    /// 決定論ゲート（digest）にも統計集計にも入れてはいけない。既定 false＝通常の試合。
    /// </summary>
    public bool HasForcedOutcomes { get; init; }

    /// <summary>注入シナリオid（設計書17 §3.4）。非nullなら開始状態が baseline と違う＝digest対象外。</summary>
    public string? ScenarioId { get; init; }

    public bool HomeWon => HomeRuns > AwayRuns;
    public bool Tied => HomeRuns == AwayRuns;
    public int TotalRuns => AwayRuns + HomeRuns;
    public int RunDifferential => Math.Abs(HomeRuns - AwayRuns);
}

/// <summary>1チームの采配集計（設計書09/11。記録のみ＝試合結果に影響しない）。</summary>
public sealed record TacticsTally(
    int StealAttempts = 0, int StealSuccesses = 0, int SacrificeBunts = 0, int SacrificeBuntSuccesses = 0,
    int Squeezes = 0);

/// <summary>
/// 試合エンジン（設計書01 §3）。イニング進行・走塁・得点・継投・疲労・采配（設計書09）を統括する。
/// 9イニング標準、同点なら延長（上限あり）、後攻勝ち越しでサヨナラ終了。決定論。
/// Team.Tactics が null（無指示）の間はサイン・指示の分岐を一切通らず、従来の挙動・統計帯と完全一致する。
/// </summary>
public static class GameEngine
{
    // ── バッチ実行（従来API）。全打席を一括で消化する。統計・バランス・裏試合の監督戦はこれ。 ──
    // 内部は Steps(...) を drain するだけ＝対話進行と単一コードパス（決定論ゲートで前後一致を固定）。
    public static GameResult Play(
        Team awayTeam, Team homeTeam, GameContext ctx, IRandomSource rng,
        IReadOnlyDictionary<Player, int>? priorWeekPitches = null)
    {
        var p = NewProgress(awayTeam, homeTeam, ctx, rng, priorWeekPitches);
        foreach (var _ in Steps(p)) { /* drain */ }
        return BuildResult(p);
    }

    // ── ステップ実行（外部が1打席ずつ引く。采配を挟む対話進行の土台） ──

    /// <summary>
    /// 試合の進行状態を新規に用意する（TeamState 構築・球数予算・当日の出来 dayForm 抽選まで）。
    /// dayForm 抽選で rng を消費する順序は従来 Play と同一。以降の rng 消費は打席解決で起きる。
    /// </summary>
    /// <param name="scenarioStart">
    /// 場面ジャンプの開始局面（設計書17 §3.4, F2）。null=通常どおり1回表の頭から。
    /// </param>
    public static GameProgress NewProgress(
        Team awayTeam, Team homeTeam, GameContext ctx, IRandomSource rng,
        IReadOnlyDictionary<Player, int>? priorWeekPitches = null,
        Debugging.ScenarioStart? scenarioStart = null)
    {
        // 試合ごとの天候（気温）モデル（Issue #120）。専用Forkストリームで気温を1回引き、空気密度・投手消耗を
        // 派生させた ctx に差し替える。Fork は親 rng の状態を進めない＝以降の乱数順・決定論は不変。
        // Weather が null／無効なら ctx はそのまま（従来挙動と完全一致）。
        ctx = WeatherModel.ApplyForMatch(ctx, rng);

        // デバッグ観測（設計書17 §4, F1）。既定オフ＝この分岐に入らず従来と完全に同じ経路。
        // ヘッダは「まだ1回も引いていない乱数状態」を持つ必要があるので、dayForm 抽選より前に組む。
        Debugging.GameTraceHeader? traceHeader = null;
        if (ctx.TracingEnabled)
        {
            traceHeader = BuildTraceHeader(awayTeam, homeTeam, ctx, rng);
            // 値は素通しのデコレータ（設計書17 §4.5）。包んでも乱数列は1ビットも変わらない。
            rng = new Debugging.CountingRandomSource(rng);
            // AI校の思考の可視化（設計書17 §5 P4）。判断内訳の文字列を組ませるだけで判断は変えない。
            if (awayTeam.Tactics is IExplainTactics ax) ax.ExplainDecisions = true;
            if (homeTeam.Tactics is IExplainTactics hx) hx.ExplainDecisions = true;
        }

        var away = new TeamState(awayTeam);
        var home = new TeamState(homeTeam);
        // 球数制限（設計書05 §1.3）。既定 WeeklyPitchLimit=MaxValue＝無効＝従来の継投挙動と完全一致。
        away.SetWeeklyPitchBudget(ctx.WeeklyPitchLimit, priorWeekPitches);
        home.SetWeeklyPitchBudget(ctx.WeeklyPitchLimit, priorWeekPitches);

        // 試合限りの好不調（設計書02 §3.3b）: 試合開始時に全選手へ当日の揺らぎを付与。
        // 大半は微小・スパイクは十数試合に一度。事前に見えない（投げて/打って初めて分かる）。
        var dayForm = SampleDayForms(awayTeam, homeTeam, ctx, rng);

        var progress = new GameProgress(awayTeam, homeTeam, away, home, ctx, rng, dayForm);
        if (scenarioStart is not null) ApplyScenarioStart(progress, scenarioStart);
        if (traceHeader is not null) ctx.TraceSink!.OnGameStart(traceHeader);
        return progress;
    }

    /// <summary>
    /// 場面ジャンプ（設計書17 §3.4）の適用。スコア・打順・投手球数など「打席に入る前から決まっている状態」を
    /// ここで注ぐ。塁・アウト・カウントは半イニングのローカル状態なので <see cref="PlayHalfSteps"/> 側で消費する。
    /// </summary>
    private static void ApplyScenarioStart(GameProgress p, Debugging.ScenarioStart s)
    {
        s.Validate();
        p.Inning = s.Inning;
        p.Away.Runs = s.AwayScore;
        p.Home.Runs = s.HomeScore;

        // ラインスコアは「注入した点は初回に入った」ものとして埋める（開始局面の再現が目的で、
        // イニング別得点の内訳までは宣言していないため）。表示の桁だけ実イニング数に合わせる。
        SeedLineScore(p.Away, s.AwayScore, s.IsTop ? s.Inning - 1 : s.Inning);
        SeedLineScore(p.Home, s.HomeScore, s.Inning - 1);

        // 打順は攻撃側のみ動かす（守備側は次に攻撃へ回るとき1番から）。
        var offense = p.OffenseOf(s.IsTop);
        for (var i = 1; i < s.BatterOrder; i++) offense.NextBatter();

        if (s.PitcherFatiguePitches > 0) p.OffenseOf(!s.IsTop).AddPitches(s.PitcherFatiguePitches, 1.0);

        p.PendingScenarioStart = s;
        p.ScenarioSkipsFirstTop = !s.IsTop;
    }

    private static void SeedLineScore(TeamState t, int runs, int completedInnings)
    {
        for (var i = 0; i < completedInnings; i++) t.RecordInningRuns(i == 0 ? runs : 0);
    }

    /// <summary>観測ヘッダ（設計書17 §4.1）。再現に必要な最小情報（RNG状態・対戦カード指紋）を先頭に置く。</summary>
    private static Debugging.GameTraceHeader BuildTraceHeader(
        Team awayTeam, Team homeTeam, GameContext ctx, IRandomSource rng)
    {
        var raw = Debugging.CountingRandomSource.UnwrapAll(rng);
        var stateHex = "";
        if (raw is Xoshiro256Random xo)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var w in xo.CaptureState())
                sb.Append(w.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
            stateHex = sb.ToString();
        }
        return new Debugging.GameTraceHeader
        {
            AwayName = awayTeam.Name,
            HomeName = homeTeam.Name,
            RngStateHex = stateHex,
            FixtureFingerprint = Debugging.ReproToken.Fingerprint(awayTeam, homeTeam, ctx),
            ScenarioId = ctx.ScenarioId,
            RegulationInnings = ctx.RegulationInnings,
            TieBreakEnabled = ctx.TieBreakEnabled,
            MercyRuleEnabled = ctx.MercyRuleEnabled,
        };
    }

    /// <summary>観測レコード上の選手識別子。成績集計と同じ SourceId を優先し、無ければ名前。</summary>
    private static string TraceIdOf(Player p)
        => p.SourceId is { } id ? "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : p.Name;

    /// <summary>塁占有の短縮表記（"---" / "1-3" / "123"）。観測専用。</summary>
    private static string BaseOccupancy(bool first, bool second, bool third)
        => new string(new[] { first ? '1' : '-', second ? '2' : '-', third ? '3' : '-' });

    /// <summary>
    /// 打席境界ごとに <see cref="GameStep"/> を1つ yield する進行イテレータ（設計書09 の采配窓の土台）。
    /// バッチ Play はこれを drain するだけ。yield は乱数を消費しないので、drain 結果は従来 Play と一致する。
    /// 将来は打席途中（投球単位のサイン・伝令）へ境界を細分化できるよう GameStep を拡張する。
    /// </summary>
    public static IEnumerable<GameStep> Steps(GameProgress p)
    {
        var ctx = p.Ctx;
        for (; ; p.Inning++)
        {
            // 延長は伝令が1イニングごとに1回追加される（設計書09 §3, 現実ルール準拠）。
            if (p.Inning > ctx.RegulationInnings)
            {
                p.Away.GrantExtraInningTimeouts();
                p.Home.GrantExtraInningTimeouts();
            }

            // 表: 先攻(away)攻撃 / 後攻(home)守備
            // 場面ジャンプで「裏から始める」と宣言されたときだけ、最初の1回に限り表を飛ばす（設計書17 §3.4）。
            if (p.ScenarioSkipsFirstTop) p.ScenarioSkipsFirstTop = false;
            else foreach (var s in PlayHalfSteps(p, p.Away, p.Home, walkoff: null, isTop: true)) yield return s;

            // 最終回以降、後攻が既にリードなら裏の攻撃はしない。
            if (p.Inning >= ctx.RegulationInnings && p.Home.Runs > p.Away.Runs) break;

            // 裏: 後攻(home)攻撃 / 先攻(away)守備。最終回以降はサヨナラ判定。
            var allowWalkoff = p.Inning >= ctx.RegulationInnings;
            foreach (var s in PlayHalfSteps(p, p.Home, p.Away,
                walkoff: allowWalkoff ? () => p.Home.Runs > p.Away.Runs : null, isTop: false)) yield return s;

            // 規定回以降で決着していれば終了。
            if (p.Inning >= ctx.RegulationInnings && p.Home.Runs != p.Away.Runs) break;
            // 延長上限（引き分け許容）。
            if (p.Inning >= ctx.MaxInnings) break;
            if (ctx.MercyRuleEnabled && IsMercy(p.Inning, p.Away.Runs, p.Home.Runs))
            {
                p.MercyEnded = true;
                break;
            }
        }
    }

    /// <summary>進行状態から最終 GameResult を組む（バッチ drain 後・対話完走後のどちらでも使える）。</summary>
    public static GameResult BuildResult(GameProgress p)
    {
        var result = BuildResultCore(p);
        // 観測の終端（設計書17 §4.2）。BuildResult は複数回呼ばれうるので一度だけ流す。
        if (p.Ctx.TracingEnabled && !p.TraceEndEmitted)
        {
            p.TraceEndEmitted = true;
            p.Ctx.TraceSink!.OnGameEnd(result);
        }
        return result;
    }

    private static GameResult BuildResultCore(GameProgress p)
    {
        var away = p.Away;
        var home = p.Home;
        return new GameResult
        {
            AwayName = p.AwayTeam.Name,
            HomeName = p.HomeTeam.Name,
            AwayRuns = away.Runs,
            HomeRuns = home.Runs,
            InningsPlayed = p.Inning,
            MercyEnded = p.MercyEnded,
            TotalPitches = p.TotalPitches,
            PitcherChanges = away.PitcherChanges + home.PitcherChanges,
            AwaySubstitutions = away.Substitutions,
            HomeSubstitutions = home.Substitutions,
            HomePlayOuts = away.HomePlayOuts + home.HomePlayOuts,
            ThirdPlayOuts = away.ThirdPlayOuts + home.ThirdPlayOuts,
            FieldersChoiceCount = away.FieldersChoiceCount + home.FieldersChoiceCount,
            DroppedThirdStrikeCount = away.DroppedThirdStrikeCount + home.DroppedThirdStrikeCount,
            ErrorExtraAdvanceCount = away.ErrorExtraAdvanceCount + home.ErrorExtraAdvanceCount,
            PickoffCount = away.PickoffCount + home.PickoffCount,
            IntentionalWalkCount = away.IntentionalWalkCount + home.IntentionalWalkCount,
            DoubleStealThirdBreakCount = away.DoubleStealThirdBreakCount + home.DoubleStealThirdBreakCount,
            WildPitchCount = away.WildPitchCount + home.WildPitchCount,
            AwayLineScore = away.InningRuns,
            HomeLineScore = home.InningRuns,
            AwayHits = away.Hits,
            HomeHits = home.Hits,
            AwayErrors = away.Errors,
            HomeErrors = home.Errors,
            Log = p.Log,
            AwayBatting = away.BuildBattingLines(),
            HomeBatting = home.BuildBattingLines(),
            AwayPitching = away.BuildPitchingLines(),
            HomePitching = home.BuildPitchingLines(),
            AwayFielding = away.BuildFieldingLines(),
            HomeFielding = home.BuildFieldingLines(),
            AwayTactics = TallyOf(away),
            HomeTactics = TallyOf(home),
            Injuries = p.Injuries,
            // デバッグ注入の痕跡（設計書17 §3.4/§6.1）。どちらかが立った試合は digest・統計集計から外す。
            HasForcedOutcomes = p.ForcedCount > 0,
            ScenarioId = p.Ctx.ScenarioId,
        };
    }

    private static TacticsTally TallyOf(TeamState t)
        => new(t.StealAttempts, t.StealSuccesses, t.SacrificeBunts, t.SacrificeBuntSuccesses, t.Squeezes);

    /// <summary>両チーム全選手（打順＋ブルペン）の当日の出来をサンプリング（順序固定＝決定論）。</summary>
    private static Dictionary<Player, double> SampleDayForms(
        Team awayTeam, Team homeTeam, GameContext ctx, IRandomSource rng)
    {
        var map = new Dictionary<Player, double>();
        // ムラっけ（設計書10）は当日の出来の振れ幅を拡大。スキルなしは倍率1.0で従来と同一。
        double Sample(Player p) => Players.FormModel.SampleDayForm(
            rng, ctx.Form, SkillModel.DayFormVarianceFactor(p.Skills, ctx.Skills));

        foreach (var team in new[] { awayTeam, homeTeam })
        {
            foreach (var p in team.BattingOrder) map[p] = Sample(p);
            // DH制の先発投手は打順外（設計書09 §6）なので個別にサンプリング。
            if (team.UsesDh && team.StartingPitcher is not null)
            {
                map[team.StartingPitcher] = Sample(team.StartingPitcher);
            }
            foreach (var p in team.Bullpen) map[p] = Sample(p);
        }
        return map;
    }

    private static IEnumerable<GameStep> PlayHalfSteps(
        GameProgress p, TeamState offense, TeamState defense, Func<bool>? walkoff, bool isTop)
    {
        // 従来 PlayHalf の本体をそのまま踏襲（yield 挿入と totalPitches の状態化のみ）。エイリアスで差分最小化。
        var ctx = p.Ctx;
        var rng = p.Rng;
        var log = p.Log;
        var dayForm = p.DayForm;
        var inning = p.Inning; // 半イニングは単一イニング内で完結（p.Inning は Steps が進める）

        var bases = new BaseState();
        var outs = 0;
        var runsThisHalf = 0;
        // 自責点（issue #69）: 失策で出塁 or 失策連鎖で延命した走者を集合で追跡し、生還時に自責から除外する。
        // 半イニング内で使い切り（次の半イニングは新しい集合）。簡易規則の詳細は design-14/PitchingStatLine 参照。
        var unearnedRunners = new HashSet<Player>();

        // デバッグ観測（設計書17 §4）。既定 null＝以降の観測分岐はすべて素通り＝従来と完全一致。
        var traceSink = p.TraceSink;
        var rngStats = p.RngStats;
        // 強制発動（設計書17 §6.1）がこの打席で効いたか（観測レコードに刻む）。Pa() から読むためループ外に置く。
        var forcedThisPa = false;

        // 完了した打席の境界を通知するヘルパ（将来は投球単位へ細分化）。log.Count-1＝直前に追記した打席。
        // 併せて局面スナップショット（塁・アウト・表裏）を GameProgress へ載せる＝対話UIの交代seam（観測のみ）。
        GameStep Pa()
        {
            p.CurrentBases = bases;
            p.CurrentOuts = outs;
            p.CurrentIsTop = isTop;
            // 打席の観測レコード（設計書17 §4.1）。この時点で outs は当該打席ぶんを加算済み。
            if (traceSink is not null && log.Count > 0)
            {
                var last = log[log.Count - 1];
                traceSink.OnPlateAppearance(new Debugging.PaTrace
                {
                    Inning = last.Inning,
                    IsTop = last.IsTop,
                    BatterId = last.BatterSourceId is { } bid
                        ? "#" + bid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : last.BatterName,
                    Result = last.Result,
                    Rbi = last.RunsScored,
                    OutsAfter = outs,
                    Pitches = last.Pitches,
                    RunnerSummary = BaseOccupancy(last.BaseFirstAfter, last.BaseSecondAfter, last.BaseThirdAfter),
                    Forced = forcedThisPa,
                });
            }
            return new(GameStepKind.PlateAppearance, inning, isTop, log.Count - 1);
        }

        // 1球ごとの采配窓（設計書15 §2.2）。各投球の「前」に yield する。LogIndex はこの打席が確定したら
        // 載る添字（log.Count）。乱数は消費しないので挟んでも結果は不変。Phase A では消費側が読み飛ばす。
        GameStep Pitch() => new(GameStepKind.Pitch, inning, isTop, log.Count);

        // イニング頭は投手が落ち着いて入り直す（動揺はイニングを跨がない）。
        defense.ClearRattled();

        // タイブレーク（設計書09 §7）: 無死一・二塁＋前打席からの継続打順で開始。
        var tieBreak = ctx.TieBreakEnabled && inning >= ctx.TieBreakStartInning;
        if (tieBreak)
        {
            bases.Second = offense.PreviousBatter(2);
            bases.First = offense.PreviousBatter(1);
        }

        // 場面ジャンプ（設計書17 §3.4）: 最初の半イニングだけ、宣言された塁・アウト・カウントから始める。
        // タイブレークより後に置く＝両方指定されたらシナリオの宣言が勝つ（デバッグの意図が最優先）。
        var pendingBalls = 0;
        var pendingStrikes = 0;
        if (p.ConsumeScenarioStart() is { } seeded)
        {
            outs = seeded.Outs;
            // 走者はタイブレークと同じ流儀で「直前の打者たち」を置く（誰を置くかを宣言せずに済ませる割り切り）。
            bases.Third = seeded.OnThird ? offense.PreviousBatter(3) : null;
            bases.Second = seeded.OnSecond ? offense.PreviousBatter(2) : null;
            bases.First = seeded.OnFirst ? offense.PreviousBatter(1) : null;
            pendingBalls = seeded.Balls;
            pendingStrikes = seeded.Strikes;
        }

        // 守備固め（設計書09 §6）: イニング頭に守備側が控えを起用。控え空/無指示なら素通り＝従来一致。
        MaybeDefensiveSub(defense, offense, ctx, rng, inning, bases);

        while (outs < 3)
        {
            // 選手交代（設計書09 §6, 攻撃側）: 代走→代打。控え空/無指示なら素通り＝従来と完全一致。
            MaybeOffenseSubs(offense, defense, ctx, rng, inning, outs, bases);

            // スモールボール暫定ヒューリスティック（既定オフ）。采配Brain設定時はサインが置き換える。
            if (ctx.EnableSmallBall && offense.Tactics is null)
            {
                outs += TryStealAttempt(bases, offense, defense, ctx, rng, outs);
                if (outs >= 3) break;
            }

            var batter = offense.PeekBatter();
            var batterOrder = offense.CurrentBatterOrder; // ライブ観戦のスタメン列ハイライト用（打順消費前に採取）
            var currentPitcher = defense.CurrentPitcher; // 成績計上用の投手アイデンティティ

            // 試合中の受傷（設計書03 §3.5・issue #29 B）。判定はすべて Fork した専用ストリームなので
            // 本体の乱数順・試合結果は不変。この打席で起きた分をタイムラインのキャプションにも載せる。
            List<MatchInjuryEvent>? paInjuries = null;
            void Hurt(MatchInjuryEvent? e)
            {
                if (e is null) return;
                p.Injuries.Add(e);
                (paInjuries ??= new List<MatchInjuryEvent>()).Add(e);
            }

            // 強制発動（設計書17 §6.1, F4）: 予約があればこの打席で一度だけ効く。既定 None＝以降すべて素通り。
            //  - 打席結果を固定する種別 → AtBatSession が投球ループごとスキップする
            //  - 稀プレー種別        → 該当ゲートの確率を1.0にした1打席コピーの走塁係数で解く
            var force = p.ConsumeForcedOutcome();
            forcedThisPa = force != Debugging.ForcedOutcome.None;
            var paBaserunning = forcedThisPa
                ? Debugging.ForcedOutcomes.Apply(ctx.Baserunning, force)
                : ctx.Baserunning;
            var forcedPaResult = Debugging.ForcedOutcomes.ToPlateAppearance(force);

            // プレッシャー指数（設計書02 §3）: 状況から自動算出。采配判断にも渡す。
            var pressureIdx = PressureModel.Compute(new PressureSituation(
                ctx.PressureStageBonus, inning, Math.Abs(offense.Runs - defense.Runs),
                bases.Second is not null, bases.Third is not null,
                bases.RunnerCount == 3, ctx.RetirementOnLine), ctx.Pressure);

            // 采配（設計書09）: 攻守の監督判断。無指示（Brain=null）なら分岐ごと素通り＝従来と完全一致。
            var sign = OffensiveSign.Swing;
            var defTactics = DefensiveTactics.Default;
            // 打席頭のスナップショット。1球采配（設計書15 §2.3）の Base としても使う。打撃・スコア等は
            // 打席内で不変だが、塁上/アウト数は盗塁の毎球判定（設計書15 Phase D-2d）により打席途中で
            // 変わりうるため、毎球ループの先頭（下）で都度作り直す。
            // Brain が両方nullなら未構築のまま＝以降 IPitchTacticsBrain 判定も必ずfalseなので参照されない。
            var situation = default(TacticsSituation);
            if (offense.Tactics is not null || defense.Tactics is not null)
            {
                situation = new TacticsSituation(
                    inning, ctx.RegulationInnings, outs, offense.Runs - defense.Runs,
                    bases.First, bases.Second, bases.Third, batter, currentPitcher, defense.Catcher,
                    pressureIdx, defense.PitcherRattled,
                    offense.OffenseTimeoutsLeft, defense.DefenseTimeoutsLeft, tieBreak);

                if (defense.Tactics is not null)
                {
                    defTactics = defense.Tactics.CallDefense(situation, rng);
                    // 守備伝令（マウンドへ, §3）: 動揺を解除し、負補正を数打席のあいだ緩和。
                    if (defense.Tactics.CallDefenseTimeout(situation, rng) && defense.TryUseDefenseTimeout())
                    {
                        defense.ClearRattled();
                        defense.StartDefenseCalm(ctx.Tactics.TimeoutDurationPa);
                    }
                }

                if (offense.Tactics is not null)
                {
                    // 攻撃伝令（打者・走者へ, §3）: 打者の緊張を数打席のあいだ緩和。
                    if (offense.Tactics.CallOffenseTimeout(situation, rng) && offense.TryUseOffenseTimeout())
                    {
                        offense.StartOffenseCalm(ctx.Tactics.TimeoutDurationPa);
                    }
                    sign = offense.Tactics.CallOffense(situation, rng);
                }
            }

            // 敬遠（design-14 P1-3・§2.5-a）: 決まっていれば攻撃側のサイン（バント/スクイズ等）は
            // 一切発動しない（打席解決全体が無条件四球に置き換わるため）。CallOffense 自体の
            // RNG消費は温存し、その後続処理だけを打たない＝統一ステッパ化後も従来の乱数順と一致させる。
            if (defTactics.IntentionalWalk) sign = OffensiveSign.Swing;

            // 実効能力の構築（補正係数はすべて物理層変換の前段で適用＝パイプライン不変）。
            var gearWeight = defTactics.Gear switch
            {
                PitcherGear.Push => ctx.Pitching.GearPushStaminaFactor,
                PitcherGear.Coast => ctx.Pitching.GearCoastStaminaFactor,
                _ => 1.0,
            };
            // チーム別疲労係数（issue #55, 監督傾向, 決定4: B-1）。傾向なしのチームは null＝ctx.Fatigue に落ちる
            // （＝従来と完全一致・帯不変）。減衰カーブ（VelocityDrop/ControlDrop）は共通なので、エース酷使でも
            // 実効能力の算出結果は ctx.Fatigue と同値＝変わるのは継投「時期」（ShouldRelieve）だけ。
            var fatigue = defense.Fatigue ?? ctx.Fatigue;
            var pitcher = PitchingFatigue.Effective(currentPitcher, defense.FatiguePitches, fatigue);
            // 調子・当日の出来（設計書02 §3.3/§3.3b）。
            pitcher = Players.FormModel.ApplyPitcher(pitcher, currentPitcher.Condition, FormOf(dayForm, currentPitcher), ctx.Form);
            var batterAttrs = Players.FormModel.ApplyBatter(batter.ToBatter(), batter.Condition, FormOf(dayForm, batter), ctx.Form);

            // スキル（設計書10）: 尻上がり/打者一巡は試合内の登板・打席累積で効く。行動特性・球質は playMods へ。
            var priorPa = offense.PriorPlateAppearances(batter);
            var priorBf = defense.PriorBattersFaced(currentPitcher);
            batterAttrs = SkillModel.ApplyBatter(batterAttrs, batter.Skills, priorPa, ctx.Skills);
            pitcher = SkillModel.ApplyPitcher(pitcher, currentPitcher.Skills, priorBf, ctx.Skills);
            var playMods = SkillModel.PlayMods(batter.Skills, currentPitcher.Skills, priorBf, ctx.Skills);

            // プレッシャー×精神力（設計書02 §3）: 緩和=主将＋伝令、負側増幅=疲労・動揺。精神力50なら恒等。
            var pitcherFatigueOver = defense.FatiguePitches > (currentPitcher.Pitching?.StaminaPitches ?? 90.0);
            pitcher = PressureModel.ApplyPitcher(pitcher, PressureModel.Multiplier(
                currentPitcher.Mental, pressureIdx, ctx.Pressure,
                defense.MitigationFor(offense: false, ctx.Tactics, ctx.Skills), pitcherFatigueOver,
                defense.PitcherRattled ? ctx.Tactics.RattledNegativeAmplify : 1.0));
            batterAttrs = PressureModel.ApplyBatter(batterAttrs, PressureModel.Multiplier(
                batter.Mental, pressureIdx, ctx.Pressure,
                offense.MitigationFor(offense: true, ctx.Tactics, ctx.Skills)));

            // --- バントサイン（設計書02 §4.3・設計書15 Phase D-2b）: AtBatSession の投球ループへ統一。
            // 2ストライクに達するまで毎球バント試行を上書きし、決着しなければ実カウントを保ったまま
            // 強攻へ切り替わる（旧: カウントを0-0にリセットして別解決していたのを解消）。
            var buntSign = sign is OffensiveSign.SacrificeBunt or OffensiveSign.SafetyBunt && bases.RunnerCount > 0
                ? sign : (OffensiveSign?)null;
            // バント/敬遠のいずれも、以降のヒットエンドラン/バスター判定・得点圏補正には関与しない
            // （実際に強攻へ落ち着くのは投球ループ内で確定するが、sign の見た目はここで前倒しして揃える）。
            if (buntSign is not null) sign = OffensiveSign.Swing;

            // --- スクイズサイン（設計書02 §4.4・設計書09 §1・設計書15 Phase D-2c）: AtBatSession の投球ループへ
            // 統一。ウエスト読み合いは1球で即決着するため、この上書きは最初の1球にしか渡さない（下の1球采配ブロック）。
            // 結果判定は従来通り SqueezeResolver のまま（ここでは前倒しでウエスト確率だけ計算する）。
            var squeezeSign = sign == OffensiveSign.Squeeze && bases.Third is not null ? sign : (OffensiveSign?)null;
            var squeezeThirdRunner = bases.Third;
            var squeezeWaste = squeezeSign is null ? 0.0 : MathUtil.Clamp(
                ctx.Tactics.SqueezeReadBase
                + (defense.Catcher.Fielding - 50) * ctx.Tactics.SqueezeReadPerLead
                + (outs == 1 && Math.Abs(offense.Runs - defense.Runs) <= 1
                    ? ctx.Tactics.SqueezeReadObviousBonus : 0.0),
                0.0, 0.60);
            if (squeezeSign is not null)
            {
                offense.RecordSqueeze(); // 試行の記録は結果に関わらず一度だけ（旧: SqueezeResolver呼び出し直後と同じ扱い）。
                sign = OffensiveSign.Swing;
            }

            // --- エンドラン/バスター: 打撃を補正係数で調整（設計書09 §1。パイプライン不変） ---
            var hitAndRun = sign == OffensiveSign.HitAndRun && bases.First is not null;
            // 一塁走者のスタート種別（設計書12 §4, Q10決定）: エンドランは接触と同時に飛び出すコンタクト始動。
            // 空振りは既存のキャッチャー捕殺判定(下)、打球が空中で捕られればライナー併殺(G2)の対象になる。
            var r1Start = hitAndRun ? StartType.Contact : StartType.Normal;
            if (hitAndRun)
            {
                batterAttrs = batterAttrs with
                {
                    Contact = ClampAbility(batterAttrs.Contact * (1.0 + ctx.Tactics.HitAndRunContactBoost)),
                    Power = ClampAbility(batterAttrs.Power * (1.0 - ctx.Tactics.HitAndRunPowerPenalty)),
                    LaunchTendency = ClampAbility(batterAttrs.LaunchTendency * (1.0 - ctx.Tactics.HitAndRunLaunchPenalty)),
                };
            }
            else if (sign == OffensiveSign.Buster)
            {
                var f = defTactics.BuntShift
                    ? 1.0 + ctx.Tactics.BusterVsShiftBonus   // シフトの穴を突く
                    : 1.0 - ctx.Tactics.BusterPenalty;       // 準備不足
                batterAttrs = batterAttrs with { Contact = ClampAbility(batterAttrs.Contact * f) };
            }
            else if (sign == OffensiveSign.Swing && (bases.Second is not null || bases.Third is not null)
                     && batter.ChanceHitFactor != 1.0)
            {
                // 性格④（設計書01 §1.1）: 得点圏の強攻。目立ちたがりは長打質(Power)上振れ、自己犠牲はやや堅実。
                batterAttrs = batterAttrs with { Power = ClampAbility(batterAttrs.Power * batter.ChanceHitFactor) };
            }

            // 打順消費（設計書15 Phase D-2e）: 打席が実際に確定した時点まで遅らせる（下の squeezeAbandoned/
            // stealEndedHalf ガード直後）。ここでは進めない＝Peekのみでこの打者を握ったまま投球ループへ入る。

            // 守備指示→初期守備位置（陣形の効果は幾何＝物理から出る, §2.1）。
            var fielders = AlignmentTactics.Adjust(defense.DefensiveAlignment(ctx.Field), defTactics, ctx.Tactics);
            var directive = defTactics.Policy == PitchPolicy.Auto
                ? (PitchDirective?)null
                : ctx.Tactics.DirectiveFor(defTactics.Policy, batter.Bats);
            // 捕手リード（設計書01 §2①）: 良い配球で球威を引き出す。名捕手（設計書10）は実効リードを底上げ。
            var catcherLead = defense.Catcher.Lead
                + (defense.Catcher.Skills.Has(Skill.MasterCatcher) ? ctx.Skills.MasterCatcherLeadBonus : 0);
            var abCtx = ctx.ToAtBatContext(fielders, runnersOn: bases.RunnerCount > 0,
                gear: defTactics.Gear, directive: directive, takeFirstPitch: sign == OffensiveSign.Take,
                skills: playMods, catcherLead: catcherLead, intentionalWalk: defTactics.IntentionalWalk);

            // 通常打席の投球ループ（設計書15 §2.1）: AtBatSession を1球ずつ回し、各投球の前に采配窓を yield する。
            // 一括 ResolveDetailed と実装・RNG消費が完全一致（yield は乱数非消費）＝バッチ Play と対話進行は同一結果。
            var session = AtBatSession.Begin(batterAttrs, pitcher, abCtx, batterPlayer: batter,
                thirdBaseRunner: squeezeThirdRunner, squeezeWasteProbability: squeezeWaste,
                initialBalls: pendingBalls, initialStrikes: pendingStrikes,
                forcedResult: forcedPaResult);
            pendingBalls = pendingStrikes = 0; // 途中カウントは場面ジャンプ直後の1打席にだけ効く

            // 采配ラベル（観測専用・設計書17 §4.1）。sign はバント/スクイズ/敬遠で Swing へ畳まれた後なので、
            // 畳む前の別名（buntSign/squeezeSign/IntentionalWalk）から表示用に復元する。
            var paSignLabel = traceSink is null ? null
                : defTactics.IntentionalWalk ? "IntentionalWalk"
                : squeezeSign?.ToString() ?? buntSign?.ToString()
                  ?? (sign != OffensiveSign.Swing ? sign.ToString() : null);

            var squeezeAbandoned = false;
            var stealEndedHalf = false;
            while (!session.IsComplete)
            {
                yield return Pitch();

                // 観測（設計書17 §4）: この球の乱数消費を測るため、采配窓を抜けた直後の値を控える。
                var drawsAtPitchStart = rngStats?.Draws ?? 0;
                var tracesBeforePitch = session.PitchTraces.Count;
                PitchPolicy? tracePolicy = null;
                string? traceCandidates = null;

                // 塁上/アウト数は盗塁の毎球判定（下）で打席途中に変わりうるため、brain へ渡す前に
                // 毎球作り直す（打者・スコア差等その他のフィールドは打席内不変なので変えない）。
                if (offense.Tactics is not null || defense.Tactics is not null)
                {
                    situation = situation with
                    {
                        Outs = outs, ScoreDiff = offense.Runs - defense.Runs,
                        OnFirst = bases.First, OnSecond = bases.Second, OnThird = bases.Third,
                    };
                }

                // 1球采配（設計書15 §2.3, C-1 seam）: brain が IPitchTacticsBrain を実装している時だけ
                // 判断RNGをFork隔離して問い合わせる。非実装ならこの分岐自体に入らずRNGを1発も引かない
                // （no-opゲート）。1球指示は打席頭の方針を単純上書きし、次球は方針へ復帰する（Q12-3）。
                // バント方針（設計書15 Phase D-2b）: 2ストライク未満のあいだ既定として効く「方針」の扱い。
                // スクイズ（設計書15 Phase D-2c）: ウエスト読み合いは1球固定なので最初の1球にしか渡さない。
                // brain/手動の1球指示（下）が常に優先し、2ストライク到達後は既定に戻らず自動的に強攻へ。
                PitchBattingOverride? battingOverride = squeezeSign is not null && session.PitchCount == 0
                    ? PitchBattingOverride.Squeeze
                    : buntSign switch
                    {
                        OffensiveSign.SafetyBunt when session.Strikes < 2 => PitchBattingOverride.SafetyBunt,
                        OffensiveSign.SacrificeBunt when session.Strikes < 2 => PitchBattingOverride.Bunt,
                        _ => null,
                    };
                Tactics.PitchDirective? pitchOverride = null;
                PitcherGear? gearOverride = null;
                // 盗塁を試みるか＋始動種別＋狙う塁（設計書15 Phase D-2d／issue #67で三盗・本盗へ拡張）。
                // 旧来は打席頭で一度きりだったが、毎球の独立試行へ置き換えたため任意の球の前で発動しうる
                // （解決式は StealResolver 等の従来のまま、下の盗塁ブロックで解決する）。
                StartType? stealAttempt = null;
                var stealTarget = StealTarget.Second;
                if (offense.Tactics is IPitchTacticsBrain offensePitchBrain)
                {
                    var pSituation = new PitchTacticsSituation(
                        situation, session.Balls, session.Strikes, session.PitchCount, session.LastPitchKind);
                    var d = offensePitchBrain.CallPitchAction(
                        pSituation, rng.Fork(PitchStreamId(inning, isTop, log.Count, session.PitchCount, 0xB47_0000UL)));
                    // brain が何も言わなければ（Batting=null）方針側の既定（バント等, 設計書15 Phase D-2b）を
                    // 保つ。以前は battingOverride の既定が常にnullだったため無条件代入でも無害だったが、
                    // バント方針という非null既定が加わったことで「上書きは値がある時だけ」に変更が必要になった。
                    if (d?.Batting is { } brainBatting) battingOverride = brainBatting;
                    stealAttempt = d?.StealAttempt;
                    stealTarget = d?.StealTarget ?? StealTarget.Second;
                    traceCandidates = d?.Explanation; // 観測専用（設計書17 §5 P4）。判断には使わない。
                }
                if (defense.Tactics is IPitchTacticsBrain defensePitchBrain)
                {
                    var pSituation = new PitchTacticsSituation(
                        situation, session.Balls, session.Strikes, session.PitchCount, session.LastPitchKind);
                    var d = defensePitchBrain.CallPitchAction(
                        pSituation, rng.Fork(PitchStreamId(inning, isTop, log.Count, session.PitchCount, 0xDEF5_0000UL)));
                    if (d?.Policy is { } policyOverride)
                    {
                        pitchOverride = ctx.Tactics.DirectiveFor(policyOverride, batter.Bats);
                        tracePolicy = policyOverride; // 観測専用
                    }
                    gearOverride = d?.Gear;
                }

                // 強制発動の走塁系（設計書17 §6.1, F4）: 采配Brainが無くても盗塁企図そのものを起こす。
                // 成否は下の StealResolver をスキップして固定する（PickoffOut/DoubleSteal はゲート確率1.0側で効く）。
                if (forcedThisPa && Debugging.ForcedOutcomes.ForcesStealAttempt(force) && session.PitchCount == 0
                    && bases.First is not null && bases.Second is null)
                {
                    stealAttempt ??= StartType.Normal;
                }

                // 盗塁/牽制/重盗（設計書02 §4.2・設計書09 §1・design-14 P1-4/P1-5・設計書15 Phase D-2d）:
                // カウントは消費せず（実球はこの後も通常どおり投げる）、この球の前に走者だけが動く。
                // PickoffResolver→StealReadModel.RollPitchout→StealResolver→重盗ロールの呼び出し順は
                // 旧来の打席頭一括判定と不変。3アウト目ならこの打席は未決着のまま次イニングへ
                // （スクイズの挟殺と同じ扱い＝設計書15 Phase D-2c の squeezeAbandoned を踏襲）。
                if (stealAttempt is { } stealStart && stealTarget == StealTarget.Second
                    && bases.First is not null && bases.Second is null)
                {
                    // 牽制アウト／離塁刺殺（design-14 P1-5）: 盗塁企図の裏で低確率の刺殺。既定オフ
                    // (PickoffBaseProb=0)では PickoffResolver.Resolve 内のガードでrng消費ゼロ。
                    if (PickoffResolver.Resolve(bases.First, currentPitcher, paBaserunning, rng))
                    {
                        offense.PickoffCount++;
                        outs++;
                        bases.First = null;
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                    else
                    {
                        // G3: 守備の読み → ピッチアウト（設計書09 §1, 12 §5）。捕手リード＋投手センス vs 意外性で
                        // 読み切れば捕手が優位に立ち刺されやすい。始動種別は采配（G3b）＝ギャンブルは好ジャンプだが
                        // 意表ゆえ読まれにくい一方、読まれると最も無防備。
                        var pitchout = StealReadModel.RollPitchout(
                            bases.First, defense.Catcher, currentPitcher, stealStart, paBaserunning, rng);
                        // 強制発動（F4）は抽選をスキップして成否を固定する。ここだけ乱数の消費順が変わるが、
                        // 強制した試合は HasForcedOutcomes で digest・統計から丸ごと外れるので影響が漏れない。
                        // 非強制時 paBaserunning は ctx.Baserunning と同一参照＝従来の乱数順・結果に不変。
                        var safe = force switch
                        {
                            Debugging.ForcedOutcome.StealSuccess => true,
                            Debugging.ForcedOutcome.StealCaught => false,
                            _ => StealResolver.Resolve(
                                bases.First, defense.Catcher, paBaserunning, rng, pitchout, stealStart) == StealResult.Safe,
                        };
                        offense.RecordSteal(bases.First, safe);
                        // 全力疾走・スライディング（設計書03 §3.5）: 盗塁企図ごとに走者が受傷しうる。
                        if (bases.First is { } slider)
                        {
                            Hurt(MatchInjuryModel.Roll(
                                InjuryScene.Sliding, ctx.MatchInjury.SlidingProb, slider, offense.Name,
                                inning, isTop, log.Count, rng, ctx.MatchInjury, ctx.Skills, ctx.InjuryCatalog,
                                sub: session.PitchCount));
                        }
                        if (safe)
                        {
                            bases.Second = bases.First;
                        }
                        else
                        {
                            outs++;
                        }
                        bases.First = null;

                        // 一・三塁の重盗（design-14 P1-4）: 二盗の送球中に三塁走者も本塁を狙う。既定オフ
                        // (DoubleStealThirdBreakProb=0)では分岐自体に入らずrng消費ゼロ＝単独二盗と完全一致。
                        // 投げた塁（二塁）だけがアウト対象という原則から、成立時の三塁走者は無条件生還。
                        if (bases.Third is not null && paBaserunning.DoubleStealThirdBreakProb > 0.0
                            && MathUtil.Chance(paBaserunning.DoubleStealThirdBreakProb, rng))
                        {
                            offense.DoubleStealThirdBreakCount++;
                            offense.Runs += 1;
                            runsThisHalf += 1;
                            if (bases.Third is { } dsScorer) offense.RecordRun(dsScorer); // 得点帰属（issue #77）
                            bases.Third = null;
                        }

                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                }
                // 三盗（issue #67）: 二塁のみ在塁でのみ判断側が返す（塁状況が排他のため一・三塁の重盗とは無競合）。
                else if (stealAttempt is { } thirdStart && stealTarget == StealTarget.Third
                    && bases.Second is not null && bases.First is null && bases.Third is null)
                {
                    if (PickoffResolver.Resolve(bases.Second, currentPitcher, ctx.Baserunning, rng))
                    {
                        offense.PickoffCount++;
                        outs++;
                        bases.Second = null;
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                    else
                    {
                        var pitchout = StealReadModel.RollPitchout(
                            bases.Second, defense.Catcher, currentPitcher, thirdStart, ctx.Baserunning, rng);
                        var safe = StealResolver.Resolve(
                            bases.Second, defense.Catcher, ctx.Baserunning, rng, pitchout, thirdStart,
                            StealTarget.Third) == StealResult.Safe;
                        offense.RecordSteal(bases.Second, safe);
                        if (bases.Second is { } thirdSlider)
                        {
                            Hurt(MatchInjuryModel.Roll(
                                InjuryScene.Sliding, ctx.MatchInjury.SlidingProb, thirdSlider, offense.Name,
                                inning, isTop, log.Count, rng, ctx.MatchInjury, ctx.Skills, ctx.InjuryCatalog,
                                sub: session.PitchCount));
                        }
                        if (safe) { bases.Third = bases.Second; } else { outs++; }
                        bases.Second = null;
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                }
                // 本盗（issue #67, design-14）: 三塁のみ在塁でのみ判断側が返す。同じ球でスクイズが確定していれば
                // 三塁走者は既にそちらへ使われているため、本盗は試みない（単純上書き, Q12-3と同型）。
                else if (stealAttempt is { } homeStart && stealTarget == StealTarget.Home
                    && bases.Third is not null && bases.Second is null && bases.First is null
                    && battingOverride != PitchBattingOverride.Squeeze)
                {
                    if (PickoffResolver.Resolve(bases.Third, currentPitcher, ctx.Baserunning, rng))
                    {
                        offense.PickoffCount++;
                        outs++;
                        bases.Third = null;
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                    else
                    {
                        var pitchout = StealReadModel.RollPitchout(
                            bases.Third, defense.Catcher, currentPitcher, homeStart, ctx.Baserunning, rng);
                        var safe = StealResolver.Resolve(
                            bases.Third, defense.Catcher, ctx.Baserunning, rng, pitchout, homeStart,
                            StealTarget.Home) == StealResult.Safe;
                        offense.RecordSteal(bases.Third, safe);
                        if (bases.Third is { } homeSlider)
                        {
                            Hurt(MatchInjuryModel.Roll(
                                InjuryScene.Sliding, ctx.MatchInjury.SlidingProb, homeSlider, offense.Name,
                                inning, isTop, log.Count, rng, ctx.MatchInjury, ctx.Skills, ctx.InjuryCatalog,
                                sub: session.PitchCount));
                        }
                        if (safe)
                        {
                            offense.Runs += 1;
                            runsThisHalf += 1;
                            if (bases.Third is { } homeScorer) offense.RecordRun(homeScorer); // 本盗の得点帰属（issue #77）
                        }
                        else
                        {
                            outs++;
                        }
                        bases.Third = null;
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                }

                // 手動1球指示（設計書15 Phase C-3）: ITacticsBrain を経由しないプレイヤー操作。
                // 予約されていれば brain の判断より常に優先する（「1球指示は方針を単純上書き」の指示元が
                // brain でも手動でも同じ扱い）。予約なし（未セット）ならフィールド読み取りのみでRNG非消費。
                if (offense.ConsumePendingPitchBattingOverride() is { } manualBatting) battingOverride = manualBatting;
                var (manualPolicy, manualGear) = defense.ConsumePendingPitchDefenseOverride();
                if (manualPolicy is { } mp)
                {
                    pitchOverride = ctx.Tactics.DirectiveFor(mp, batter.Bats);
                    tracePolicy = mp; // 観測専用
                }
                if (manualGear is { } mg) gearOverride = mg;

                var pitchRes = session.ThrowNextPitch(rng, battingOverride, pitchOverride, gearOverride);

                // 1球の観測レコードを完成させて流す（設計書17 §4.1）。打席解決層が埋めた意図・実着弾・打者判断へ、
                // 試合層しか知らない局面・状態・采配・RNG消費を足す。乱数は1回も追加消費しない。
                void EmitPitchTrace()
                {
                    if (traceSink is null) return;
                    var traces = session.PitchTraces;
                    if (traces.Count <= tracesBeforePitch) return; // 敬遠など1球も投げずに確定した打席
                    traceSink.OnPitch(traces[traces.Count - 1] with
                    {
                        Inning = inning,
                        IsTop = isTop,
                        Outs = outs,
                        BatterId = TraceIdOf(batter),
                        PitcherId = TraceIdOf(currentPitcher),
                        PitchNoInGame = p.TotalPitches + session.PitchCount,
                        PressureIndex = pressureIdx,
                        Rattled = defense.PitcherRattled,
                        PitchingFatigue = (int)System.Math.Round(defense.FatiguePitches),
                        Gear = gearOverride ?? defTactics.Gear,
                        Policy = tracePolicy ?? defTactics.Policy,
                        ChosenSign = battingOverride?.ToString() ?? paSignLabel,
                        SignCandidatesCsv = traceCandidates,
                        RngStreamId = rngStats?.LastForkStreamId ?? 0UL,
                        RngDrawsInPitch = (int)((rngStats?.Draws ?? 0) - drawsAtPitchStart),
                        Forced = forcedThisPa,
                    });
                }

                if (pitchRes.SqueezeRunnerCaughtAtThird)
                {
                    // スクイズのウエストを読まれた（設計書15 Phase D-2c）: 三塁走者が挟殺され、打者は
                    // 打席続行（次球からは方針なし＝強攻）。3アウト目ならこの打席は未決着のまま次イニングへ
                    // （盗塁死で3アウトになる場合と同じ扱い＝この打者は次イニング先頭）。
                    bases.Third = null;
                    outs++;
                    if (outs >= 3) { squeezeAbandoned = true; EmitPitchTrace(); break; }
                }

                // 暴投・パスボール（design-14 P2-8, 設計書15 Phase D-3）: 走者ありの各投球のうち、実際に
                // キャッチャーへ到達/通過した球（Ball/CalledStrike/SwingingStrike。Foul/InPlayは打者が
                // 触れた球なので対象外）だけを対象に、低確率のバッテリーミスで全走者が1つ進塁する
                // （三塁走者は生還）。暴投=投手責/パスボール=捕手責は意味上分けるが、記録は合算1カウントに
                // 簡略化（design-14の割り切り）。既定0（WildPitchProb=PassedBallProb=0）では走者の有無・
                // 投球結果によらず分岐自体に入らずrng消費ゼロ＝従来の帯と完全一致。
                if (bases.RunnerCount > 0
                    && session.LastPitchKind is PitchKind.Ball or PitchKind.CalledStrike or PitchKind.SwingingStrike
                    && (paBaserunning.WildPitchProb > 0.0 || paBaserunning.PassedBallProb > 0.0))
                {
                    var wpProb = MathUtil.Clamp(
                        paBaserunning.WildPitchProb - (pitcher.Control - 50) * paBaserunning.WildPitchControlSlope,
                        0.0, 1.0);
                    var pbProb = MathUtil.Clamp(
                        paBaserunning.PassedBallProb - (defense.Catcher.Catching - 50) * paBaserunning.PassedBallCatchingSlope,
                        0.0, 1.0);
                    if (MathUtil.Chance(wpProb + pbProb, rng))
                    {
                        var wpScorer = bases.Third; // 暴投/捕逸で生還するのは三塁走者（issue #77 得点帰属）
                        var wpRuns = BaserunningModel.ApplyBatteryMiss(bases);
                        offense.Runs += wpRuns;
                        runsThisHalf += wpRuns;
                        if (wpRuns > 0 && wpScorer is not null) offense.RecordRun(wpScorer);
                        offense.WildPitchCount++;
                    }
                }

                // この1球ぶんの観測を流す（暴投判定まで含めて「この球の窓」の乱数消費に数える）。
                EmitPitchTrace();
            }
            if (squeezeAbandoned || stealEndedHalf) continue;

            // 打順消費（設計書15 Phase D-2e・旧Q13）: 打席が実際に確定した時のみ1回だけ進める。三塁走者/一塁
            // 走者の挟殺・盗塁死・牽制死で3アウト目になり打席未決着のまま終わる場合は上のcontinueで抜けるため
            // ここへ来ない＝中断された打者自身が次イニング先頭になる（次打席は新規AtBatSessionなのでカウントは
            // 自動的に0-0からリセットされる）。旧実装はこの呼び出しをAtBatSession開始前（投球ループへ入る前）に
            // 無条件で行っていたため、3アウト目中断時に「次の打者」が誤って次イニング先頭になっていた
            // （バント/スクイズが決着する打席では、決着後の分岐内でも二重にNextBatter()を呼んでおり、
            // 打者を1人分余計に飛ばしていた。ここへ一本化したことで両方解消）。
            offense.NextBatter();
            var res = session.Result;
            defense.AddPitches(res.Pitches, gearWeight);
            p.TotalPitches += res.Pitches;

            // 死球（設計書03 §3.5）: 当たり所によっては骨折・打撲。
            if (res.Result == PlateAppearanceResult.HitByPitch)
            {
                Hurt(MatchInjuryModel.Roll(
                    InjuryScene.HitByPitch, ctx.MatchInjury.HitByPitchProb, batter, offense.Name,
                    inning, isTop, log.Count, rng, ctx.MatchInjury, ctx.Skills, ctx.InjuryCatalog));
            }

            // 投球過多（設計書03 §3.5）: スタミナ目安を大きく超えて投げ続けた投手の肩肘。
            if (defense.FatiguePitches
                > (currentPitcher.Pitching?.StaminaPitches ?? 90.0) + ctx.MatchInjury.OveruseOverPitches)
            {
                Hurt(MatchInjuryModel.Roll(
                    InjuryScene.Overuse, ctx.MatchInjury.OveruseProb, currentPitcher, defense.Name,
                    inning, isTop, log.Count, rng, ctx.MatchInjury, ctx.Skills, ctx.InjuryCatalog));
            }

            // バント決着（設計書15 Phase D-2b）: セッションがバント経路で確定した場合、進塁は AdvanceOnBunt
            // （一・二塁走者のみ1つ進む・三塁走者は自重）専用ルールで、ApplyDetailed の一般経路は使わない。
            if (res.BuntOutcome is { } buntOutcome)
            {
                offense.RecordSacrificeBunt(buntOutcome == BuntResult.SacrificeSuccess);
                var buntOuts = buntOutcome is BuntResult.PopOut or BuntResult.SacrificeSuccess ? 1 : 0;
                if (buntOutcome == BuntResult.SacrificeSuccess) AdvanceOnBunt(bases, batter: null);
                else if (buntOutcome == BuntResult.InfieldHit) AdvanceOnBunt(bases, batter);
                FinishPlateAppearance(offense, defense, currentPitcher, batter,
                    res.Result, 0, buntOuts, res.Pitches, inning, isTop, log, ctx,
                    batterOrder: batterOrder, pitchLog: res.PitchLog);
                outs += buntOuts;
                if (PitchingFatigue.ShouldRelieve(defense.CurrentPitcher, defense.FatiguePitches, defense.Fatigue ?? ctx.Fatigue)
                    || defense.CurrentPitcherAtWeeklyLimit())
                {
                    defense.TryChangePitcher();
                }
                if (walkoff?.Invoke() == true)
                {
                    offense.RecordInningRuns(runsThisHalf);
                    yield return Pa();
                    yield break;
                }
                yield return Pa();
                continue;
            }

            // スクイズ決着（設計書15 Phase D-2c）: セッションがスクイズ経路で確定した場合。ここに来るのは
            // SacrificeSuccess/InfieldHit/PopOut のみ（ウエストを読まれた場合、または送りバント自体が
            // Foul/MissedBunt で不成立だった場合はいずれも三塁走者が挟殺されるだけで打席は終わらず、
            // AtBatSession が PitchResolution.SqueezeRunnerCaughtAtThird で通知して打席続行させる＝上の
            // インラインブロックで処理済み。つまり sq.RunnerOut はここでは常に false）。
            if (res.Squeeze is { } sq)
            {
                var sqScorer = bases.Third; // スクイズで生還するのは三塁走者（issue #77 得点帰属。null化の前に捕捉）
                var sqOuts = sq.BatterOut ? 1 : 0;
                if (sq.Bunt is BuntResult.SacrificeSuccess or BuntResult.InfieldHit)
                {
                    bases.Third = null; // 三塁走者は生還済み（sq.Runs=1）
                    AdvanceOnBunt(bases, sq.BatterOut ? null : batter);
                }
                // 小フライ（PopOut）は走者釘付けのまま。

                offense.Runs += sq.Runs;
                runsThisHalf += sq.Runs;
                // スクイズ自体は失策由来ではないが、生還する三塁走者が既に失策で延命済み（issue #69）なら
                // その1点は自責対象外のまま引き継ぐ。
                var sqUnearnedRuns = sq.Runs > 0 && sqScorer is not null && unearnedRunners.Contains(sqScorer) ? sq.Runs : 0;
                if (sq.Runs > 0 && sqScorer is not null) offense.RecordRun(sqScorer);
                FinishPlateAppearance(offense, defense, currentPitcher, batter,
                    res.Result, sq.Runs, sqOuts, res.Pitches, inning, isTop, log, ctx,
                    batterOrder: batterOrder, pitchLog: res.PitchLog, unearnedRuns: sqUnearnedRuns);
                outs += sqOuts;
                if (PitchingFatigue.ShouldRelieve(defense.CurrentPitcher, defense.FatiguePitches, defense.Fatigue ?? ctx.Fatigue)
                    || defense.CurrentPitcherAtWeeklyLimit())
                {
                    defense.TryChangePitcher();
                }
                if (walkoff?.Invoke() == true)
                {
                    offense.RecordInningRuns(runsThisHalf);
                    yield return Pa();
                    yield break;
                }
                yield return Pa();
                continue;
            }

            // エンドランの空振り: スタートを切った一塁走者が憤死の危機（§1）。
            var extraOutsFromSign = 0;
            if (hitAndRun && bases.First is not null && res.Result == PlateAppearanceResult.Strikeout)
            {
                var stealSafeProb = StealResolver.SuccessProbability(bases.First, defense.Catcher, paBaserunning)
                        - ctx.Tactics.HitAndRunCaughtPenalty;
                if (bases.Second is null && MathUtil.Chance(MathUtil.Clamp(stealSafeProb, 0.02, 0.98), rng))
                {
                    bases.Second = bases.First;
                }
                else
                {
                    extraOutsFromSign = 1;
                }
                bases.First = null;
            }

            // 本塁クロスプレー（バックホーム憤死, 設計書12 §3, F2）: この打球の外野処理点・肩から
            // 送り判定＋時間の勝負で本塁生還を解く。幾何が無ければ null＝従来の確率テーブルへフォールバック。
            HomePlayContext? homePlay = null;
            if (res.Play is { FielderThrowSpeedMps: { } arm } fp)
            {
                // 送球の起点は「回収点」（安打では着地後の転がりの終端＝Issue #24）。転がりを解かない
                // 経路では着地点と同値なので従来と一致する。
                var homePlaySituation = new HomePlaySituation(
                    new Vector3D(fp.FieldedX, 0, fp.FieldedZ),
                    fp.FieldedAtSeconds ?? fp.HangTimeSeconds,
                    arm,
                    fp.FielderFielding);
                // aggression は中立固定（校風/ティア/采配の三層写像は残Q10）。
                // 守備の内野深さ（G1）＝ゴロ凡打の三塁走者判定へ「前進で刺す/後退で献上」を反映。
                homePlay = new HomePlayContext(
                    ctx.Field, homePlaySituation, ctx.Tactics, 0.5, defTactics.Infield, fp.IsFly);
            }

            // 打席前の塁状況＋アウト数を観測（塁ダイヤ／静止走者トークン用。追加記録のみ・判定不変, #3/#4）。
            var preFirst = bases.First is not null;
            var preSecond = bases.Second is not null;
            var preThird = bases.Third is not null;
            // 自責点（issue #69）: 失策連鎖が発生した打席では、打席前から塁上にいた走者も同じ失策1本で
            // 延命したとみなし自責対象外にする（簡易規則）。この後 ApplyDetailed が bases を書き換えるため先に控える。
            var preFirstPlayer = bases.First;
            var preSecondPlayer = bases.Second;
            var preThirdPlayer = bases.Third;
            var outsBefore = outs;
            // 本塁クロスプレーの受傷候補（設計書03 §3.5）。刺された走者を個別に特定する経路が無いため、
            // 本塁を狙う可能性が最も高い先行走者を候補にする（観測用の近似。判定には一切使わない）。
            var leadRunnerBefore = bases.Third ?? bases.Second ?? bases.First;

            // 走塁詳細（走者の動き）はタイムライン捕捉時のみ収集（判定・乱数順は同一）。
            // 失策連鎖（design-14 P1-6b）の送球精度連動用: 失策を犯した野手の ThrowAccuracy（未処理球では既定50）。
            var errorThrowAccuracy = res.Play?.FielderThrowAccuracy ?? 50;
            // collectMoves は常に true（GameEngine は自校の詳細試合専用＝裏試合は通らない, RNG中立）。
            // 得点帰属（issue #77）に本塁到達 RunnerMove を使うため、観戦しない試合でも moves を収集する。
            // 走塁係数は paBaserunning（設計書17 §6.1, F4）。非強制時は ctx.Baserunning と同一参照＝従来と不変。
            var (runs, extraOuts, baseOuts, batterSafeOnFc, errorExtraAdvanceOccurred, runnerMoves) = BaserunningModel.ApplyDetailed(
                bases, res.Result, batter, outs, paBaserunning, rng, collectMoves: true,
                homePlay, r1Start, errorThrowAccuracy);
            var homeOuts = baseOuts.Home; // 本塁クロスプレー憤死（統計参考値）
            var thirdOuts = baseOuts.Third; // 単打の一塁→三塁レース憤死（Issue #89。統計参考値）
            offense.Runs += runs;
            runsThisHalf += runs;

            // 自責点（issue #69・簡易規則）: 失策で出塁した打者走者、および失策連鎖（design-14 P1-6）で
            // 延命した打席前からの走者を自責対象外として集合に積む。得点が絡む解決ロジック自体には触れない
            // （記録のみ・帯不変）。
            if (res.Result == PlateAppearanceResult.ReachedOnError)
            {
                unearnedRunners.Add(batter);
                if (errorExtraAdvanceOccurred)
                {
                    if (preFirstPlayer is not null) unearnedRunners.Add(preFirstPlayer);
                    if (preSecondPlayer is not null) unearnedRunners.Add(preSecondPlayer);
                    if (preThirdPlayer is not null) unearnedRunners.Add(preThirdPlayer);
                }
            }

            // 本塁到達（ToBase>=4・非アウト）した走者へ得点を帰属（issue #77）。同時に自責点対象外の
            // 生還数を数える（issue #69）。
            var unearnedRunsThisPa = 0;
            foreach (var m in runnerMoves)
            {
                if (m.ToBase >= 4 && !m.Out)
                {
                    offense.RecordRun(m.Runner);
                    if (unearnedRunners.Contains(m.Runner)) unearnedRunsThisPa++;
                }
            }
            if (errorExtraAdvanceOccurred) offense.ErrorExtraAdvanceCount++; // 失策連鎖（design-14 P1-6。統計参考値）

            // 本塁クロスプレー（設計書03 §3.5・設計書12 §3）: 走者と捕手の接触。どちらが負傷するかも
            // Fork した専用ストリームで決める（本体の乱数順に触れない）。
            if (homeOuts > 0 && ctx.MatchInjury.HomeCollisionProb > 0.0)
            {
                var pick = rng.Fork(MatchInjuryModel.StreamId(
                    inning, isTop, log.Count, Players.InjuryScene.HomeCollision, sub: 1));
                var catcherHurt = MathUtil.Chance(ctx.MatchInjury.HomeCollisionCatcherShare, pick);
                var victim = catcherHurt ? defense.Catcher : leadRunnerBefore;
                if (victim is not null)
                {
                    Hurt(MatchInjuryModel.Roll(
                        Players.InjuryScene.HomeCollision, ctx.MatchInjury.HomeCollisionProb, victim,
                        catcherHurt ? defense.Name : offense.Name,
                        inning, isTop, log.Count, rng, ctx.MatchInjury, ctx.Skills, ctx.InjuryCatalog));
                }
            }

            // フェンス激突（設計書03 §3.5・設計書13）: フェンス際まで追って捕った外野手。
            if (ctx.MatchInjury.FenceCrashProb > 0.0
                && res.Play is { IsFly: true, FielderRole: { } role } fpc
                && fpc.Result == Match.Fielding.BattedBallResult.Out
                && IsOutfield(role))
            {
                var fenceDist = ctx.Field.FenceDistance(fpc.BearingDeg * Math.PI / 180.0);
                if (fenceDist - fpc.RangeM <= ctx.MatchInjury.FenceCrashMarginM
                    && defense.PlayerAtPosition(role) is { } outfielder)
                {
                    Hurt(MatchInjuryModel.Roll(
                        Players.InjuryScene.FenceCrash, ctx.MatchInjury.FenceCrashProb, outfielder, defense.Name,
                        inning, isTop, log.Count, rng, ctx.MatchInjury, ctx.Skills, ctx.InjuryCatalog));
                }
            }

            // タイムラインへ走者レッグを合成（CHANGELOG 32: 走者進塁の動きも再生対象）。
            var timeline = res.Timeline;
            if (timeline is not null)
            {
                if (runnerMoves.Count > 0)
                {
                    timeline = Match.Timeline.TimelineBuilder.AppendRunnerLegs(timeline, runnerMoves, ctx.Field);

                    // 併殺(二塁封殺)かつ内野ゴロなら、ボールの送球連鎖を 6-4-3 型に差し替える（設計書12 §2, F1 Slice 3）。
                    // 野選（FC, design-14 P1-1）も同じ二塁封殺 RunnerMove を積むが打者は生存するため、
                    // !batterSafeOnFc で除外しないと「打者アウト」の2本目送球が誤って描画される。
                    var hasForceAtSecond = false;
                    foreach (var m in runnerMoves)
                    {
                        if (m.Out && m.FromBase == 1 && m.ToBase == 2) { hasForceAtSecond = true; break; }
                    }
                    if (hasForceAtSecond && !batterSafeOnFc && res.Play is { IsFly: false } dpPlay)
                    {
                        // 打者走者の一塁到達（"打"レッグ終端）を2本目送球のアンカーにする。
                        var batterArrive = timeline.Duration;
                        foreach (var leg in timeline.Runners)
                        {
                            if (leg.Label == "打") { batterArrive = leg.T1; break; }
                        }
                        timeline = Match.Timeline.TimelineBuilder.AppendDoublePlayThrows(
                            timeline, dpPlay, ctx.Field, batterArrive);
                    }
                }

                // 打席前から塁上にいて動かなかった走者を静止トークンで描く（#3）。移動走者とは二重に描かない。
                timeline = Match.Timeline.TimelineBuilder.AppendHeldRunners(
                    timeline, preFirst, preSecond, preThird, runnerMoves, ctx.Field);

                // バックホーム: 本塁で憤死した走者、または外野安打で生還した走者がいれば送球連鎖
                // （外野→中継→本塁）を描く（設計書12 §3, F2 Slice D＋#7 セーフ生還も描く）。
                var homeThrowAdded = false;
                if (res.Play is { FielderThrowSpeedMps: not null } bhPlay)
                {
                    Match.Timeline.RunnerLeg? outAtHome = null;
                    Match.Timeline.RunnerLeg? safeAtHome = null;
                    foreach (var leg in timeline.Runners)
                    {
                        var toHome = Math.Abs(leg.To.X) < 0.5 && Math.Abs(leg.To.Z) < 0.5;
                        if (!toHome) continue;
                        if (leg.OutAtEnd) { outAtHome = leg; break; }
                        safeAtHome ??= leg;
                    }
                    if (outAtHome is { } o)
                    {
                        timeline = Match.Timeline.TimelineBuilder.AppendBackHomeThrows(
                            timeline, bhPlay, ctx.Field, o.T1, runnerOut: true);
                        homeThrowAdded = true;
                    }
                    else if (safeAtHome is { } s && (bhPlay.RangeM > 46.0 || bhPlay.ThroughInfield))
                    {
                        // 外野安打での生還は際どくなくても返球を描く（ボールが外野で死なない, #7）。
                        timeline = Match.Timeline.TimelineBuilder.AppendBackHomeThrows(
                            timeline, bhPlay, ctx.Field, s.T1, runnerOut: false);
                        homeThrowAdded = true;
                    }
                }

                // 本塁送球が無い外野安打は、先頭走者の到達塁へ返球してボールを内野へ戻す（#6）。
                // 返球到達は打者走者の到達時刻（"打"レッグ終端＝到達塁）に紐付けて、送球が走者を追い越さないようにする。
                if (!homeThrowAdded && res.Play is { } ofHit && (ofHit.RangeM > 46.0 || ofHit.ThroughInfield)
                    && ofHit.Result is Match.Fielding.BattedBallResult.Single
                        or Match.Fielding.BattedBallResult.Double or Match.Fielding.BattedBallResult.Triple)
                {
                    var leadBase = ofHit.Result == Match.Fielding.BattedBallResult.Triple ? "third" : "second";
                    var runnerArrive = 0.0;
                    foreach (var leg in timeline.Runners)
                        if (leg.Label == "打" && leg.T1 > runnerArrive) runnerArrive = leg.T1;
                    timeline = Match.Timeline.TimelineBuilder.AppendOutfieldReturnThrow(
                        timeline, ofHit, ctx.Field, leadBase, runnerArrive);
                }
            }

            // 振り逃げ（第3ストライク不捕球, design-14 P1-2）: 一塁空き or 2アウトの三振でのみ判定。
            // 既定オフ(DropThirdStrikeReachProb=0)では分岐自体に入らずrng消費ゼロ。投手にはKを計上したまま
            // 打者はアウトにしない（PlateAppearanceResult自体は Strikeout のまま変えない＝打数1・無安打は自動的に満たす）。
            var droppedThirdStrikeReached = false;
            if (res.Result == PlateAppearanceResult.Strikeout && (bases.First is null || outs == 2)
                && paBaserunning.DropThirdStrikeReachProb > 0.0)
            {
                var reachProb = MathUtil.Clamp(
                    paBaserunning.DropThirdStrikeReachProb
                    - (defense.Catcher.Catching - 50) * paBaserunning.DropThirdStrikeCatchingSlope,
                    0.0, 0.95);
                if (MathUtil.Chance(reachProb, rng))
                {
                    droppedThirdStrikeReached = true;
                    bases.First = batter;
                    offense.DroppedThirdStrikeCount++;
                }
            }

            var batterOut = BaserunningModel.IsBatterOut(res.Result, batterSafeOnFc, droppedThirdStrikeReached);
            var outsThisPa = (batterOut ? 1 : 0) + extraOuts + extraOutsFromSign;

            offense.HomePlayOuts += homeOuts; // 本塁クロスプレー憤死（F2外野＋G1内野ゴロ, 統計参考値）
            offense.ThirdPlayOuts += thirdOuts; // 単打の一塁→三塁レース憤死（Issue #89, 統計参考値）
            if (batterSafeOnFc) offense.FieldersChoiceCount++; // 野選（FC, design-14 P1-1。統計参考値）

            // 打席解決後の塁状況（塁ダイヤを結果へ更新, #4）。outs はこの後 outsThisPa を加算する前の値。
            var postFirst = bases.First is not null;
            var postSecond = bases.Second is not null;
            var postThird = bases.Third is not null;

            // 受傷をタイムラインのキャプションへ載せる（設計書03 §3.5 の表示接続）。
            // タイムライン捕捉オフ（統計シム）では timeline が null なので何も起きない＝ゼロコスト。
            if (timeline is not null && paInjuries is not null)
            {
                var caps = new List<Match.Timeline.TimelineCaption>(timeline.Captions);
                foreach (var e in paInjuries)
                {
                    caps.Add(new Match.Timeline.TimelineCaption(timeline.Duration, e.Caption(ctx.InjuryCatalog)));
                }
                timeline = timeline with { Captions = caps };
            }

            // 失策の個人帰属（issue #91）: FielderRole（守備位置）から当該選手を引く。守備位置は必ず埋まっている
            // ため通常は解決できるが、万一 null でもチーム計（AddError相当）は FinishPlateAppearance 側で必ず加算する。
            Player? errorFielder = null;
            Match.Field.FieldPosition errorRole = default;
            if (res.Result == PlateAppearanceResult.ReachedOnError && res.Play is { FielderRole: { } eRole })
            {
                errorRole = eRole;
                errorFielder = defense.PlayerAtPosition(eRole);
            }

            // 犠飛（issue #68）: フライ捕球の打席で、タッチアップにより走者が生還した場合。3アウト目の凡打では
            // ApplyInPlayOut が即座に (0 runs) を返す（アウトカウント確認は現状 outs>=2 ガード）ため、
            // ここで runs>0 を見るだけで「3アウト目ではない」ことも保証される＝AB非算入の対象を安全に絞れる。
            var isSacFly = res.Result == PlateAppearanceResult.InPlayOut && homePlay is { IsFly: true } && runs > 0;

            FinishPlateAppearance(offense, defense, currentPitcher, batter,
                res.Result, runs, outsThisPa, res.Pitches, inning, isTop, log, ctx, timeline,
                outsBefore, preFirst, preSecond, preThird, postFirst, postSecond, postThird,
                batterOrder, res.PitchLog, errorFielder, errorRole, isSacFly, unearnedRunsThisPa);
            // 敬遠（design-14 P1-3・設計書15 Phase D-2a）: 統一ステッパ経由でも、四球の内訳として別記録する。
            if (defTactics.IntentionalWalk) offense.IntentionalWalkCount++;

            outs += outsThisPa;

            if (PitchingFatigue.ShouldRelieve(defense.CurrentPitcher, defense.FatiguePitches, defense.Fatigue ?? ctx.Fatigue)
                        || defense.CurrentPitcherAtWeeklyLimit())
            {
                defense.TryChangePitcher();
            }

            if (walkoff?.Invoke() == true)
            {
                offense.RecordInningRuns(runsThisHalf);
                yield return Pa();
                yield break;
            }
            yield return Pa();
        }

        offense.RecordInningRuns(runsThisHalf);
    }

    /// <summary>速報・成績・動揺・伝令窓の更新（結果には影響しない共通後処理）。</summary>
    private static void FinishPlateAppearance(
        TeamState offense, TeamState defense, Player pitcher, Player batter,
        PlateAppearanceResult result, int runs, int outsThisPa, int pitches,
        int inning, bool isTop, List<PlayLogEntry> log, GameContext ctx,
        Match.Timeline.PlayTimeline? timeline = null,
        int outsBefore = 0,
        bool baseFirstBefore = false, bool baseSecondBefore = false, bool baseThirdBefore = false,
        bool baseFirstAfter = false, bool baseSecondAfter = false, bool baseThirdAfter = false,
        int batterOrder = 0,
        IReadOnlyList<AtBat.PitchRecord>? pitchLog = null,
        Player? errorFielder = null,
        Match.Field.FieldPosition errorRole = default,
        bool isSacFly = false,
        int unearnedRuns = 0)
    {
        if (result.IsHit()) offense.AddHit();
        if (result == PlateAppearanceResult.ReachedOnError) defense.RecordFieldingError(errorFielder, errorRole);
        offense.RecordBatting(batter, result, runs, isSacFly);
        // 打席確定球の球種（issue #180）: インプレーで終わった打席のみ末尾の PitchRecord が決め球。
        PitchType? decisivePitch = pitchLog is { Count: > 0 } pl && pl[^1].Kind == PitchKind.InPlay
            ? pl[^1].PitchType
            : null;
        defense.RecordPitching(pitcher, result, runs, outsThisPa, pitches, decisivePitch, unearnedRuns);
        defense.NotePitchingResult(result, ctx.Tactics.RattledThresholdFor(pitcher.Mental),
            outsThisPa, ctx.Tactics.RattledRecoveryOuts);
        offense.TickOffenseCalm();
        defense.TickDefenseCalm();
        log.Add(new PlayLogEntry(inning, isTop, batter.Name, result, runs, timeline,
            pitches, outsBefore,
            baseFirstBefore, baseSecondBefore, baseThirdBefore,
            baseFirstAfter, baseSecondAfter, baseThirdAfter,
            batterOrder,
            batter.SourceId, batter.Bats, batter.Position,
            pitcher.SourceId, pitcher.Name, pitcher.Throws,
            batter.UniformNumber, pitcher.UniformNumber,
            pitchLog,
            batter.ConditionValue, pitcher.ConditionValue));
    }

    /// <summary>バント処理時の進塁: 一・二塁走者が1つ進む（三塁走者は自重）。batter 非null なら打者一塁へ。</summary>
    private static void AdvanceOnBunt(BaseState bases, Player? batter)
    {
        if (bases.Second is not null && bases.Third is null)
        {
            bases.Third = bases.Second;
            bases.Second = null;
        }
        if (bases.First is not null && bases.Second is null)
        {
            bases.Second = bases.First;
            bases.First = null;
        }
        if (batter is not null && bases.First is null)
        {
            bases.First = batter;
        }
    }

    private static int ClampAbility(double v) => Math.Clamp((int)Math.Round(v), 1, 100);

    /// <summary>外野手か（フェンス激突の対象判定用）。</summary>
    private static bool IsOutfield(Match.Field.FieldPosition pos)
        => pos is Match.Field.FieldPosition.LeftField
               or Match.Field.FieldPosition.CenterField
               or Match.Field.FieldPosition.RightField;

    /// <summary>1球采配（設計書15 §2.3）の判断RNG用Fork streamId。inning/半イニング/PA添字/球数から
    /// 決定論的に導出し、末尾に攻守を分けるsaltを混ぜる（同じ球でも攻撃側・守備側で別ストリーム）。</summary>
    private static ulong PitchStreamId(int inning, bool isTop, int paIndex, int pitchNumber, ulong salt)
        => salt ^ ((ulong)(uint)inning << 48) ^ ((isTop ? 1UL : 0UL) << 47)
                ^ ((ulong)(uint)paIndex << 16) ^ (ulong)(uint)pitchNumber;

    /// <summary>
    /// 暫定盗塁ヒューリスティック: 一塁に俊足走者・二塁が空・&lt;2アウトなら単独二盗を試みる。
    /// 戻り値=このプレーで増えたアウト数（盗塁死なら1）。成功なら走者を二塁へ進める。
    /// 采配Brain（設計書09）設定時はサインが置き換えるため呼ばれない。
    /// </summary>
    private static int TryStealAttempt(
        BaseState bases, TeamState offense, TeamState defense, GameContext ctx, IRandomSource rng, int outs)
    {
        if (outs >= 2) return 0;
        var runner = bases.First;
        if (runner is null || bases.Second is not null) return 0;
        if (runner.Steal < ctx.StealAttemptThreshold) return 0;

        var catcher = defense.Catcher;
        var safe = StealResolver.Resolve(runner, catcher, ctx.Baserunning, rng) == StealResult.Safe;
        offense.RecordSteal(runner, safe);
        if (safe)
        {
            bases.Second = runner;
            bases.First = null;
            return 0;
        }
        // 盗塁死。
        bases.First = null;
        return 1;
    }

    // ===== 選手交代（設計書09 §6, C-2）。控えが非空かつBrainありの時だけ判断＝既定オフで従来一致。 =====

    /// <summary>守備側のイニング頭の守備固め判断。</summary>
    private static void MaybeDefensiveSub(
        TeamState defense, TeamState offense, GameContext ctx, IRandomSource rng, int inning, BaseState bases)
    {
        if (defense.Tactics is null || defense.Bench.Count == 0) return;
        var sit = SubSituationFor(defense, offense, ctx, inning, outs: 0, bases);
        if (defense.Tactics.CallDefensiveSub(sit, rng) is { } d)
        {
            defense.DefensiveSub(d.Out, d.Sub);
        }
    }

    /// <summary>攻撃側の打席頭の代走・代打判断（代走が先＝走者を確定してから打者を替える）。</summary>
    private static void MaybeOffenseSubs(
        TeamState offense, TeamState defense, GameContext ctx, IRandomSource rng, int inning, int outs, BaseState bases)
    {
        if (offense.Tactics is null || offense.Bench.Count == 0) return;

        // 代走: 塁上の鈍足走者を速い控えへ。塁の参照も差し替える。
        if (offense.Tactics.CallPinchRun(SubSituationFor(offense, defense, ctx, inning, outs, bases), rng)
            is { } r && offense.PinchRunFor(r.Runner, r.Sub))
        {
            var inserted = r.Sub with { Position = r.Runner.Position };
            if (bases.First == r.Runner) bases.First = inserted;
            else if (bases.Second == r.Runner) bases.Second = inserted;
            else if (bases.Third == r.Runner) bases.Third = inserted;
        }

        // 代打: 次打者を厚い控えへ（投手枠はStandard側で除外）。
        if (offense.Bench.Count > 0
            && offense.Tactics.CallPinchHit(SubSituationFor(offense, defense, ctx, inning, outs, bases), rng)
            is { } sub)
        {
            offense.PinchHitNext(sub);
        }
    }

    /// <summary>交代判断用の状況スナップショット（team 視点の得失点差）。</summary>
    private static SubstitutionSituation SubSituationFor(
        TeamState team, TeamState opponent, GameContext ctx, int inning, int outs, BaseState bases)
    {
        var upcoming = team.PeekBatter();
        var upcomingIsPitcher = !team.UsesDh && upcoming == team.CurrentPitcher;
        return new SubstitutionSituation(
            inning, ctx.RegulationInnings, outs, team.Runs - opponent.Runs,
            bases.First, bases.Second, bases.Third, upcoming, upcomingIsPitcher,
            team.CurrentLineup, team.Bench);
    }

    /// <summary>当日の出来を引く（途中出場等で未登録なら0=平常）。</summary>
    private static double FormOf(Dictionary<Player, double> dayForm, Player p)
        => dayForm.TryGetValue(p, out var v) ? v : 0.0;

    /// <summary>コールドゲーム（マーシールール）判定（設計書05 §1.3, OPEN-QUESTIONS Q18）。internal はテスト専用。</summary>
    internal static bool IsMercy(int inning, int away, int home)
    {
        var diff = Math.Abs(home - away);
        return (inning >= 5 && diff >= 10) || (inning >= 7 && diff >= 7);
    }
}

/// <summary>
/// 試合進行の境界。現状は「打席が1つ完了した」単位のみ。設計書09 の采配には打席途中
/// （カウント間のサイン・伝令）もあるため、<see cref="GameStepKind"/> を将来
/// 投球単位（Pitch）・伝令（Timeout）へ拡張できるよう Kind を持たせて予約する。
/// </summary>
public enum GameStepKind
{
    /// <summary>打席が1つ確定した境界。</summary>
    PlateAppearance,

    /// <summary>
    /// 1球ごとの境界（カウント間サイン・伝令の采配窓, 設計書15 §2.2）。通常打席の各投球の「前」に yield される。
    /// yield は乱数を消費しないので、この境界を挟んでも結果は従来と1ビットも変わらない。Phase A では采配は
    /// 挟まず（no-op 窓）、PA単位でしか進まない消費側（MatchProgression / GameReplay）はこの境界を読み飛ばす。
    /// </summary>
    Pitch,
    // 将来拡張: Timeout（伝令）, ...
}

/// <param name="LogIndex">この境界で確定した打席の <see cref="GameProgress.Log"/> 上の添字。</param>
public sealed record GameStep(GameStepKind Kind, int Inning, bool IsTop, int LogIndex);

/// <summary>
/// 試合の進行状態（打席単位ステップ実行の器）。<see cref="GameEngine.NewProgress"/> で用意し、
/// <see cref="GameEngine.Steps"/> で1打席ずつ進める。バッチ <see cref="GameEngine.Play"/> も同じ状態を
/// drain する（単一コードパス）。采配を挟む対話進行は、打席間にこの状態（攻撃側の控え起用など）を読み書きする。
/// </summary>
public sealed class GameProgress
{
    public Team AwayTeam { get; }
    public Team HomeTeam { get; }
    public TeamState Away { get; }
    public TeamState Home { get; }
    public GameContext Ctx { get; }
    public IRandomSource Rng { get; }
    public Dictionary<Player, double> DayForm { get; }

    /// <summary>現在のイニング（Steps が進める）。</summary>
    public int Inning { get; set; } = 1;
    /// <summary>コールドゲーム（マーシールール）成立で打ち切られたか。</summary>
    public bool MercyEnded { get; set; }
    /// <summary>両軍累計球数。</summary>
    public int TotalPitches { get; set; }
    /// <summary>打席の速報記録（打席確定ごとに追記される）。</summary>
    public List<PlayLogEntry> Log { get; } = new();
    /// <summary>試合中に発生した怪我（設計書03 §3.5）。観測データ＝試合進行・乱数順には影響しない。</summary>
    public List<MatchInjuryEvent> Injuries { get; } = new();

    public GameProgress(Team awayTeam, Team homeTeam, TeamState away, TeamState home,
        GameContext ctx, IRandomSource rng, Dictionary<Player, double> dayForm)
    {
        AwayTeam = awayTeam;
        HomeTeam = homeTeam;
        Away = away;
        Home = home;
        Ctx = ctx;
        Rng = rng;
        DayForm = dayForm;
    }

    /// <summary>攻撃側の TeamState（isTop=true なら先攻 away）。対話采配の適用先判定に使う。</summary>
    public TeamState OffenseOf(bool isTop) => isTop ? Away : Home;

    // ===== 打席境界の局面スナップショット（観測seam。打席が確定するたび更新される） =====
    // 対話進行（MatchProgression）の交代UIが「今この瞬間、誰が塁にいて何アウトか」を知るために使う。
    // 書き込みは PlayHalfSteps の打席境界のみ・乱数を消費しないので試合結果には一切影響しない。
    /// <summary>直近に確定した打席時点の塁状況（未進行なら null）。代走の塁差し替えもこの実体に対して行う。</summary>
    public BaseState? CurrentBases { get; internal set; }
    /// <summary>直近に確定した打席の直後のアウト数（3ならその半イニングは終了済み＝塁上は無効）。</summary>
    public int CurrentOuts { get; internal set; }
    /// <summary>直近に確定した打席が表（先攻の攻撃）だったか。交代対象チームの攻守判定に使う。</summary>
    public bool CurrentIsTop { get; internal set; } = true;

    // ===== デバッグ観測（設計書17 §4, F1）。すべて観測専用で試合結果には影響しない。 =====

    /// <summary>観測の出口（<see cref="GameContext.TracingEnabled"/> が偽なら null）。</summary>
    public Debugging.IDebugTraceSink? TraceSink => Ctx.TracingEnabled ? Ctx.TraceSink : null;

    /// <summary>乱数消費の集計器（観測時のみ非null）。</summary>
    public Debugging.CountingRandomSource.Counter? RngStats
        => (Rng as Debugging.CountingRandomSource)?.Stats;

    /// <summary><see cref="GameEngine.BuildResult"/> の OnGameEnd を一度だけ流すためのフラグ。</summary>
    internal bool TraceEndEmitted { get; set; }

    // ===== 強制発動（設計書17 §6.1, F4）=====
    // 唯一「結果を変える」層。予約は次の1打席に対して一度きり効く（1球采配の override と同型）。

    private Debugging.ForcedOutcome _pendingForce;

    /// <summary>これまでに強制発動が実際に効いた回数。1回でも非ゼロなら digest・統計集計から外す。</summary>
    public int ForcedCount { get; private set; }

    /// <summary>次の1打席に効く強制発動を予約する（デバッグ経路専用）。</summary>
    public void ForceNext(Debugging.ForcedOutcome outcome) => _pendingForce = outcome;

    /// <summary>予約された強制発動を取り出す（取り出したら解除＝一度きり）。</summary>
    internal Debugging.ForcedOutcome ConsumeForcedOutcome()
    {
        var f = _pendingForce;
        if (f != Debugging.ForcedOutcome.None)
        {
            _pendingForce = Debugging.ForcedOutcome.None;
            ForcedCount++;
        }
        return f;
    }

    // ===== 場面ジャンプ（設計書17 §3.4, F2）=====

    /// <summary>最初の半イニングで消費する開始局面（塁・アウト・カウント）。消費後は null。</summary>
    internal Debugging.ScenarioStart? PendingScenarioStart { get; set; }

    /// <summary>「裏から始める」宣言のとき、最初の1回だけ表の攻撃を飛ばす。</summary>
    internal bool ScenarioSkipsFirstTop { get; set; }

    internal Debugging.ScenarioStart? ConsumeScenarioStart()
    {
        var s = PendingScenarioStart;
        PendingScenarioStart = null;
        return s;
    }
}
