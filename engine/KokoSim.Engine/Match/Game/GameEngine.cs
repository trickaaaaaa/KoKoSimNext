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
    /// <summary>併殺打の総数（両軍計。GIDP＝二塁封殺＋打者一塁アウト, Issue #369。統計参考値）。</summary>
    public int DoublePlayCount { get; init; }

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

    /// <summary>継投ログ（issue #209 表現。継投・投野交代・野手登板）。観測専用＝決定論digestには含めない。</summary>
    public IReadOnlyList<PitchingChangeEvent> PitchingChangeLog { get; init; } = Array.Empty<PitchingChangeEvent>();

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
        IReadOnlyDictionary<Player, int>? priorWeekPitches = null,
        IReadOnlyDictionary<Player, double>? awayPitchRecovery = null,
        IReadOnlyDictionary<Player, double>? homePitchRecovery = null)
    {
        var p = NewProgress(
            awayTeam, homeTeam, ctx, rng, priorWeekPitches,
            awayPitchRecovery: awayPitchRecovery, homePitchRecovery: homePitchRecovery);
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
    /// <param name="awayPitchRecovery">
    /// 試合間の回復モデル（設計書02 §1.1e-2, issue #41 本流配線）で調整済みの実効目安投球数（先攻チーム分）。
    /// null=素の目安投球数のまま（従来挙動と完全一致・Balance CLI 等は常にこちら）。チームごとに別辞書にするのは、
    /// <see cref="Player"/> が値等価の record で、1つの辞書へ merge すると別校の選手が偶然全属性一致したとき
    /// 同一キーに潰れかねないため（校内は背番号が一意なので同一チーム内では衝突しない）。
    /// </param>
    /// <param name="homePitchRecovery">同上（後攻チーム分）。</param>
    public static GameProgress NewProgress(
        Team awayTeam, Team homeTeam, GameContext ctx, IRandomSource rng,
        IReadOnlyDictionary<Player, int>? priorWeekPitches = null,
        Debugging.ScenarioStart? scenarioStart = null,
        IReadOnlyDictionary<Player, double>? awayPitchRecovery = null,
        IReadOnlyDictionary<Player, double>? homePitchRecovery = null)
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
        // 試合間の回復モデル（設計書02 §1.1e-2）。既定 null＝素の目安投球数のまま＝従来挙動と完全一致。
        away.SetPitchRecovery(awayPitchRecovery);
        home.SetPitchRecovery(homePitchRecovery);

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

            // 規定回未満でも、表を終えた時点で後攻が既にコールド差でリードしていれば裏は行わず終了する
            // （規定回以降の「後攻リードで裏を省く」は下の既存判定に任せる＝挙動不変。issue #537）。
            if (ctx.MercyRuleEnabled && p.Inning < ctx.RegulationInnings
                && p.Home.Runs > p.Away.Runs && IsMercy(p.Inning, p.Away.Runs, p.Home.Runs))
            {
                p.MercyEnded = true;
                break;
            }

            // 最終回以降、後攻が既にリードなら裏の攻撃はしない。
            if (p.Inning >= ctx.RegulationInnings && p.Home.Runs > p.Away.Runs) break;

            // 裏: 後攻(home)攻撃 / 先攻(away)守備。最終回以降はサヨナラ判定。
            //
            // issue #633: コールドも「点差がついた時点で打ち切る」（設計書05 §1.3）。判定が半回の
            // 境目（上の :249 と下の :268）にしか無かったため、裏の攻撃中に点差が開いてもその半回を
            // 3アウトまで消化していた。サヨナラと同じ器＝打席の決着ごとに評価される述語へ相乗りさせる。
            //
            // 表には入れない: 先攻がコールド差をつけても**その回の裏は行ってから**終わるのが実際の
            // ルールで、表の中で打ち切ると後攻の攻撃機会を奪う（:249 の「表を終えた時点で後攻が既に
            // リードなら裏を省く」はそのまま維持）。
            //
            // MercyEnded と break はここでは触らない。半回を早く抜けた後に既存の :268 が
            // そのまま拾う（二重に break を足すと表の分岐と食い違う）。
            var allowWalkoff = p.Inning >= ctx.RegulationInnings;
            var stopBottomHalf = allowWalkoff || ctx.MercyRuleEnabled
                ? () => p.Home.Runs > p.Away.Runs
                        && (allowWalkoff
                            || IsMercy(p.Inning, p.Away.Runs, p.Home.Runs))
                : (Func<bool>?)null;
            foreach (var s in PlayHalfSteps(p, p.Home, p.Away,
                walkoff: stopBottomHalf, isTop: false)) yield return s;

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
            DoublePlayCount = away.DoublePlayCount + home.DoublePlayCount,
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
            PitchingChangeLog = p.PitchingChanges,
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

    /// <summary>打順＋先発＋「投手として置かれた控え」の当日の出来をサンプリング（順序固定＝決定論）。</summary>
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
            // 控えは1本の名簿になったが（issue #381）、**サンプリングする集合は変えない**＝
            // 投手として置かれた控えだけを名簿順に引く。控えを二分していた頃と同じ選手・同じ順序なので
            // 乱数消費が1語も動かない（帯・決定論baseline 不変）。控え野手にも当日の出来を配るのは
            // 消費が増える別の変更なので、やるなら帯の張り直しを伴う独立した issue にする。
            foreach (var p in team.Reserves)
                if (p.Position == Field.FieldPosition.Pitcher) map[p] = Sample(p);
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
        // 引き継ぎ走者の責任投手（issue inherited-runner-run-attribution）: 走者→出塁させた投手。
        // unearnedRunners と同じ寿命（半イニング）・同じ場所で持つ。GameProgress に置くのは代走の差し替え
        // （SubstitutionCommands.PinchRun＝対話采配）からも引き継ぎを書けるようにするため。
        var responsible = p.RunnerResponsibility;
        responsible.Clear();
        // 直前の打席を投げた投手（Pa() で「未登録の走者＝この打席で出塁した打者走者」の責任投手にする）。
        Player? paPitcher = null;

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
            // 打席で出塁した打者走者の責任投手＝この打席を投げた投手。塁上で未登録の走者は打者走者だけ
            // （タイブレーク/場面ジャンプの走者は半イニング頭で、代走は差し替え時に登録済み）。
            if (paPitcher is not null)
            {
                if (bases.First is { } r1 && !responsible.ContainsKey(r1)) responsible[r1] = paPitcher;
                if (bases.Second is { } r2 && !responsible.ContainsKey(r2)) responsible[r2] = paPitcher;
                if (bases.Third is { } r3 && !responsible.ContainsKey(r3)) responsible[r3] = paPitcher;
            }
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
        // タイブレーク／場面ジャンプで最初から塁にいる走者は、この半イニングの頭の投手の責任にする。
        if (bases.First is { } seededFirst) responsible[seededFirst] = defense.CurrentPitcher;
        if (bases.Second is { } seededSecond) responsible[seededSecond] = defense.CurrentPitcher;
        if (bases.Third is { } seededThird) responsible[seededThird] = defense.CurrentPitcher;

        // 守備固め（設計書09 §6）: イニング頭に守備側が控えを起用。控え空/無指示なら素通り＝従来一致。
        MaybeDefensiveSub(defense, offense, ctx, rng, inning, bases);

        // 守備位置の入替（設計書09 §2.3, issue #255）: 交代を挟んだ後の並びに対して組み替える。
        // 既定Brain（Standard／無指示）は常に「動かさない」を返すので従来一致。
        MaybeDefenseSwap(defense, offense, ctx, rng, inning, isTop);

        // issue #619: 1球指示の予約は半回をまたがない。盗塁・牽制が3アウト目になった球は
        // 予約を消費しないまま半回が終わる（stealEndedHalf）ため、消さないと次の攻撃回の
        // 先頭打者へ持ち越される。予約は「押した次の1球」の寿命なので、ここで捨てるのが正しい。
        // 読み書きのみで乱数を消費しない＝無指示のシム・統計帯は不変。
        offense.SetPendingPitchBattingOverride(null);
        offense.SetPendingSteal(null, StealTarget.Second);
        offense.ConsumePendingOffensiveSign();
        defense.SetPendingPitchDefenseOverride(null, null);

        while (outs < 3)
        {
            // 選手交代（設計書09 §6, 攻撃側）: 代走→代打。控え空/無指示なら素通り＝従来と完全一致。
            MaybeOffenseSubs(p, offense, defense, ctx, rng, inning, outs, bases);

            // スモールボール暫定ヒューリスティック（既定オフ）。采配Brain設定時はサインが置き換える。
            if (ctx.EnableSmallBall && offense.Tactics is null)
            {
                outs += TryStealAttempt(bases, offense, defense, ctx, rng, outs);
                if (outs >= 3) break;
            }

            // 采配窓の観測（設計書15 Phase D-1 のUI採用）: これから投げる1球の局面。乱数は消費しない。
            Timeline.Playback.LiveAtBat LiveAtBatOf(Player b, Player pit, int balls, int strikes, int pitchNumberInPa) => new()
            {
                Inning = inning,
                IsTop = isTop,
                AwayScore = p.Away.Runs,
                HomeScore = p.Home.Runs,
                Outs = outs,
                BaseFirst = bases.First is not null,
                BaseSecond = bases.Second is not null,
                BaseThird = bases.Third is not null,
                Balls = balls,
                Strikes = strikes,
                PitchNumberInPa = pitchNumberInPa,
                BatterOrder = offense.CurrentBatterOrder,
                BatterName = b.Name,
                BatterSourceId = b.SourceId,
                BatterBats = b.Bats,
                BatterPosition = b.Position,
                BatterNumber = b.UniformNumber,
                BatterConditionValue = b.ConditionValue,
                PitcherName = pit.Name,
                PitcherSourceId = pit.SourceId,
                PitcherThrows = pit.Throws,
                PitcherNumber = pit.UniformNumber,
                PitcherConditionValue = pit.ConditionValue,
            };

            // 打席の頭の采配窓（まだ1球も投げていない窓）。**投手・打者を束縛する前**に開く（2026-08-15）。
            // 旧実装は束縛（実効能力の計算・AtBatSession.Begin）を済ませてから窓を開いていたため、この窓で
            // 行った継投・代打はこの打席に届かず次の打席から効いた（監督報告「一人目の打者の間、前の投手が
            // 投げている」）。GameReplay は決定列を打席境界＝この窓と同じ位置で適用するので、順序を揃えることで
            // Save/Load 往復とも一致する。yield は乱数非消費＝一括 Play・AI駆動のシムは1ビットも変わらない。
            // **打席の頭でも埋まる**のが肝で、これが無いとUIは1球目を投げるまで打者を知れず、打席の頭で
            // サインを受け付けられない。
            if (ctx.CapturePitches)
                p.LiveAtBat = LiveAtBatOf(offense.PeekBatter(), defense.CurrentPitcher, pendingBalls, pendingStrikes, 1);
            yield return Pitch();

            var batter = offense.PeekBatter();
            var batterOrder = offense.CurrentBatterOrder; // ライブ観戦のスタメン列ハイライト用（打順消費前に採取）
            var currentPitcher = defense.CurrentPitcher; // 成績計上用の投手アイデンティティ
            paPitcher = currentPitcher;
            // 打者一巡: 打席に立つ選手は塁上に居ないので、前の出塁で残った責任投手を忘れる
            // （残すと救援のときに出た同じ選手の生還が先発へ付く）。
            responsible.Remove(batter);

            // 走者の責任投手（issue inherited-runner-run-attribution）: 登録があればその投手、無ければ
            // 今の投手（＝この打席で出た打者走者）。生還時の失点・自責点はこの投手へ付ける。
            Player ResponsibleFor(Player runner)
                => responsible.TryGetValue(runner, out var owner) ? owner : currentPitcher;
            // 打席の外（本盗・重盗・暴投/捕逸）で生還した走者の失点を責任投手へ付ける（sign=-1 は巻き戻し）。
            void ChargeRunnerScored(Player runner, int sign = 1)
                => defense.ChargeRuns(ResponsibleFor(runner), sign, unearnedRunners.Contains(runner) ? sign : 0);

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
            // Brain が両方nullなら未構築のまま。
            // ⚠ 「未構築なら以降 IPitchTacticsBrain 判定も必ずfalse」は成り立たない。打席の**途中**で
            //    brain が差し込まれうるからで、実際 MatchProgression.SkipDelegateToAi（スキップ委任）が
            //    OverrideTactics でそれをやる。未構築の situation は Batter が null なので、そのまま
            //    1球采配へ渡すと StandardTacticsBrain.CallPitchAction の s.Base.Batter で NRE になる
            //    （＝スキップが例外で落ち、押した側からは「試合が進まない」としか見えない）。
            //    構築済みかを下の1球采配の入口で見張る（両チームとも brain 無しの試合だけが該当し、
            //    既存経路では brain がある＝必ず構築済みなので RNG 消費も結果も1ビットも変わらない）。
            var situation = default(TacticsSituation);
            var situationBuilt = offense.Tactics is not null || defense.Tactics is not null;
            if (situationBuilt)
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

            // プレイヤーの采配指示（2026-07-31）: brain の判断より常に優先する。brain 呼び出しの**後**に
            // 置くのが肝で、こうすると brain 側のRNG消費が指示の有無で変わらない＝同シードの乱数順が保たれる。
            // 未設定ならフィールド読み取りだけでRNGを1発も引かない（＝AI駆動のシム・統計帯は完全に不変）。
            defTactics = defense.ApplyStandingDefense(defTactics);
            // 手動の攻撃サイン（エンドラン）は打席頭でなく**采配窓（各投球の直前）で消費する**（issue #492 B）。
            // 旧来はここで消費していたが、UI の采配窓はエンジンがこの地点を通り過ぎた後に開くため、いま見ている
            // 打席には構造上効かなかった。sign（brain の判断）は従来どおり打席頭で確定し、手動エンドランだけを
            // 下の投球ループで毎球評価する（無指示ならフィールド読み取りのみ＝RNG非消費・帯不変）。

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
            // 投手・打者の実効能力（疲労→調子→スキル→プレッシャー）。**純関数の連鎖で乱数非消費**。
            // 打席頭と、采配窓で継投・代打が入ったときの束縛し直し（RebindMatchup）が同じ式を通る＝一箇所に置く。
            (PitcherAttributes Pitcher, BatterAttributes BatterBase, SkillPlayMods PlayMods) EffectiveMatchup()
            {
                // 表示→物理の効き（設計書21）: 投手は制球・キレを Lv で寝かせる（球速の反応は PitchSelection・スタミナは据え置き）。
                var pit = PitchingFatigue.Effective(
                    ctx.Batting.AbilityEffect.Pitcher(currentPitcher.Pitching), defense.FatiguePitches, fatigue,
                    defense.EffectiveBaseStamina(currentPitcher));
                // 調子・当日の出来（設計書02 §3.3/§3.3b）。
                pit = Players.FormModel.ApplyPitcher(pit, currentPitcher.Condition, FormOf(dayForm, currentPitcher), ctx.Form);
                // 表示→物理の効き（設計書21）は ToBatter の中＝ここが打者側の唯一の入口。
                var bat = Players.FormModel.ApplyBatter(batter.ToBatter(ctx.Batting.AbilityEffect), batter.Condition, FormOf(dayForm, batter), ctx.Form);

                // スキル（設計書10）: 尻上がり/打者一巡は試合内の登板・打席累積で効く。行動特性・球質は playMods へ。
                var priorPa = offense.PriorPlateAppearances(batter);
                var priorBf = defense.PriorBattersFaced(currentPitcher);
                bat = SkillModel.ApplyBatter(bat, batter.Skills, priorPa, ctx.Skills);
                pit = SkillModel.ApplyPitcher(pit, currentPitcher.Skills, priorBf, ctx.Skills);
                var mods = SkillModel.PlayMods(batter.Skills, currentPitcher.Skills, priorBf, ctx.Skills);

                // プレッシャー×精神力（設計書02 §3）: 緩和=主将＋伝令、負側増幅=疲労・動揺。精神力50なら恒等。
                var pitcherFatigueOver = defense.FatiguePitches > defense.EffectiveBaseStamina(currentPitcher);
                pit = PressureModel.ApplyPitcher(pit, PressureModel.Multiplier(
                    currentPitcher.Mental, pressureIdx, ctx.Pressure,
                    defense.MitigationFor(offense: false, ctx.Tactics, ctx.Skills), pitcherFatigueOver,
                    defense.PitcherRattled ? ctx.Tactics.RattledNegativeAmplify : 1.0));
                bat = PressureModel.ApplyBatter(bat, PressureModel.Multiplier(
                    batter.Mental, pressureIdx, ctx.Pressure,
                    offense.MitigationFor(offense: true, ctx.Tactics, ctx.Skills)));
                return (pit, bat, mods);
            }
            var (pitcher, batterBase, playMods) = EffectiveMatchup();
            // ここまで（調子・スキル・プレッシャー）が打席内で不変な実効打者能力の土台。以降のサイン補正
            // （エンドラン/バスター/得点圏）と打撃姿勢は毎球評価しうる（issue #492）ので、土台を控えておき
            // 球ごとに BatterFor(...) で組み直す。無指示なら毎球の値は打席頭の値とビット単位で一致する。
            var batterAttrs = batterBase;

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
            // ウエストを読まれる確率（設計書02 §4.4）。現在の局面（捕手・アウト・点差）から都度求める純関数。
            // 打席頭の brain スクイズは頭の値で1回だけ計算（＝従来と完全一致）、手動スクイズ（issue #492 C）は
            // 打席途中に押されうるので下の投球ループで球ごとに取り直す。
            double SqueezeWasteNow() => MathUtil.Clamp(
                ctx.Tactics.SqueezeReadBase
                + (ctx.Fielding.AbilityEffect.Fielding(defense.Catcher.Fielding) - 50) * ctx.Tactics.SqueezeReadPerLead
                + (outs == 1 && Math.Abs(offense.Runs - defense.Runs) <= 1
                    ? ctx.Tactics.SqueezeReadObviousBonus : 0.0),
                0.0, 0.60);
            var squeezeWaste = squeezeSign is null ? 0.0 : SqueezeWasteNow();
            if (squeezeSign is not null)
            {
                offense.RecordSqueeze(); // 試行の記録は結果に関わらず一度だけ（旧: SqueezeResolver呼び出し直後と同じ扱い）。
                sign = OffensiveSign.Swing;
            }

            // --- エンドラン/バスター/得点圏の打撃補正（設計書09 §1。パイプライン不変） ---
            // brain のエンドラン判断は従来どおり打席頭で確定（＝打席全体に効く既定）。手動エンドラン（issue #492 B）は
            // 投球ループで毎球評価し、エンドランが効いた球だけ BatterFor(..., endrun:true) で補正する。
            var brainHitAndRun = sign == OffensiveSign.HitAndRun && bases.First is not null;
            // バスター/得点圏補正の適用可否は打席頭で確定（塁状況スナップショット）＝AI駆動のシムは従来と完全一致。
            var busterApplies = sign == OffensiveSign.Buster;
            var busterFactor = defTactics.BuntShift
                ? 1.0 + ctx.Tactics.BusterVsShiftBonus   // シフトの穴を突く
                : 1.0 - ctx.Tactics.BusterPenalty;       // 準備不足
            var chanceApplies = sign == OffensiveSign.Swing
                && (bases.Second is not null || bases.Third is not null)
                && batter.ChanceHitFactor != 1.0;

            // 実効打者能力を「この球のサイン状態（エンドランの有無）＋打撃姿勢」で組み立てる純関数（issue #492）。
            // 打席頭の既定は BatterFor(打席頭の姿勢, brainHitAndRun) で、旧来のインライン計算とビット単位で一致する
            // （順序: エンドラン→バスター→得点圏→姿勢）。毎球これを呼び直すが、状態が変わらなければ同じ値になる。
            BatterAttributes BatterFor(BattingApproach approach, bool endrun)
            {
                var b = batterBase;
                if (endrun)
                    b = b with
                    {
                        Contact = ClampAbility(b.Contact * (1.0 + ctx.Tactics.HitAndRunContactBoost)),
                        Power = ClampAbility(b.Power * (1.0 - ctx.Tactics.HitAndRunPowerPenalty)),
                        LaunchTendency = ClampAbility(b.LaunchTendency * (1.0 - ctx.Tactics.HitAndRunLaunchPenalty)),
                    };
                else if (busterApplies)
                    b = b with { Contact = ClampAbility(b.Contact * busterFactor) };
                else if (chanceApplies)
                    // 性格④（設計書01 §1.1）: 得点圏の強攻。目立ちたがりは長打質(Power)上振れ、自己犠牲はやや堅実。
                    b = b with { Power = ClampAbility(b.Power * batter.ChanceHitFactor) };
                return BattingApproachModel.Apply(b, approach, ctx.Tactics);
            }

            // 打席頭の既定（=1球目に効く値）。以降は投球ループが毎球 BatterFor を呼び直す。
            batterAttrs = BatterFor(offense.BattingApproach, brainHitAndRun);
            // 一塁走者のスタート種別（設計書12 §4, Q10決定）: エンドランは接触と同時に飛び出すコンタクト始動。
            // 投球ループが「その球でエンドランが効いたか」で毎球更新し、打席確定時（打球解決）に最後の値を使う。
            var r1Start = brainHitAndRun ? StartType.Contact : StartType.Normal;
            // 一塁走者が投球と同時に走っていた（盗塁企図が打球で巻き戻された。issue #626）か。打球が空中で
            // 捕られると始動種別によらず飛び出しの勝負になる（issue fly-out-runner-dp-table-and-doubled-off-depiction）。
            var r1StealBreak = false;
            var endrunOnLastPitch = brainHitAndRun; // 空振り三振でのスタート走者の憤死判定（下）に使う

            // 打順消費（設計書15 Phase D-2e）: 打席が実際に確定した時点まで遅らせる（下の squeezeAbandoned/
            // stealEndedHalf ガード直後）。ここでは進めない＝Peekのみでこの打者を握ったまま投球ループへ入る。

            // 守備指示→初期守備位置（陣形の効果は幾何＝物理から出る, §2.1）。基準配置を控えておき、陣形を
            // 1球単位で上書き（issue #492）するときはこの基準から組み直す。
            var baseAlignment = defense.DefensiveAlignment(ctx.Field, ctx.Fielding.AbilityEffect);
            var fielders = AlignmentTactics.Adjust(baseAlignment, defTactics, ctx.Tactics);
            // 打席文脈（守備配置・配球方針・捕手リード・スキル）。打席頭と束縛し直し（RebindMatchup）が同じ式を通る。
            AtBatContext BuildAtBatContext()
            {
                var directive = defTactics.Policy == PitchPolicy.Auto
                    ? (PitchDirective?)null
                    : ctx.Tactics.DirectiveFor(defTactics.Policy, batter.Bats);
                // 捕手リード（設計書01 §2①）: 良い配球で球威を引き出す。名捕手（設計書10）は実効リードを底上げ。
                var catcherLead = ctx.Batting.AbilityEffect.Pitching(defense.Catcher.Lead)
                    + (defense.Catcher.Skills.Has(Skill.MasterCatcher) ? ctx.Skills.MasterCatcherLeadBonus : 0);
                return ctx.ToAtBatContext(fielders, runnersOn: bases.RunnerCount > 0,
                    gear: defTactics.Gear, directive: directive, takeFirstPitch: sign == OffensiveSign.Take,
                    skills: playMods, catcherLead: catcherLead, intentionalWalk: defTactics.IntentionalWalk,
                    swingProbabilityFactor: BattingApproachModel.SwingFactor(offense.BattingApproach, ctx.Tactics),
                    // 慣れ（設計書02 §2.3b）: この打者がこの試合でこの投手と対戦した打席数。継投で0へ戻る。
                    familiarityPa: offense.PriorPlateAppearancesAgainst(batter, currentPitcher));
            }
            var abCtx = BuildAtBatContext();

            // 通常打席の投球ループ（設計書15 §2.1）: AtBatSession を1球ずつ回し、各投球の前に采配窓を yield する。
            // 一括 ResolveDetailed と実装・RNG消費が完全一致（yield は乱数非消費）＝バッチ Play と対話進行は同一結果。
            var session = AtBatSession.Begin(batterAttrs, pitcher, abCtx, batterPlayer: batter,
                thirdBaseRunner: squeezeThirdRunner, squeezeWasteProbability: squeezeWaste,
                initialBalls: pendingBalls, initialStrikes: pendingStrikes,
                forcedResult: forcedPaResult);
            pendingBalls = pendingStrikes = 0; // 途中カウントは場面ジャンプ直後の1打席にだけ効く

            // 采配窓で継投・代打が入ったとき（プレイヤーの手動交代。AI・一括シムはここへ来ない＝帯不変）、
            // この打席の投手・打者を差し替えて実効能力・打席文脈・AtBatSession を組み直す。頭の窓は上で束縛より
            // 前に開いたので通常ここへは来ないが、打席途中の窓（クリック中断→交代）では毎回ここで拾う＝
            // 新投手が同じカウントから同じ打者に投げ続ける（成績・球数はこの打席ぶんまるごと新投手へ＝簡略）。
            // 乱数は一切消費しない（EffectiveMatchup / BuildAtBatContext はすべて純関数）。
            void RebindMatchup()
            {
                batter = offense.PeekBatter();
                currentPitcher = defense.CurrentPitcher;
                paPitcher = currentPitcher;
                responsible.Remove(batter);
                if (situationBuilt)
                    situation = situation with { Batter = batter, Pitcher = currentPitcher, Catcher = defense.Catcher };
                (pitcher, batterBase, playMods) = EffectiveMatchup();
                // 得点圏の強攻補正は打者の性格で決まる（上の chanceApplies と同じ式・同じ sign 状態）
                chanceApplies = sign == OffensiveSign.Swing
                    && (bases.Second is not null || bases.Third is not null)
                    && batter.ChanceHitFactor != 1.0;
                batterAttrs = BatterFor(offense.BattingApproach, brainHitAndRun);
                baseAlignment = defense.DefensiveAlignment(ctx.Field, ctx.Fielding.AbilityEffect);   // 投野交代・守備交代で野手が代わりうる
                fielders = AlignmentTactics.Adjust(baseAlignment, defTactics, ctx.Tactics);
                abCtx = BuildAtBatContext();
                session.RebindMatchup(batterAttrs, pitcher, abCtx, batter);
            }

            // 采配ラベル（観測専用・設計書17 §4.1）。sign はバント/スクイズ/敬遠で Swing へ畳まれた後なので、
            // 畳む前の別名（buntSign/squeezeSign/IntentionalWalk）から表示用に復元する。
            var paSignLabel = traceSink is null ? null
                : defTactics.IntentionalWalk ? "IntentionalWalk"
                : squeezeSign?.ToString() ?? buntSign?.ToString()
                  ?? (sign != OffensiveSign.Swing ? sign.ToString() : null);

            var squeezeAbandoned = false;
            var stealEndedHalf = false;
            var atHeadWindow = true;   // 打席の頭の窓は束縛の前（上）で開いた
            while (!session.IsComplete)
            {
                // 打席途中の采配窓（各投球の手前）。頭の窓は上で開いてあるので1周目は開かない。
                if (!atHeadWindow)
                {
                    if (ctx.CapturePitches)
                        p.LiveAtBat = LiveAtBatOf(batter, currentPitcher, session.Balls, session.Strikes, session.PitchCount + 1);
                    yield return Pitch();
                }
                atHeadWindow = false;
                // 窓で継投・代打が入っていたら、この球からその選手で解決する（上の RebindMatchup の項）。
                if (!ReferenceEquals(offense.PeekBatter(), batter) || !ReferenceEquals(defense.CurrentPitcher, currentPitcher))
                    RebindMatchup();

                // 観測（設計書17 §4）: この球の乱数消費を測るため、采配窓を抜けた直後の値を控える。
                var drawsAtPitchStart = rngStats?.Draws ?? 0;
                var tracesBeforePitch = session.PitchTraces.Count;
                PitchPolicy? tracePolicy = null;
                string? traceCandidates = null;

                // 塁上/アウト数は盗塁の毎球判定（下）で打席途中に変わりうるため、brain へ渡す前に
                // 毎球作り直す（打者・スコア差等その他のフィールドは打席内不変なので変えない）。
                if (situationBuilt)
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
                if (situationBuilt && offense.Tactics is IPitchTacticsBrain offensePitchBrain)
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
                // 手動の盗塁指示（2026-07-31）: brain より優先。**盗塁の解決より前**で消費しないと
                // その球には間に合わない（打撃の手動指示は解決順の都合で下の別ブロックのまま）。
                if (offense.ConsumePendingSteal() is { } manualSteal)
                {
                    stealAttempt = manualSteal.Start;
                    stealTarget = manualSteal.Target;
                }
                if (situationBuilt && defense.Tactics is IPitchTacticsBrain defensePitchBrain)
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

                // 盗塁の再生データ（issue #485）: 盗塁企図が「実際の走塁勝負」で解決した球にだけ載る観測。
                // CaptureTimelines が真のときだけ組む＝統計シム・裏試合はゼロコスト・乱数消費ゼロ（BuildBattedBall と同作法）。
                Timeline.Playback.PlaybackPlay? stealPlay = null;
                // 牽制の再生データ（issue #486）: PickoffResolver.Resolve が発火した球にだけ載る観測。
                // 判定結果（アウト/セーフ）に関わらず組む＝分岐させない（2026-08-05 issue決定）。
                Timeline.Playback.PlaybackPlay? pickoffPlay = null;

                // issue #626: 盗塁が無効になる球（ファウル＝ボールデッド／打球＝打球優先）のための控え。
                // 盗塁は実球より前に完全解決する構造で、その球が無効だったと判るのは投げた後になる。
                // 解決順・乱数消費順は<b>一切動かさず</b>、適用結果だけを後から巻き戻す（不変条件#2）。
                // 牽制（PickoffResolver）は投球前の独立事象なので巻き戻さない＝牽制で決着した球には
                // そもそも盗塁が走らないので、下の stealRolledBack 系は立たない。
                var steal0First = bases.First;
                var steal0Second = bases.Second;
                var steal0Third = bases.Third;
                var steal0Outs = outs;
                var steal0Runs = offense.Runs;
                var steal0RunsThisHalf = runsThisHalf;
                Player? stealRecRunner = null;      // RecordSteal した走者（取り消し対象）
                var stealRecSuccess = false;
                Player? stealRecScorer = null;      // 本盗・重盗で得点した走者（帰属の取り消し対象）
                var stealDoubleBreak = false;       // 重盗の三塁ブレイクを数えたか
                StartType? stealStartApplied = null; // 実際に走った始動種別（打球の球でだけ活かす）

                // 盗塁/牽制/重盗（設計書02 §4.2・設計書09 §1・design-14 P1-4/P1-5・設計書15 Phase D-2d）:
                // カウントは消費せず（実球はこの後も通常どおり投げる）、この球の前に走者だけが動く。
                // PickoffResolver→StealReadModel.RollPitchout→StealResolver→重盗ロールの呼び出し順は
                // 旧来の打席頭一括判定と不変。3アウト目ならこの打席は未決着のまま次イニングへ
                // （スクイズの挟殺と同じ扱い＝設計書15 Phase D-2c の squeezeAbandoned を踏襲）。
                // 狙える塁の判定は StealTargets が単一ソース（issue #622）。UI の可否・ラベルも同じ式を引く
                // ＝「押せたのに engine が解決しない指示」を構造的に作らない。各分岐に残っている
                // `bases.X is { }` は判定の重複ではなく**コンパイラへの非null証明**（走者をそのまま
                // 牽制・盗塁解決へ渡すため。式が bool 経由になると null 解析が届かない）。
                var stealFeasible = StealTargets.FromBases(
                    bases.First is not null, bases.Second is not null, bases.Third is not null);

                if (stealAttempt is { } stealStart && stealTarget == StealTarget.Second
                    && stealFeasible == StealTarget.Second && bases.First is { })
                {
                    // 牽制アウト／離塁刺殺（design-14 P1-5）: 盗塁企図の裏で低確率の刺殺。既定オフ
                    // (PickoffBaseProb=0)では PickoffResolver.Resolve 内のガードでrng消費ゼロ。
                    var pickedOffFirst = PickoffResolver.Resolve(bases.First, currentPitcher, paBaserunning, rng);
                    // 再生データ（issue #486）: 判定結果（アウト/セーフ）に関わらず、牽制球が投じられた
                    // 球は絵にする＝分岐させない。乱数不使用（BuildPickoff は既に確定した pickedOffFirst の写し）。
                    if (ctx.CaptureTimelines)
                        pickoffPlay = Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(
                            Timeline.TimelineBuilder.BuildPickoff(
                                bases.First, currentPitcher, "first", pickedOffFirst, paBaserunning, ctx.Field));
                    if (pickedOffFirst)
                    {
                        offense.PickoffCount++;
                        outs++;
                        defense.RecordRunnerOut(currentPitcher); // 牽制死も投球回に数える（完投=27アウトの保証）
                        bases.First = null;
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                    else
                    {
                        // 走塁勝負に入る一塁走者と、重盗で本塁生還する三塁走者（成立時のみ）を絵のために控える
                        // （下で bases.First は二塁移動 or アウトで、bases.Third は生還で null になる）。表示専用。
                        var secondStealer = bases.First!;
                        Player? doubleScorer = null;
                        // G3: 守備の読み → ピッチアウト（設計書09 §1, 12 §5）。捕手リード＋投手センス vs 意外性で
                        // 読み切れば捕手が優位に立ち刺されやすい。始動種別は采配（G3b）＝ギャンブルは好ジャンプだが
                        // 意表ゆえ読まれにくい一方、読まれると最も無防備。
                        var pitchout = StealReadModel.RollPitchout(
                            bases.First, defense.Catcher, currentPitcher, stealStart, paBaserunning, rng);
                        // 強制発動（F4）は抽選をスキップして成否を固定する。ここだけ乱数の消費順が変わるが、
                        // 強制した試合は HasForcedOutcomes で digest・統計から丸ごと外れるので影響が漏れない。
                        // 非強制時 paBaserunning は ctx.Baserunning と同一参照＝従来の乱数順・結果に不変。
                        var steal = force switch
                        {
                            Debugging.ForcedOutcome.StealSuccess => StealResolver.Forced(
                                bases.First, defense.Catcher, paBaserunning, StealResult.Safe, pitchout, stealStart),
                            Debugging.ForcedOutcome.StealCaught => StealResolver.Forced(
                                bases.First, defense.Catcher, paBaserunning, StealResult.CaughtStealing, pitchout, stealStart),
                            _ => StealResolver.ResolveVerdict(
                                bases.First, defense.Catcher, paBaserunning, rng, pitchout, stealStart),
                        };
                        var safe = steal.Safe;
                        offense.RecordSteal(bases.First, safe);
                        stealRecRunner = secondStealer; stealRecSuccess = safe;   // issue #626: 巻き戻し対象
                        stealStartApplied = stealStart;
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
                            defense.RecordRunnerOut(currentPitcher); // 盗塁死も投球回に数える（下の巻き戻しと対）
                        }
                        bases.First = null;

                        // 一・三塁の重盗（design-14 P1-4）: 二盗の送球中に三塁走者も本塁を狙う。既定オフ
                        // (DoubleStealThirdBreakProb=0)では分岐自体に入らずrng消費ゼロ＝単独二盗と完全一致。
                        // 投げた塁（二塁）だけがアウト対象という原則から、成立時の三塁走者は無条件生還。
                        // issue #619: 本盗と同じ理由で、未消費の手動スクイズ予約があるときは三塁走者を
                        // 走らせない（同じ球で三塁走者がスクイズと重盗の両方に使われるのを防ぐ）。
                        // 既定 DoubleStealThirdBreakProb=0 では従来どおりこの分岐に入らず乱数消費ゼロ。
                        if (bases.Third is not null && paBaserunning.DoubleStealThirdBreakProb > 0.0
                            && offense.PendingPitchBattingOverride != PitchBattingOverride.Squeeze
                            && MathUtil.Chance(paBaserunning.DoubleStealThirdBreakProb, rng))
                        {
                            offense.DoubleStealThirdBreakCount++;
                            stealDoubleBreak = true;         // issue #626: 巻き戻し対象
                            offense.Runs += 1;
                            runsThisHalf += 1;
                            if (bases.Third is { } dsScorer)
                            {
                                offense.RecordRun(dsScorer); // 得点帰属（issue #77）
                                ChargeRunnerScored(dsScorer); // 失点は責任投手へ
                                doubleScorer = dsScorer;     // 重盗の絵（issue #485）
                                stealRecScorer = dsScorer;   // issue #626: 巻き戻し対象
                            }
                            bases.Third = null;
                        }

                        // 再生データ（issue #485）: 二盗の走塁勝負＋（成立時）重盗の三塁走者生還を絵にする。
                        if (ctx.CaptureTimelines)
                            stealPlay = Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(
                                Timeline.TimelineBuilder.BuildSteal(
                                    secondStealer, defense.Catcher, steal.Result,
                                    paBaserunning, ctx.Field, StealTarget.Second, pitchout, stealStart, doubleScorer,
                                    steal.RunnerSeconds, steal.DefenseSeconds));

                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                }
                // 三盗（issue #67）: 二塁のみ在塁でのみ判断側が返す（塁状況が排他のため一・三塁の重盗とは無競合）。
                else if (stealAttempt is { } thirdStart && stealTarget == StealTarget.Third
                    && stealFeasible == StealTarget.Third && bases.Second is { })
                {
                    var pickedOffSecond = PickoffResolver.Resolve(bases.Second, currentPitcher, ctx.Baserunning, rng);
                    // 再生データ（issue #486）: 判定結果に関わらず、牽制球が投じられた球は絵にする。
                    if (ctx.CaptureTimelines)
                        pickoffPlay = Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(
                            Timeline.TimelineBuilder.BuildPickoff(
                                bases.Second, currentPitcher, "second", pickedOffSecond, ctx.Baserunning, ctx.Field));
                    if (pickedOffSecond)
                    {
                        offense.PickoffCount++;
                        outs++;
                        defense.RecordRunnerOut(currentPitcher); // 牽制死も投球回に数える
                        bases.Second = null;
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                    else
                    {
                        var thirdStealer = bases.Second!; // 三盗の走者（下で bases.Second は三塁移動 or アウトで null）
                        var pitchout = StealReadModel.RollPitchout(
                            bases.Second, defense.Catcher, currentPitcher, thirdStart, ctx.Baserunning, rng);
                        var steal = StealResolver.ResolveVerdict(
                            bases.Second, defense.Catcher, ctx.Baserunning, rng, pitchout, thirdStart, StealTarget.Third);
                        var safe = steal.Safe;
                        offense.RecordSteal(bases.Second, safe);
                        stealRecRunner = thirdStealer; stealRecSuccess = safe;   // issue #626: 巻き戻し対象
                        if (bases.Second is { } thirdSlider)
                        {
                            Hurt(MatchInjuryModel.Roll(
                                InjuryScene.Sliding, ctx.MatchInjury.SlidingProb, thirdSlider, offense.Name,
                                inning, isTop, log.Count, rng, ctx.MatchInjury, ctx.Skills, ctx.InjuryCatalog,
                                sub: session.PitchCount));
                        }
                        if (safe) { bases.Third = bases.Second; }
                        else { outs++; defense.RecordRunnerOut(currentPitcher); } // 三盗死も投球回に数える
                        bases.Second = null;
                        // 再生データ（issue #485）: 三盗の走塁勝負を絵にする（送球先＝三塁・受け手＝三塁手）。
                        if (ctx.CaptureTimelines)
                            stealPlay = Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(
                                Timeline.TimelineBuilder.BuildSteal(
                                    thirdStealer, defense.Catcher, steal.Result,
                                    ctx.Baserunning, ctx.Field, StealTarget.Third, pitchout, thirdStart,
                                    doubleStealScorer: null, steal.RunnerSeconds, steal.DefenseSeconds));
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                }
                // 本盗（issue #67, design-14）: 三塁のみ在塁でのみ判断側が返す。同じ球でスクイズが確定していれば
                // 三塁走者は既にそちらへ使われているため、本盗は試みない（単純上書き, Q12-3と同型）。
                // issue #619: 抑止は brain 由来のスクイズ（battingOverride）だけでなく、まだ消費していない
                // 手動スクイズ予約も見る。手動指示の消費はこの下（盗塁解決の後）なので、予約を覗かないと
                // 「同じ球でスクイズと本盗が両方走り、三塁走者が二重に使われる」ことになる。
                else if (stealAttempt is { } homeStart && stealTarget == StealTarget.Home
                    && stealFeasible == StealTarget.Home && bases.Third is { }
                    && battingOverride != PitchBattingOverride.Squeeze
                    && offense.PendingPitchBattingOverride != PitchBattingOverride.Squeeze)
                {
                    var pickedOffThird = PickoffResolver.Resolve(bases.Third, currentPitcher, ctx.Baserunning, rng);
                    // 再生データ（issue #486）: 判定結果に関わらず、牽制球が投じられた球は絵にする。
                    if (ctx.CaptureTimelines)
                        pickoffPlay = Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(
                            Timeline.TimelineBuilder.BuildPickoff(
                                bases.Third, currentPitcher, "third", pickedOffThird, ctx.Baserunning, ctx.Field));
                    if (pickedOffThird)
                    {
                        offense.PickoffCount++;
                        outs++;
                        defense.RecordRunnerOut(currentPitcher); // 牽制死も投球回に数える
                        bases.Third = null;
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                    else
                    {
                        var homeStealer = bases.Third!; // 本盗の走者（下で bases.Third は生還 or アウトで null）
                        var pitchout = StealReadModel.RollPitchout(
                            bases.Third, defense.Catcher, currentPitcher, homeStart, ctx.Baserunning, rng);
                        var steal = StealResolver.ResolveVerdict(
                            bases.Third, defense.Catcher, ctx.Baserunning, rng, pitchout, homeStart, StealTarget.Home);
                        var safe = steal.Safe;
                        offense.RecordSteal(bases.Third, safe);
                        stealRecRunner = homeStealer; stealRecSuccess = safe;   // issue #626: 巻き戻し対象
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
                            if (bases.Third is { } homeScorer)
                            {
                                offense.RecordRun(homeScorer); // 本盗の得点帰属（issue #77）
                                ChargeRunnerScored(homeScorer); // 失点は責任投手へ
                                stealRecScorer = homeScorer;   // issue #626: 巻き戻し対象
                            }
                        }
                        else
                        {
                            outs++;
                            defense.RecordRunnerOut(currentPitcher); // 本盗死も投球回に数える
                        }
                        bases.Third = null;
                        // 再生データ（issue #485）: 本盗は捕手からの送球区間そのものが無い（投球が送球を兼ねる）。
                        if (ctx.CaptureTimelines)
                            stealPlay = Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(
                                Timeline.TimelineBuilder.BuildSteal(
                                    homeStealer, defense.Catcher, steal.Result,
                                    ctx.Baserunning, ctx.Field, StealTarget.Home, pitchout, homeStart,
                                    doubleStealScorer: null, steal.RunnerSeconds, steal.DefenseSeconds));
                        if (outs >= 3) { stealEndedHalf = true; break; }
                    }
                }

                // 手動1球指示（設計書15 Phase C-3）: ITacticsBrain を経由しないプレイヤー操作。
                // 予約されていれば brain の判断より常に優先する（「1球指示は方針を単純上書き」の指示元が
                // brain でも手動でも同じ扱い）。予約なし（未セット）ならフィールド読み取りのみでRNG非消費。
                if (offense.ConsumePendingPitchBattingOverride() is { } manualBatting) battingOverride = manualBatting;
                var (manualPolicy, manualGear, manualInfield, manualOutfield) = defense.ConsumePendingPitchDefenseOverride();
                if (manualPolicy is { } mp)
                {
                    pitchOverride = ctx.Tactics.DirectiveFor(mp, batter.Bats);
                    tracePolicy = mp; // 観測専用
                }
                if (manualGear is { } mg) gearOverride = mg;

                // 手動の攻撃サイン（エンドラン）を毎球評価（issue #492 B）。無指示ならフィールド読み取りのみ
                // ＝RNG非消費。brain のエンドラン（brainHitAndRun）は打席全体に効く既定として温存し、手動は
                // 「押した次の1球」に効く（bases.First があるときだけ＝走者不在なら自然に無効）。
                var manualEndrun = offense.ConsumePendingOffensiveSign() == OffensiveSign.HitAndRun;
                var endrunThisPitch = brainHitAndRun || (manualEndrun && bases.First is not null);
                endrunOnLastPitch = endrunThisPitch;
                r1Start = endrunThisPitch ? StartType.Contact : StartType.Normal;
                r1StealBreak = false;

                // 実効打者能力とスイング頻度をこの球の状態（姿勢＋エンドラン）で組み直す（issue #492）。
                // 無指示なら打席頭の値とビット単位で一致（BatterFor は同じ純関数・DecideSwing/ContactModel は
                // 確率値によらず必ず引く）＝AI駆動のシム・統計帯は不変。
                var batterAttrsPitch = BatterFor(offense.BattingApproach, endrunThisPitch);
                var swingFactorPitch = BattingApproachModel.SwingFactor(offense.BattingApproach, ctx.Tactics);

                // 陣形（前進守備/長打警戒）を毎球評価（issue #492）: 深さ上書きがあればこの球だけ野手配列を
                // 基準配置から組み直す。上書きなしなら打席頭の fielders を使う（＝従来と完全一致）。
                IReadOnlyList<Field.Fielder>? fieldersOverride = null;
                if (manualInfield is not null || manualOutfield is not null)
                {
                    var dt = defTactics with
                    {
                        Infield = manualInfield ?? defTactics.Infield,
                        Outfield = manualOutfield ?? defTactics.Outfield,
                    };
                    fieldersOverride = AlignmentTactics.Adjust(baseAlignment, dt, ctx.Tactics);
                }

                // スクイズの三塁走者を毎球取り直す（issue #492 C）。手動スクイズ（brain の squeezeSign 経由でない）は
                // 打席途中に押されうるので、この球の三塁走者と外し確率を渡す＝打席頭スナップショットの null に
                // よる例外を起こさない。brain スクイズ（squeezeSign 非null）は打席頭の値のまま＝従来と完全一致。
                Player? sqThirdOverride = null;
                double? sqWasteOverride = null;
                if (battingOverride == PitchBattingOverride.Squeeze && squeezeSign is null)
                {
                    // issue #619: 予約を消費した時点で三塁が空なら、スクイズが成立しない場面なので
                    // 無指示（強攻）へ落とす。盗塁・牽制の解決は手動指示の消費より前に走るため、
                    // 「窓を開けた時は三塁走者が居たのに、投球の直前に本盗・牽制死・重盗で消えた」が起こりうる。
                    // ここで落とさないと ①両方nullで AtBatSession が例外を投げて試合が止まる
                    // ②打席頭スナップショット（_thirdBaseRunner）が生きていると、もう三塁に居ない走者で
                    // スクイズが解決される（幽霊走者）— のどちらかになる。
                    // 無指示へ落とす経路は SqueezeWasteNow（純計算）も RecordSqueeze も通らない＝乱数消費ゼロ。
                    if (bases.Third is null)
                    {
                        battingOverride = null;
                    }
                    else
                    {
                        sqThirdOverride = bases.Third;
                        sqWasteOverride = SqueezeWasteNow();
                        offense.RecordSqueeze(); // 手動スクイズの試行を記録（brain は打席頭で記録済み）
                    }
                }

                var pitchRes = session.ThrowNextPitch(rng, battingOverride, pitchOverride, gearOverride,
                    batterOverride: batterAttrsPitch, swingFactorOverride: swingFactorPitch,
                    fieldersOverride: fieldersOverride, thirdRunnerOverride: sqThirdOverride,
                    squeezeWasteOverride: sqWasteOverride);

                // 盗塁の取り消し（issue #626）: 投げた球が「盗塁が成立しない球」だったら、上で適用した
                // 盗塁の結果だけを巻き戻す。乱数は既に引き終わっており順序も量も変えない（不変条件#2）。
                //
                //  - Foul   … ボールデッド。走者は元の塁へ戻り、企図そのものが無かったことになる
                //  - InPlay … 打球優先。盗塁は記録されず、走者は打球の解決（BaserunningModel.ApplyDetailed）で進む。
                //             スタートを切っていた分は r1Start へ渡して走塁の有利として活かす
                //
                // ファウルチップ（実ルールではインプレーで盗塁が成立する）は PitchKind に区別が無いため
                // 当面 Foul へ一括する（2026-08-13 監督判断。OPEN-QUESTIONS 参照）。
                // 三盗・本盗を打球の球で巻き戻した場合の「スタートの有利」は、走塁モデルが一塁走者の始動しか
                // 受け取らないため表現しない（走者は通常の始動で進む＝有利を捨てる保守側に倒す）。
                if (stealRecRunner is not null
                    && session.LastPitchKind is AtBat.PitchKind.Foul or AtBat.PitchKind.InPlay)
                {
                    bases.First = steal0First;
                    bases.Second = steal0Second;
                    bases.Third = steal0Third;
                    outs = steal0Outs;
                    offense.Runs = steal0Runs;
                    runsThisHalf = steal0RunsThisHalf;
                    offense.UndoSteal(stealRecRunner, stealRecSuccess);
                    // 盗塁死を上で投球回に計上済みなら、それも対で戻す（outs は steal0Outs へ戻している）。
                    if (!stealRecSuccess) defense.RecordRunnerOut(currentPitcher, -1);
                    if (stealRecScorer is not null)
                    {
                        offense.UndoRun(stealRecScorer);
                        ChargeRunnerScored(stealRecScorer, -1); // 責任投手の失点も対で戻す
                    }
                    if (stealDoubleBreak) offense.DoubleStealThirdBreakCount--;
                    stealPlay = null;   // 絵にも出さない＝同じ球のファウル打球の再生へ落ちる
                    if (session.LastPitchKind == AtBat.PitchKind.InPlay
                        && stealStartApplied is { } appliedStart)
                    {
                        r1Start = appliedStart;
                        r1StealBreak = true;   // stealStartApplied は二盗（一塁走者）でだけ立つ
                    }
                }

                // 1球ぶんの観測（設計書15 Phase D-1 のUI採用）: 投げ終えた球と、その時点の局面・対戦を控える。
                // 打席が確定する前に1球ずつ絵にするための供給口で、MatchProgression.AdvancePitch と対で使う。
                // 既に解決済みの値の写しだけ＝乱数を1回も追加消費しない（EmitPitchTrace と同じ位置・同じ流儀）。
                if (ctx.CapturePitches && session.PitchLog.Count > 0)
                {
                    var rec = session.PitchLog[session.PitchLog.Count - 1];
                    p.LastPitch = new Timeline.Playback.LivePitch
                    {
                        Pitch = Timeline.Playback.PitchTokenFactory.From(rec, ctx,
                            bases.First is not null, bases.Second is not null, bases.Third is not null),
                        EndsPlateAppearance = pitchRes.EndsPlateAppearance,
                        // 塁・アウトはこの球を投げ終えた時点の実状態（盗塁・牽制死で打席途中に動く）。
                        Outs = outs,
                        BaseFirst = bases.First is not null,
                        BaseSecond = bases.Second is not null,
                        BaseThird = bases.Third is not null,
                        // 得点も同じく「この球を投げ終えた時点」（issue #628）。本盗の生還はこの球より前に
                        // 解決済みなのでここに乗る。暴投・捕逸はこの後に判定するので、そちらで with し直す。
                        AwayScore = p.Away.Runs,
                        HomeScore = p.Home.Runs,
                        PitchNumberInPa = session.PitchCount,
                        // この球の前に盗塁が解決していれば、その再生データを載せる（issue #485）。
                        // 打席途中の盗塁はこの1球観測が唯一の運び口（打球は Current.Play が運ぶ）。
                        StealPlay = stealPlay,
                        // この球の前に牽制が発火していれば、その再生データを載せる（issue #486）。
                        PickoffPlay = pickoffPlay,
                    };
                }

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
                        EffectiveVelocityCeilingKmh = pitcher.MaxVelocityKmh,
                        EffectiveControl = pitcher.Control,
                        FamiliarityPa = abCtx.FamiliarityPa,
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
                    defense.RecordRunnerOut(currentPitcher); // スクイズ挟殺も投球回に数える
                    if (outs >= 3) { squeezeAbandoned = true; EmitPitchTrace(); break; }
                }

                // 暴投・パスボール（design-14 P2-8, 設計書15 Phase D-3）: 走者ありの各投球のうち、実際に
                // キャッチャーへ到達/通過した球（Ball/CalledStrike/SwingingStrike。Foul/InPlayは打者が
                // 触れた球なので対象外）で、低確率のバッテリーミスにより全走者が1つ進塁する（三塁走者は生還）。
                // 実際に暴投・捕逸が起きるのは、そのうち捕手が捕り損ねうる着弾コースの球（低い/大きく外れた）
                // だけ＝ゾーン内の捕れる球では起きない（issue #525・下の CanBatteryMiss ゲート）。
                // 暴投=投手責/パスボール=捕手責は意味上分けるが、記録は合算1カウントに簡略化（design-14の割り切り）。
                // 既定0（WildPitchProb=PassedBallProb=0）では走者の有無・投球結果によらず分岐自体に入らずrng消費ゼロ。
                if (bases.RunnerCount > 0
                    && session.LastPitchKind is PitchKind.Ball or PitchKind.CalledStrike or PitchKind.SwingingStrike
                    && (paBaserunning.WildPitchProb > 0.0 || paBaserunning.PassedBallProb > 0.0))
                {
                    var wpProb = MathUtil.Clamp(
                        paBaserunning.WildPitchProb - (pitcher.Control - 50) * paBaserunning.WildPitchControlSlope,
                        0.0, 1.0);
                    var pbProb = MathUtil.Clamp(
                        paBaserunning.PassedBallProb - (ctx.Fielding.AbilityEffect.Fielding(defense.Catcher.Catching) - 50) * paBaserunning.PassedBallCatchingSlope,
                        0.0, 1.0);
                    // 着弾コースのゲート（issue #525・監督判断 2026-08-07）: 捕手が捕り損ねうる球（低い球/大きく
                    // 外れた球）だけを対象にする。ゾーン内の捕れる球は構造的に対象外＝捕れる球が後逸にならない。
                    // 判定式は BaserunningModel.CanBatteryMiss に集約。rng（Chance）は従来どおり対象球ごとに1回引き、
                    // コースのゲートは Chance の後段（&& の第2項）に置く＝乱数消費の位置と回数を従来と揃え、
                    // 発生「率」だけを低い/外れた球へ絞る（狭めたぶん基準確率を再校正済み）。&& の短絡により
                    // Chance が外れた球ではコース評価をしない（CanBatteryMiss は乱数非消費なので順序は帯に無関係）。
                    if (MathUtil.Chance(wpProb + pbProb, rng)
                        && BaserunningModel.CanBatteryMiss(
                            session.PitchLog[^1].LocationX, session.PitchLog[^1].LocationY, ctx.StrikeZone, paBaserunning))
                    {
                        // 進塁前の走者配置を控える（issue #487の絵はこの写しから組む。ApplyBatteryMiss は
                        // bases を直接書き換えるので、呼び出し後には「誰から誰へ動いたか」が取れない）。
                        var wpFirst = bases.First;
                        var wpSecond = bases.Second;
                        var wpScorer = bases.Third; // 暴投/捕逸で生還するのは三塁走者（issue #77 得点帰属）
                        var wpRuns = BaserunningModel.ApplyBatteryMiss(bases);
                        offense.Runs += wpRuns;
                        runsThisHalf += wpRuns;
                        if (wpRuns > 0 && wpScorer is not null)
                        {
                            offense.RecordRun(wpScorer);
                            ChargeRunnerScored(wpScorer); // 失点は責任投手へ
                        }
                        offense.WildPitchCount++;
                        // 再生データ（issue #487）: この球は暴投判定が投球後に走るため p.LastPitch を
                        // 既に構築済み（EmitPitchTrace 手前の CapturePitches ブロック）＝with で追加する。
                        // 乱数を追加消費しない（BuildBatteryMiss は純関数）。
                        if (ctx.CaptureTimelines && p.LastPitch is { } lpForBatteryMiss)
                            p.LastPitch = lpForBatteryMiss with
                            {
                                BatteryMissPlay = Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(
                                    Timeline.TimelineBuilder.BuildBatteryMiss(
                                        wpFirst, wpSecond, wpScorer, defense.Catcher, paBaserunning, ctx.Field)),
                            };
                    }
                }

                // 得点を「この球を投げ終えた時点」へ揃え直す（issue #628）。LivePitch を組んだ後にも
                // その球ぶんの得点が入りうる（いまは暴投・捕逸での生還）ので、投球ごとの処理を
                // すべて終えたここで一度だけ上書きする＝発生源が増えても取りこぼさない。
                // 観測（CapturePitches）の担当なので CaptureTimelines のオン/オフとは無関係。乱数不使用。
                if (ctx.CapturePitches && p.LastPitch is { } lpScore
                    && (lpScore.AwayScore != p.Away.Runs || lpScore.HomeScore != p.Home.Runs))
                    p.LastPitch = lpScore with { AwayScore = p.Away.Runs, HomeScore = p.Home.Runs };

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
                > defense.EffectiveBaseStamina(currentPitcher) + ctx.MatchInjury.OveruseOverPitches)
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
                // タイムライン用に進塁前の走者を控える（AdvanceOnBunt は bases を直接書き換えるため、
                // 呼び出し後には「誰が動いたか」が取れない。issue #490）。
                var buntPreFirst = bases.First;
                var buntPreSecond = bases.Second;
                var buntPreThird = bases.Third;
                if (buntOutcome == BuntResult.SacrificeSuccess) AdvanceOnBunt(bases, batter: null);
                else if (buntOutcome == BuntResult.InfieldHit) AdvanceOnBunt(bases, batter);
                // バント打球のタイムライン（issue #490）: 判定（BuntResolver）はそのまま、結果から逆算した絵。
                // CaptureTimelines のときだけ組む＝統計シム・裏試合はゼロコスト・乱数消費ゼロ（他Buildと同作法）。
                var buntTimeline = ctx.CaptureTimelines
                    ? Timeline.TimelineBuilder.BuildBunt(
                        batter, fielders, buntOutcome,
                        buntPreFirst, buntPreSecond, buntPreThird,
                        bases.First, bases.Second, bases.Third,
                        paBaserunning, ctx.Field)
                    : null;
                FinishPlateAppearance(offense, defense, currentPitcher, batter,
                    res.Result, 0, buntOuts, res.Pitches, inning, isTop, log, ctx,
                    timeline: buntTimeline,
                    outsBefore: outs,
                    baseFirstBefore: buntPreFirst is not null, baseSecondBefore: buntPreSecond is not null, baseThirdBefore: buntPreThird is not null,
                    baseFirstAfter: bases.First is not null, baseSecondAfter: bases.Second is not null, baseThirdAfter: bases.Third is not null,
                    batterOrder: batterOrder, pitchLog: res.PitchLog,
                    isSacBunt: buntOutcome == BuntResult.SacrificeSuccess);
                outs += buntOuts;
                MaybeChangePitcher(p, defense, offense, ctx, rng, inning, isTop, log.Count, runsThisHalf, outs);
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
                // タイムライン用に進塁前の走者を控える（issue #490。バントと同じ理由）。
                var sqPreFirst = bases.First;
                var sqPreSecond = bases.Second;
                var sqPreThird = bases.Third;
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
                // 生還する三塁走者が引き継ぎ走者なら失点は責任投手へ（今の投手の行には積まない）。
                var sqOwnRuns = sq.Runs;
                if (sq.Runs > 0 && sqScorer is not null && !ResponsibleFor(sqScorer).Equals(currentPitcher))
                {
                    defense.ChargeRuns(ResponsibleFor(sqScorer), sq.Runs, sqUnearnedRuns);
                    sqOwnRuns = 0;
                    sqUnearnedRuns = 0;
                }
                // スクイズのタイムライン（issue #490）: バントと同じビルダーを使い回す（三塁走者の生還は
                // pre/post の突き合わせで自然に描かれる）。
                var sqTimeline = ctx.CaptureTimelines
                    ? Timeline.TimelineBuilder.BuildBunt(
                        batter, fielders, sq.Bunt,
                        sqPreFirst, sqPreSecond, sqPreThird,
                        bases.First, bases.Second, bases.Third,
                        paBaserunning, ctx.Field, isSqueeze: true)
                    : null;
                FinishPlateAppearance(offense, defense, currentPitcher, batter,
                    res.Result, sq.Runs, sqOuts, res.Pitches, inning, isTop, log, ctx,
                    pitcherRuns: sqOwnRuns,
                    timeline: sqTimeline,
                    outsBefore: outs,
                    baseFirstBefore: sqPreFirst is not null, baseSecondBefore: sqPreSecond is not null, baseThirdBefore: sqPreThird is not null,
                    baseFirstAfter: bases.First is not null, baseSecondAfter: bases.Second is not null, baseThirdAfter: bases.Third is not null,
                    batterOrder: batterOrder, pitchLog: res.PitchLog, unearnedRuns: sqUnearnedRuns,
                    isSacBunt: sq.Bunt == BuntResult.SacrificeSuccess);
                outs += sqOuts;
                MaybeChangePitcher(p, defense, offense, ctx, rng, inning, isTop, log.Count, runsThisHalf, outs);
                if (walkoff?.Invoke() == true)
                {
                    offense.RecordInningRuns(runsThisHalf);
                    yield return Pa();
                    yield break;
                }
                yield return Pa();
                continue;
            }

            // 打席前の塁状況を観測（塁ダイヤ／静止走者トークン用。追加記録のみ・判定不変, #3/#4）。
            // エンドランの空振り（次のブロック）が bases を書き換える前に控える＝issue #534。
            var preFirst = bases.First is not null;
            var preSecond = bases.Second is not null;
            var preThird = bases.Third is not null;

            // エンドランの空振り: スタートを切った一塁走者が憤死の危機（§1）。エンドランが効いたのが
            // 打席を終わらせた最後の球か（endrunOnLastPitch）で判定する（issue #492: brain は打席全体・手動は
            // その球だけに効くので、三振を喫した球でスタートを切っていたかを見る）。
            var extraOutsFromSign = 0;
            if (endrunOnLastPitch && bases.First is not null && res.Result == PlateAppearanceResult.Strikeout)
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
                // 経路は null＝着地点フォールバックなので従来と一致する（Issue #315）。
                var homePlaySituation = new HomePlaySituation(
                    new Vector3D(fp.FieldedX ?? fp.LandingX, 0, fp.FieldedZ ?? fp.LandingZ),
                    fp.FieldedAtSeconds ?? fp.HangTimeSeconds,
                    arm,
                    fp.FielderFielding);
                // aggression は中立固定（校風/ティア/采配の三層写像は残Q10）。
                // 守備の内野深さ（G1）＝ゴロ凡打の三塁走者判定へ「前進で刺す/後退で献上」を反映。
                // ThrowArriveSeconds（一塁送球到達時刻）はゴロ凡打の進塁打（Issue #318）分岐Cの転送起点。
                // BatterToFirstSeconds（打者走者の一塁到達）は併殺の転送レース（Issue #369）の二段目。
                // 「併殺の名手」（design-10）: 二塁経由の転送が速い＝守備側の遊撃手/二塁手が持つとき転送時間を短縮。
                var dpPivotBonus = (defense.PlayerAtPosition(Field.FieldPosition.SecondBase)?.Skills.Has(Skill.DoublePlayArtist) == true
                                    || defense.PlayerAtPosition(Field.FieldPosition.Shortstop)?.Skills.Has(Skill.DoublePlayArtist) == true)
                    ? ctx.Skills.DoublePlayArtistBonus : 0.0;
                // 併殺ピボットの中継球速（issue⑦: 従来 RelayThrowSpeedMps 固定で二塁手/遊撃手の地肩が
                // 併殺成立率に無効だった）。二塁カバーへ実際に入るのは「処理した側と逆の中堅手」
                // （FormationTable の全ゾーンでこの対応＝遊撃手/三塁手が処理→二塁手がカバー、それ以外
                // （二塁手/一塁手/投手/捕手）が処理→遊撃手がカバー）。判定層はゾーン表を持たないので、
                // 処理役から同じ対応を導く（DefensiveFormation.BuildGenericRules と等価）。
                var dpPivotRole = fp.FielderRole is Field.FieldPosition.ThirdBase or Field.FieldPosition.Shortstop
                    ? Field.FieldPosition.SecondBase : Field.FieldPosition.Shortstop;
                var dpPivotArm = defense.PlayerAtPosition(dpPivotRole)?.ToFielder(ctx.Baserunning.AbilityEffect).ThrowSpeedMps;
                homePlay = new HomePlayContext(
                    ctx.Field, homePlaySituation, ctx.Tactics, 0.5, defTactics.Infield, fp.IsFly,
                    fp.ThrowArriveSeconds, fp.BatterToFirstSeconds, dpPivotBonus, dpPivotArm,
                    FielderRole: fp.FielderRole);
            }

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
            // 失策種別（issue #629）: 連鎖は悪送球にだけ乗る。強制結果（設計書17 §6.1）は物理層を
            // 1回も回さない＝res.Play ごと無く種別も取れないので、注入で連鎖を再現できるよう悪送球とみなす
            // （実試合の失策は必ず FieldingResolver 由来の Play を持つので、この既定へは落ちない）。
            var errorKind = res.Play?.ErrorKind
                ?? (res.Result == PlateAppearanceResult.ReachedOnError ? Match.Fielding.FieldingErrorKind.Throw : null);
            // collectMoves は常に true（GameEngine は自校の詳細試合専用＝裏試合は通らない, RNG中立）。
            // 得点帰属（issue #77）に本塁到達 RunnerMove を使うため、観戦しない試合でも moves を収集する。
            // 走塁係数は paBaserunning（設計書17 §6.1, F4）。非強制時は ctx.Baserunning と同一参照＝従来と不変。
            var (runs, extraOuts, baseOuts, batterSafeOnFc, errorExtraAdvanceOccurred, runnerMoves) = BaserunningModel.ApplyDetailed(
                bases, res.Result, batter, outs, paBaserunning, rng, collectMoves: true,
                homePlay, r1Start, errorThrowAccuracy, errorKind, r1StealBreak);
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
            // 失点の帰属（issue inherited-runner-run-attribution）: 走者ごとに責任投手を引き、今の投手の
            // 分（ownRuns）だけを打席の投手行へ、引き継ぎ走者の分は責任投手へ直接付ける。
            var unearnedRunsThisPa = 0;
            var ownRunsThisPa = 0;
            foreach (var m in runnerMoves)
            {
                if (m.ToBase >= 4 && !m.Out)
                {
                    offense.RecordRun(m.Runner);
                    var unearned = unearnedRunners.Contains(m.Runner);
                    var owner = ResponsibleFor(m.Runner);
                    if (owner.Equals(currentPitcher))
                    {
                        ownRunsThisPa++;
                        if (unearned) unearnedRunsThisPa++;
                    }
                    else
                    {
                        defense.ChargeRuns(owner, 1, unearned ? 1 : 0);
                    }
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
                    // フライ捕球なら捕球時刻を渡す＝進塁する走者は全員タッチアップ（捕球起点）になる。
                    var flyCaughtAt = res.Play is { IsFly: true, Result: Match.Fielding.BattedBallResult.Out } caughtFly
                        ? caughtFly.FieldedAtSeconds ?? caughtFly.HangTimeSeconds   // 接触からの秒（TagUpCatchAtSeconds と同じ基準）
                        : (double?)null;
                    // 失策の連鎖（design-14 P1-6）で同一走者に積まれた続きの move は、失策の瞬間より前に
                    // 走り出させない（issue error-extra-advance-phantom-runner）。悪送球＝一塁へ届くはずだった時刻。
                    var errorAt = res.Play is { Result: Match.Fielding.BattedBallResult.Error } chainErrPlay
                        ? chainErrPlay.ErrorAtSeconds
                          ?? (chainErrPlay.ErrorKind == Match.Fielding.FieldingErrorKind.Throw ? chainErrPlay.ThrowArriveSeconds : null)
                        : null;
                    timeline = Match.Timeline.TimelineBuilder.AppendRunnerLegs(
                        timeline, runnerMoves, ctx.Field, paBaserunning, ctx.Fielding, flyCaughtAt, errorAt);

                    // 併殺(二塁封殺)かつ内野ゴロなら、ボールの送球連鎖を 6-4-3 型に差し替える（設計書12 §2, F1 Slice 3）。
                    // 野選（FC, design-14 P1-1）も同じ二塁封殺 RunnerMove を積む。BuildBattedBall は二塁封殺・
                    // 野選の区別を知らずに「打」レッグ・判定を一塁アウトとして作ってしまっている（打球の基礎
                    // 解決＝直接送球1本の前提で組むため）ので、FCでも呼んで実際の結末（打者セーフ）へ
                    // 描き直す（issue double-play-throw-pace-and-second-out-call の追加報告。以前は !batterSafeOnFc
                    // で除外していたため、野選が「打者も一塁アウト＝併殺成立」に誤って見えていた）。
                    var hasForceAtSecond = false;
                    RunnerMove? selfForceLead = null;
                    foreach (var m in runnerMoves)
                    {
                        if (m.Out && m.FromBase == 1 && m.ToBase == 2) hasForceAtSecond = true;
                        else if (m.SelfForceDoublePlayLead) selfForceLead = m;
                    }
                    if (hasForceAtSecond && res.Play is { IsFly: false } dpPlay)
                    {
                        // 打者走者の一塁到達（"打"レッグ終端）を食い違いガードのアンカーにする。
                        var batterArrive = timeline.Duration;
                        foreach (var leg in timeline.Runners)
                        {
                            if (leg.Label == "打") { batterArrive = leg.T1; break; }
                        }
                        // 判定側（BaserunningModel.ResolveDoublePlay）と同じ値を渡す＝絵と判定が同じ秒で組まれる
                        // （issue double-play-throw-pace-and-second-out-call 案B/C）。
                        var dpPivotBonusForDisplay =
                            (defense.PlayerAtPosition(Field.FieldPosition.SecondBase)?.Skills.Has(Skill.DoublePlayArtist) == true
                             || defense.PlayerAtPosition(Field.FieldPosition.Shortstop)?.Skills.Has(Skill.DoublePlayArtist) == true)
                            ? ctx.Skills.DoublePlayArtistBonus : 0.0;
                        var pivotSprintSpeedByRole = new Dictionary<Field.FieldPosition, double>
                        {
                            [Field.FieldPosition.SecondBase] =
                                defense.PlayerAtPosition(Field.FieldPosition.SecondBase)?.ToFielder(ctx.Baserunning.AbilityEffect).SprintSpeedMps
                                    ?? new FielderAttributes().SprintSpeedMps,
                            [Field.FieldPosition.Shortstop] =
                                defense.PlayerAtPosition(Field.FieldPosition.Shortstop)?.ToFielder(ctx.Baserunning.AbilityEffect).SprintSpeedMps
                                    ?? new FielderAttributes().SprintSpeedMps,
                        };
                        // 中継球速（issue⑦）: 絵側の二塁カバー役は AppendDoublePlayThrows 内部の実際の陣形
                        // （CoverRoleAt）で決まる＝2B/SSどちらが来ても引けるよう両方を渡す。判定側
                        // （HomePlayContext.DoublePlayPivotArmMps）とは別経路の辞書だが、実際に選ばれる役
                        // （二塁手/遊撃手のどちらが処理側と反対か）は同じ対応（DefensiveFormation）から
                        // 出るので値は一致する。
                        var pivotThrowSpeedByRole = new Dictionary<Field.FieldPosition, double>
                        {
                            [Field.FieldPosition.SecondBase] =
                                defense.PlayerAtPosition(Field.FieldPosition.SecondBase)?.ToFielder(ctx.Baserunning.AbilityEffect).ThrowSpeedMps
                                    ?? new FielderAttributes().ThrowSpeedMps,
                            [Field.FieldPosition.Shortstop] =
                                defense.PlayerAtPosition(Field.FieldPosition.Shortstop)?.ToFielder(ctx.Baserunning.AbilityEffect).ThrowSpeedMps
                                    ?? new FielderAttributes().ThrowSpeedMps,
                        };
                        timeline = Match.Timeline.TimelineBuilder.AppendDoublePlayThrows(
                            timeline, dpPlay, ctx.Field, batterArrive, ctx.Baserunning, dpPivotBonusForDisplay,
                            pivotSprintSpeedByRole, batterOut: !batterSafeOnFc,
                            pivotThrowSpeedByRole: pivotThrowSpeedByRole);
                    }
                    // 併殺の代替ルート（三塁/本塁の自己フォース, Q31）: 三塁手/捕手/投手が自打球を自分でベースへ
                    // 運んで封殺→一塁へ自己送球。上の二塁封殺(hasForceAtSecond)とは互いに排他
                    // （BaserunningModel.ResolveDoublePlay がどちらか一方の経路しか選ばないため）。
                    else if (selfForceLead is { } sfl && res.Play is { IsFly: false } sfPlay)
                    {
                        var batterArrive = timeline.Duration;
                        foreach (var leg in timeline.Runners)
                        {
                            if (leg.Label == "打") { batterArrive = leg.T1; break; }
                        }
                        timeline = Match.Timeline.TimelineBuilder.AppendSelfForceDoublePlayThrows(
                            timeline, sfPlay, ctx.Field, batterArrive, sfl.ToBase, ctx.Baserunning,
                            batterOut: !batterSafeOnFc);
                    }

                    // 飛び出しの勝負（フライ捕球→一塁走者が戻る vs 戻し送球）: 戻し送球を描く
                    // （issue fly-out-runner-dp-table-and-doubled-off-depiction）。走者レッグは AppendRunnerLegs が積む。
                    if (res.Play is { IsFly: true } doPlay)
                    {
                        timeline = Match.Timeline.TimelineBuilder.AppendDoubledOffThrow(
                            timeline, doPlay, runnerMoves, ctx.Field, paBaserunning);
                    }
                }

                // 打席前から塁上にいて動かなかった走者を静止トークンで描く（#3）。移動走者とは二重に描かない。
                // 2アウトのフライアウトはタッチアップ不要のため、静止させず接触直後にスタートさせる（#299）。
                timeline = Match.Timeline.TimelineBuilder.AppendHeldRunners(
                    timeline, preFirst, preSecond, preThird,
                    preFirstPlayer, preSecondPlayer, preThirdPlayer,
                    runnerMoves, ctx.Field, outsBefore, res.Play, paBaserunning);

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
                        // 複数生還するプレーでは**最後に還る走者**に送球を合わせる（issue
                        // match-picture-vs-safe-out-verdict）。走者レッグは三塁走者→二塁走者→一塁走者の順に
                        // 積まれるので、先に見つかったものを採ると<b>いちばん早く還る走者</b>に固定され、
                        // 後から還る走者は捕手がボールを持ったあとに悠々とホームインする絵になっていた
                        // （実測 300試合で189件・中央値0.82秒・最大1.76秒の先着）。すぐ下の外野返球の枝と同じ形。
                        if (safeAtHome is null || leg.T1 > safeAtHome.T1) safeAtHome = leg;
                    }
                    if (outAtHome is { } o)
                    {
                        timeline = Match.Timeline.TimelineBuilder.AppendBackHomeThrows(
                            timeline, bhPlay, ctx.Field, o.T1, runnerOut: true, paBaserunning);
                        homeThrowAdded = true;
                    }
                    else if (safeAtHome is { } s && (bhPlay.RangeM > 46.0 || bhPlay.ThroughInfield))
                    {
                        // 外野安打での生還は際どくなくても返球を描く（ボールが外野で死なない, #7）。
                        timeline = Match.Timeline.TimelineBuilder.AppendBackHomeThrows(
                            timeline, bhPlay, ctx.Field, s.T1, runnerOut: false, paBaserunning);
                        homeThrowAdded = true;
                    }
                }

                // 本塁送球が無い外野安打は、先頭走者の到達塁へ返球してボールを内野へ戻す（#6）。
                // 狙う塁は**走塁結果から**決める（issue #627）。打者の塁打数だけで決めていた頃は、単打で
                // 一塁走者が三塁まで進んでも二塁へ返していた。返球到達のアンカーも同じ走者のレッグ終端に
                // 揃える＝打者走者に紐付けると「三塁へ投げるが打者の一塁到達に合わせて着く」ことになる。
                // トンネル（issue #632）も外野まで球が行く＝返球しないと外野手が球を持ったまま棒立ちになる
                // （issue #629 ③と同じ症状）。ただしお手玉・悪送球・フライ落球は「拾い直し」を持つので、
                // 下の AppendErrorReturnThrow が投げる＝二重に積まないよう ErrorPickupAt が無い失策だけを拾う。
                if (!homeThrowAdded && res.Play is { } ofHit && (ofHit.RangeM > 46.0 || ofHit.ThroughInfield)
                    && (ofHit.Result is Match.Fielding.BattedBallResult.Single
                            or Match.Fielding.BattedBallResult.Double or Match.Fielding.BattedBallResult.Triple
                        || (ofHit.Result == Match.Fielding.BattedBallResult.Error && timeline.ErrorPickupAt is null)))
                {
                    var returnTo = Match.Timeline.TimelineBuilder.ResolveOutfieldReturnTarget(runnerMoves, ofHit.Result);
                    var runnerArrive = 0.0;
                    foreach (var leg in timeline.Runners)
                        if (leg.Label == returnTo.AnchorLabel && leg.T1 > runnerArrive) runnerArrive = leg.T1;
                    // 中継しきい値は肩連動（issue #526）＝判定と同じ係数を渡して絵と食い違わせない
                    timeline = Match.Timeline.TimelineBuilder.AppendOutfieldReturnThrow(
                        timeline, ofHit, ctx.Field, returnTo.BaseName, runnerArrive, ctx.Fielding, paBaserunning,
                        returnTo.RunnerOut);
                }

                // 失策でこぼれた球を拾った野手は内野へ投げ返す（issue #629 ③）。積まないと、拾った野手が
                // 球を持ったまま棒立ちで、走者が本塁まで走り抜けるのを眺めている絵になる。
                // 本塁クロスプレーを描いた打席では投げない＝上の AppendBackHomeThrows と二重にならない。
                if (!homeThrowAdded && res.Play is { } errPlay && timeline.ErrorPickupAt is not null)
                {
                    timeline = Match.Timeline.TimelineBuilder.AppendErrorReturnThrow(timeline, errPlay, ctx.Field);
                }
            }

            // 振り逃げ（第3ストライク不捕球, design-14 P1-2）: 一塁空き or 2アウトの三振でのみ判定。
            // 既定オフ(DropThirdStrikeReachProb=0)では分岐自体に入らずrng消費ゼロ。投手にはKを計上したまま
            // 打者はアウトにしない（PlateAppearanceResult自体は Strikeout のまま変えない＝打数1・無安打は自動的に満たす）。
            // 着弾コースのゲート（issue #525・監督判断 2026-08-07）: 暴投・捕逸と同じ条件を振り逃げにも掛ける。
            // 第3ストライクが捕手の捕り損ねうる球（低い/大きく外れた球）のときだけ成立＝ゾーン内で見逃した/空振り
            // した「捕れる球」では起きない。暴投・捕逸と同じく rng（Chance）は従来どおり対象三振ごとに1回引き、
            // コースのゲートは Chance の後段（&& の第2項）に置く＝乱数消費の位置と回数を従来と揃える。
            var droppedThirdStrikeReached = false;
            if (res.Result == PlateAppearanceResult.Strikeout && (bases.First is null || outs == 2)
                && paBaserunning.DropThirdStrikeReachProb > 0.0)
            {
                var reachProb = MathUtil.Clamp(
                    paBaserunning.DropThirdStrikeReachProb
                    - (ctx.Fielding.AbilityEffect.Fielding(defense.Catcher.Catching) - 50) * paBaserunning.DropThirdStrikeCatchingSlope,
                    0.0, 0.95);
                if (MathUtil.Chance(reachProb, rng)
                    && session.PitchLog.Count > 0
                    && BaserunningModel.CanBatteryMiss(
                        session.PitchLog[^1].LocationX, session.PitchLog[^1].LocationY, ctx.StrikeZone, paBaserunning))
                {
                    droppedThirdStrikeReached = true;
                    // 一塁を与える＝**押し出しを伴う**（issue #525）。2アウトの振り逃げは一塁が埋まっていても
                    // 成立するので、bases.First へ直に代入すると一塁走者が消える（走者保存則違反。400試合中4件を
                    // Heavy が検出。旧確率 0.001 では滅多に踏まなかった経路が、着弾コースゲートに伴う再校正
                    // 0.39 で表に出た）。四球・死球と同じ規則を使う＝満塁なら三塁走者が生還する。
                    // 乱数は引かない（AwardFirstBase は純粋な塁の付け替え）。
                    // 走者の脚は timeline に足さない＝振り逃げの絵は BuildDropThirdStrike（打者→一塁と捕手の
                    // 処理）が担当で、押し出される走者の描画は別途（この打席の timeline は既に確定済み）。
                    var (dtsRuns, _) = BaserunningModel.AwardFirstBase(bases, batter);
                    runs += dtsRuns;
                    offense.DroppedThirdStrikeCount++;
                    // 再生データ（issue #488）: この球は三振確定後の後処理として判定されるため p.LastPitch を
                    // 既に構築済み（この打席の最終球＝CapturePitches ブロックで既に控えている）＝with で追加する。
                    // 乱数を追加消費しない（BuildDropThirdStrike は純関数）。
                    if (ctx.CaptureTimelines && p.LastPitch is { } lpForDropThirdStrike)
                        p.LastPitch = lpForDropThirdStrike with
                        {
                            DropThirdStrikePlay = Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(
                                Timeline.TimelineBuilder.BuildDropThirdStrike(batter, defense.Catcher, paBaserunning, ctx.Field)),
                        };
                }
            }

            var batterOut = BaserunningModel.IsBatterOut(res.Result, batterSafeOnFc, droppedThirdStrikeReached);
            var outsThisPa = (batterOut ? 1 : 0) + extraOuts + extraOutsFromSign;

            offense.HomePlayOuts += homeOuts; // 本塁クロスプレー憤死（F2外野＋G1内野ゴロ, 統計参考値）
            offense.ThirdPlayOuts += thirdOuts; // 単打の一塁→三塁レース憤死（Issue #89, 統計参考値）
            if (batterSafeOnFc) offense.FieldersChoiceCount++; // 野選（FC, design-14 P1-1。統計参考値）
            // 併殺打（GIDP, Issue #369）: 二塁封殺（1→2 Out）成立かつ打者一塁アウト（野選でない）の内野ゴロ。
            // AppendDoublePlayThrows は野選（FC）でも呼ぶ（打者を実際の結末へ描き直すため）が、
            // この集計は従来どおり !batterSafeOnFc だけを併殺打として数える。
            if (!batterSafeOnFc && res.Play is { IsFly: false })
            {
                foreach (var m in runnerMoves)
                {
                    if (m.Out && m.FromBase == 1 && m.ToBase == 2) { offense.DoublePlayCount++; break; }
                }
            }

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

            // 失策の個人帰属（issue #91）: 守備位置から当該選手を引く。守備位置は必ず埋まっている
            // ため通常は解決できるが、万一 null でもチーム計（AddError相当）は FinishPlateAppearance 側で必ず加算する。
            // ⚠ トンネル（issue #632）は<b>処理野手が回収する外野手</b>なので FielderRole では別人に付く。
            // 失策は抜かれた内野手（ErrorFielderRole）に付ける（他の種別では null＝FielderRole と同じ）。
            Player? errorFielder = null;
            Match.Field.FieldPosition errorRole = default;
            if (res.Result == PlateAppearanceResult.ReachedOnError
                && (res.Play?.ErrorFielderRole ?? res.Play?.FielderRole) is { } eRole)
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
                batterOrder, res.PitchLog, errorFielder, errorRole, isSacFly, unearnedRunsThisPa,
                pitcherRuns: ownRunsThisPa,
                play: res.Play);
            // 敬遠（design-14 P1-3・設計書15 Phase D-2a）: 統一ステッパ経由でも、四球の内訳として別記録する。
            if (defTactics.IntentionalWalk) offense.IntentionalWalkCount++;

            outs += outsThisPa;

            MaybeChangePitcher(p, defense, offense, ctx, rng, inning, isTop, log.Count, runsThisHalf, outs);

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
        int unearnedRuns = 0,
        Match.Fielding.FieldingPlay? play = null,
        bool isSacBunt = false,
        int? pitcherRuns = null)
    {
        if (result.IsHit()) offense.AddHit();
        if (result == PlateAppearanceResult.ReachedOnError) defense.RecordFieldingError(errorFielder, errorRole);
        offense.RecordBatting(batter, result, runs, isSacFly);
        offense.RecordMatchup(batter, pitcher);   // 慣れの蓄積（設計書02 §2.3b）
        // 打席確定球の球種（issue #180）: インプレーで終わった打席のみ末尾の PitchRecord が決め球。
        PitchType? decisivePitch = pitchLog is { Count: > 0 } pl && pl[^1].Kind == PitchKind.InPlay
            ? pl[^1].PitchType
            : null;
        // pitcherRuns＝この打席の投手に付ける失点（引き継ぎ走者の分は呼び出し側が責任投手へ付けた後）。
        // 省略時は runs 全部＝走者の由来を持たない経路（バント等・得点なし）向け。
        defense.RecordPitching(pitcher, result, pitcherRuns ?? runs, outsThisPa, pitches, decisivePitch, unearnedRuns);
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
            batter.ConditionValue, pitcher.ConditionValue,
            // 打球観測（本日成績の略記列用）: 既に確定した FieldingPlay の値を写すだけ＝判定・乱数順・digest不変。
            // ⚠ 失策の記録は「処理した野手」ではなく「失策を犯した野手」に付く。トンネル（issue #632）は
            // 球を回収するのが外野手なので、ErrorFielderRole を優先しないと「左失」（＝外野失策）になる。
            play?.ErrorFielderRole ?? play?.FielderRole, play?.IsFly ?? false, play?.Class, play?.BearingDeg,
            outsThisPa, isSacFly, isSacBunt));
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

    /// <summary>
    /// 継投判断（設計書11 §4, issue #209）。打席境界ごとに呼ぶ。采配Brainがあれば <see cref="ITacticsBrain.CallPitchingChange"/>
    /// に委ね（Standard＝従来の疲労球数トリガーそのまま／AI＝高度トリガー＋三層を被せる）、無指示（Brain=null）なら
    /// 従来の engine 直判定を完全に踏襲する（＝帯不変）。判断RNGは専用Forkストリームで引くため試合結果RNGを進めない
    /// （不変条件#2・裏試合帯不変）。人選は Phase A では bullpen 先頭のまま。
    /// </summary>
    private static void MaybeChangePitcher(
        GameProgress p, TeamState defense, TeamState offense, GameContext ctx, IRandomSource rng,
        int inning, bool isTop, int paIndex, int runsThisHalf, int outs)
    {
        var fatigue = defense.Fatigue ?? ctx.Fatigue;
        var staminaTarget = defense.EffectiveBaseStamina(defense.CurrentPitcher);
        var fatigueTriggered = PitchingFatigue.ShouldRelieve(
            defense.CurrentPitcher, defense.FatiguePitches, fatigue, staminaTarget);
        var atWeekly = defense.CurrentPitcherAtWeeklyLimit();

        var changed = false;
        if (defense.Tactics is { } brain)
        {
            // 継投人選の候補＝控え全員＋在フィールドのレギュラー野手（issue #209 Phase B/C）。
            // 在フィールド野手が選ばれたら RelieveWith が投野交代で守備穴を backfill する。
            var candidates = defense.ReliefRankingCandidates;
            var sit = new PitchingChangeSituation(
                inning, ctx.RegulationInnings, outs,
                defense.Runs - offense.Runs, runsThisHalf, defense.PitcherRattled,
                fatigueTriggered, atWeekly, defense.FatiguePitches, staminaTarget,
                fatigue.RelievePitchMargin, candidates.Count > 0, candidates,
                // コンバート継投（issue #255 B-1）の判断材料。既定Brainは参照しない＝恒等。
                defense.CurrentPitcher, defense.CurrentLineup, SwappableSlotsOf(defense), defense.UsesDh);
            var forked = rng.Fork(ReliefStreamId(inning, isTop, paIndex));
            if (brain.CallPitchingChange(sit, forked) is { } decision)
            {
                // ConvertTo 指定はコンバート継投＝元投手をその守備位置へ回して Reliever を登板（issue #255）。
                // Reliever 指定だけならその投手を指名（横断ランキング）。在フィールド野手なら投野交代で backfill。
                // どちらも null は engine 既定＝名簿順で最初の投手枠の控え（StandardTacticsBrain／無指示は常に null＝
                // 従来挙動と1ビットも変わらない＝帯不変）。
                changed = decision switch
                {
                    { ConvertTo: { } to, Reliever: { } r } => defense.ConvertPitcherAndRelieve(to, r)
                                                              || defense.RelieveWith(r, ctx.ReliefBackfill),
                    { Reliever: { } reliever } => defense.RelieveWith(reliever, ctx.ReliefBackfill),
                    _ => defense.TryChangePitcher(),
                };
            }
        }
        else if (!ctx.PlayerControlsPitcherChanges && (fatigueTriggered || atWeekly))
        {
            // PlayerControlsPitcherChanges が真の自校だけは何もしない：疲労のみ（fatigueTriggered）は
            // 目安に過ぎないので自動交代せずプレイヤーの采配権のまま、atWeekly はルール上の強制だが
            // engine で機械的に選ばず MatchProgression.PendingForcedPitcherChange 側で人選待ちにする。
            // 既定（false＝裏試合・統計シム・既存テスト）はここに来ず従来どおり1ビットも変わらない。
            changed = defense.TryChangePitcher();
        }

        // 交代が成立した時だけ観測ログへ積む（issue #209 表現。観測専用＝決定論・帯には影響しない）。
        if (changed) p.RecordPitchingChange(defense, isTop);
    }

    /// <summary>継投判断RNG用Fork streamId（issue #209）。1球采配/怪我/天候とは別saltで衝突を避ける。</summary>
    private static ulong ReliefStreamId(int inning, bool isTop, int paIndex)
        => 0xC0FFEE_2090_0000UL ^ ((ulong)(uint)inning << 32) ^ ((isTop ? 1UL : 0UL) << 31) ^ (ulong)(uint)paIndex;

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
        // 盗塁死。投球回にも数える（完投=27アウトの保証）。
        defense.RecordRunnerOut(defense.CurrentPitcher);
        bases.First = null;
        return 1;
    }

    // ===== 選手交代（設計書09 §6, C-2）。控えが非空かつBrainありの時だけ判断＝既定オフで従来一致。 =====

    /// <summary>守備側のイニング頭の守備固め判断。</summary>
    private static void MaybeDefensiveSub(
        TeamState defense, TeamState offense, GameContext ctx, IRandomSource rng, int inning, BaseState bases)
    {
        if (defense.Tactics is null || defense.Reserves.Count == 0) return;
        var sit = SubSituationFor(defense, offense, ctx, inning, outs: 0, bases);
        if (defense.Tactics.CallDefensiveSub(sit, rng) is { } d)
        {
            defense.DefensiveSub(d.Out, d.Sub);
        }
    }

    /// <summary>
    /// 守備側のイニング頭の守備位置入替（コンバート）判断（issue #255, 設計書09 §2.3）。
    /// 交代回数を消費しない手なので<b>控えが居なくても</b>問い合わせる（守備固めと違い Bench 条件が無い）。
    /// 判断RNGは継投と同じく専用Forkストリームで引くため試合結果RNGを進めない（不変条件#2・裏試合帯不変）。
    /// 既定Brain（Standard／無指示）は常に null を返すので、従来の試合結果と1ビットも変わらない。
    /// </summary>
    private static void MaybeDefenseSwap(
        TeamState defense, TeamState offense, GameContext ctx, IRandomSource rng, int inning, bool isTop)
    {
        if (defense.Tactics is null) return;
        var sit = new Tactics.DefenseSwapSituation(
            inning, ctx.RegulationInnings, defense.Runs - offense.Runs,
            defense.CurrentLineup, SwappableSlotsOf(defense));
        var forked = rng.Fork(DefenseSwapStreamId(inning, isTop));
        if (defense.Tactics.CallDefenseSwap(sit, forked) is { } d)
        {
            defense.SwapDefensivePositions(d.SlotA, d.SlotB);
        }
    }

    /// <summary>守備位置入替の判断RNG用Fork streamId（issue #255）。継投・1球采配・怪我とは別saltにする。</summary>
    private static ulong DefenseSwapStreamId(int inning, bool isTop)
        => 0xD3F5_5AA9_0000UL ^ ((ulong)(uint)inning << 32) ^ (isTop ? 1UL : 0UL);

    /// <summary>打順スロットごとの「守備位置を動かせるか」（投手枠・DH枠は false）。</summary>
    private static bool[] SwappableSlotsOf(TeamState team)
    {
        var flags = new bool[team.CurrentLineup.Count];
        for (var i = 0; i < flags.Length; i++) flags[i] = team.IsSwappableDefenseSlot(i);
        return flags;
    }

    /// <summary>攻撃側の打席頭の代走・代打判断（代走が先＝走者を確定してから打者を替える）。</summary>
    private static void MaybeOffenseSubs(
        GameProgress p, TeamState offense, TeamState defense, GameContext ctx, IRandomSource rng, int inning, int outs, BaseState bases)
    {
        if (offense.Tactics is null || offense.Reserves.Count == 0) return;

        // 代走: 塁上の鈍足走者を速い控えへ。塁の参照も差し替える。
        if (offense.Tactics.CallPinchRun(SubSituationFor(offense, defense, ctx, inning, outs, bases), rng)
            is { } r && offense.PinchRunFor(r.Runner, r.Sub, out var inserted))
        {
            // 塁上の参照は engine が実際に打順へ入れた実体へ揃える（ここで組み直すと打順の実体とズレる）。
            if (bases.First == r.Runner) bases.First = inserted;
            else if (bases.Second == r.Runner) bases.Second = inserted;
            else if (bases.Third == r.Runner) bases.Third = inserted;
            // 代走は元走者の責任投手を引き継ぐ（issue inherited-runner-run-attribution）。
            if (inserted is not null) p.TransferRunnerResponsibility(r.Runner, inserted);
        }

        // 代打: 次打者を厚い控えへ（投手枠はStandard側で除外）。
        if (offense.Reserves.Count > 0
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
            team.CurrentLineup, team.Reserves);
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
    /// <summary>継投のログ（issue #209 表現。継投・投野交代・野手登板の各1件）。観測専用＝試合結果・決定論には影響しない。</summary>
    public List<PitchingChangeEvent> PitchingChanges { get; } = new();

    /// <summary>
    /// 直前に投げた1球の観測（設計書15 Phase D-1 のUI採用）。<see cref="GameContext.CapturePitches"/> が
    /// 真のときだけ毎球書き換わる。観測専用＝読み書きとも乱数を消費せず、結果・digest・統計帯に影響しない。
    /// </summary>
    public Timeline.Playback.LivePitch? LastPitch { get; set; }

    /// <summary>
    /// これから投げる1球の局面（設計書15 Phase D-1 のUI採用）。采配窓ごと＝各投球の手前で書き換わり、
    /// <b>打席の頭でも埋まる</b>。<see cref="GameContext.CapturePitches"/> が真のときだけ。観測専用。
    /// </summary>
    public Timeline.Playback.LiveAtBat? LiveAtBat { get; set; }

    /// <summary>
    /// 守備側 <paramref name="defense"/> の直近の継投（<see cref="TeamState.LastPitchingChange"/>）を、
    /// 現イニング・攻守を付けて <see cref="PitchingChanges"/> ログへ積む（issue #209 表現）。交代確定直後に呼ぶ。
    /// <paramref name="isTop"/> は起きた半イニング（表＝先攻の攻撃）。観測専用。
    /// </summary>
    public void RecordPitchingChange(TeamState defense, bool isTop)
    {
        if (defense.LastPitchingChange is { } change)
            PitchingChanges.Add(new PitchingChangeEvent(Inning, isTop, ReferenceEquals(defense, Away), change));
    }

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

    // ===== 引き継ぎ走者の責任投手（issue inherited-runner-run-attribution・野球規則 9.16(g)） =====
    // 走者 → その走者を出塁させた投手。生還時の失点・自責点はこの投手へ付ける（救援は自分が出した走者と
    // 打者走者ぶんだけ）。半イニング内で使い切り（PlayHalfSteps の頭で空にする）。記録のみ＝乱数非消費・
    // 試合進行には一切影響しない。代走は元走者の責任投手を引き継ぐ（<see cref="TransferRunnerResponsibility"/>）。
    /// <summary>走者→責任投手（今の半イニングぶん）。走者の実体は塁上の参照と同じ（record の値等価で引く）。</summary>
    internal Dictionary<Player, Player> RunnerResponsibility { get; } = new();

    /// <summary>代走: 元走者 <paramref name="from"/> の責任投手を、塁に入る <paramref name="to"/> へ引き継ぐ。</summary>
    internal void TransferRunnerResponsibility(Player from, Player to)
    {
        if (RunnerResponsibility.TryGetValue(from, out var pitcher))
        {
            RunnerResponsibility.Remove(from);
            RunnerResponsibility[to] = pitcher;
        }
    }

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
