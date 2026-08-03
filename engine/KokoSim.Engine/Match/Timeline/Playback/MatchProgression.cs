using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// 1打席の観戦データ（対話進行の逐次供給）。タイムラインがある打球/盗塁のみ <see cref="Play"/> が非null。
/// 三振・四球などタイムラインを持たない打席は Play=null で、UI は結果テキストだけを短く見せる。
/// </summary>
public sealed class LivePlateAppearance
{
    public required int Inning { get; init; }
    public required bool IsTop { get; init; }
    public required int AwayScore { get; init; }
    public required int HomeScore { get; init; }
    public required string BatterName { get; init; }
    public required int RunsScored { get; init; }
    public required PlateAppearanceResult Result { get; init; }
    public PlaybackPlay? Play { get; init; }

    // ── 中継風カウント表示用（#4）。すべて観測データで結果には影響しない ──
    /// <summary>合成した投球列（B/S を1球ずつ点灯させる。結果と整合・独立シード）。</summary>
    public PitchSequence PitchSeq { get; init; } = PitchSequence.Empty;
    /// <summary>打席開始時のアウト数（0-2）。</summary>
    public int OutsBefore { get; init; }
    /// <summary>打席前の塁占有（塁ダイヤ初期表示）。</summary>
    public bool BaseFirstBefore { get; init; }
    public bool BaseSecondBefore { get; init; }
    public bool BaseThirdBefore { get; init; }
    /// <summary>打席解決後の塁占有（塁ダイヤを結果へ更新）。</summary>
    public bool BaseFirstAfter { get; init; }
    public bool BaseSecondAfter { get; init; }
    public bool BaseThirdAfter { get; init; }

    // ── 現在の対戦（マッチアップHUD＋スタメン列ハイライト用）。すべて観測データで結果には影響しない ──
    /// <summary>この打者の打順（1-9。攻撃側列のハイライト用）。</summary>
    public int BatterOrder { get; init; }
    /// <summary>打者の選手ID（自校のみ。相手校は null）。通算・背番号の join キー。</summary>
    public int? BatterSourceId { get; init; }
    /// <summary>打者の左右打。</summary>
    public Players.Handedness BatterBats { get; init; }
    /// <summary>打者の守備位置（相手校の背番号フォールバック用）。</summary>
    public Field.FieldPosition BatterPosition { get; init; }
    /// <summary>対戦投手の選手ID（自校のみ。相手校は null）。</summary>
    public int? PitcherSourceId { get; init; }
    /// <summary>対戦投手名。</summary>
    public string PitcherName { get; init; } = "";
    /// <summary>対戦投手の左右投。</summary>
    public Players.Handedness PitcherThrows { get; init; }
    /// <summary>打者の背番号（0=番号なし）。</summary>
    public int BatterNumber { get; init; }
    /// <summary>対戦投手の背番号（0=番号なし）。</summary>
    public int PitcherNumber { get; init; }
    /// <summary>打者の調子の真値（連続値。相手校の観測誤認は Shell 側, issue #47）。</summary>
    public double BatterConditionValue { get; init; }
    /// <summary>対戦投手の調子の真値（連続値）。</summary>
    public double PitcherConditionValue { get; init; }
}

/// <summary>設計書15 Phase D-1: <see cref="MatchProgression.AdvancePitch"/> が止まった境界の種類。</summary>
public enum AdvancePitchResult
{
    /// <summary>1球ごとの采配窓（<see cref="GameStepKind.Pitch"/>）で止まった。まだ打席は確定していない。</summary>
    Pitch,
    /// <summary>打席が確定した（<see cref="MatchProgression.Current"/> が更新された）。</summary>
    PlateAppearance,
    /// <summary>試合が終了した。</summary>
    Finished,
}

/// <summary>
/// 実試合を「打席単位で解決→再生→采配窓→次」で進める逐次供給ドライバ（設計書09 采配進行の土台）。
/// <see cref="GameEngine.Steps"/> を1打席ずつ引き、各打席を <see cref="LivePlateAppearance"/> として供給する。
/// 采配（今回は代打）は打席間に注入し、<see cref="Save"/> で保存できる決定列として記録する。
/// 「スキップ」は残りを委任AI（設計書11）へ委ねて一括解決する。バッチ Play とは単一コードパスで、
/// 采配なしで全打席進めれば結果はバッチと完全一致する（＝MatchProgressionSteppingTests で保証）。
/// Unity 非依存で dotnet テスト可能。
/// </summary>
public sealed class MatchProgression
{
    private GameProgress _p;
    private IEnumerator<GameStep> _steps;
    private readonly List<GameDecision> _decisions = new();
    private readonly ulong _seed;
    private readonly bool _seedable;   // seed ベースで構築＝Save() で中断保存できるか
    private readonly ulong[]? _rngState; // 注入乱数の開始状態（設計書17 §3.2, F0）。非null なら Save() 可
    // シーク（設計書17 §6.2, F4）で最初から再生し直すために試合設定を保持する。
    private readonly Team _awayTeam;
    private readonly Team _homeTeam;
    private readonly GameContext _ctx;
    private readonly Debugging.ScenarioStart? _scenarioStart; // 注入で始めた試合は再生時も同じ局面から起こす

    private int _confirmed;   // 確定した打席数
    private int _pitchIndexInCurrentPa; // AdvancePitch() で現在の打席内に何回Pitch窓が来たか（0始まり）

    /// <summary>
    /// 1回の <see cref="Advance"/>／<see cref="DrainToEnd"/> が回してよい <see cref="GameStep"/> 数の安全上限
    /// （issue #288）。正常な試合は延長込みでも全ステップ数が数千どまりで、この桁上の値には絶対に達しない。
    /// これを超えたら進行体が壊れている（例: 継投の適用がメイン／背景スレッドで競合し、列挙状態や
    /// <see cref="TeamState"/> のラインナップが破損して打席境界が返らなくなった）と判断し、Unity ごとの
    /// フリーズではなく例外で落とす。背景経路（<c>BackgroundGameOp</c>）ならこの例外は捕捉されログに残る。
    /// カウンタを進めるだけで乱数は一切消費しない＝結果・digest・帯は不変（不変条件#2）。
    /// </summary>
    private const int MaxStepsPerAdvance = 1_000_000;

    /// <param name="scenarioStart">
    /// 場面ジャンプの開始局面（設計書17 §3.4, F2）。null=通常どおり1回表の頭から。
    /// 注入した試合は <see cref="GameResult.ScenarioId"/> が立ち、digest・統計集計から外れる。
    /// </param>
    public MatchProgression(Team away, Team home, GameContext ctx, ulong seed,
        Debugging.ScenarioStart? scenarioStart = null)
    {
        _seed = seed;
        _seedable = true;
        _awayTeam = away;
        _homeTeam = home;
        _ctx = ctx;
        _scenarioStart = scenarioStart;
        _p = GameEngine.NewProgress(away, home, ctx, new Xoshiro256Random(seed), null, scenarioStart);
        _steps = GameEngine.Steps(_p).GetEnumerator();
    }

    /// <summary>
    /// 注入乱数で駆動する（大会の隔離Fork ストリームをそのまま渡す用）。<see cref="GameEngine.Play"/> に
    /// 同じ away/home/ctx/rng を渡した結果とバイト一致で進行する（＝観戦しても大会結果は不変）。
    ///
    /// <para>設計書17 §3.2（F0・穴#2の解消）: 渡された乱数源が <see cref="Xoshiro256Random"/> なら
    /// <b>試合開始前の内部状態を控える</b>ので、シードを持たなくても <see cref="Save"/> で中断保存できる。
    /// それ以外の実装（テスト用の固定乱数など）では従来どおり <see cref="Save"/> は使えない。</para>
    /// </summary>
    public MatchProgression(Team away, Team home, GameContext ctx, IRandomSource rng)
    {
        _seed = 0;
        _seedable = false;
        _awayTeam = away;
        _homeTeam = home;
        _ctx = ctx;
        // NewProgress は dayForm 抽選で rng を消費するので、控えるのは必ずその手前。
        _rngState = (rng as Xoshiro256Random)?.CaptureState();
        _p = GameEngine.NewProgress(away, home, ctx, rng);
        _steps = GameEngine.Steps(_p).GetEnumerator();
    }

    /// <summary>直近に確定した打席（未進行なら null）。</summary>
    public LivePlateAppearance? Current { get; private set; }
    public bool IsFinished { get; private set; }
    public int ConfirmedPlateAppearances => _confirmed;
    // 進行中スコアは engine の実値をそのまま映す（自前で足し込まない）。設計書17 F2/F4 で
    // 「打席に紐づかない得点」（場面ジャンプの注入・暴投での生還・重盗の生還）が入ったため、
    // PlayLogEntry.RunsScored の合計では実スコアとズレる。
    public int AwayScore => _p.Away.Runs;
    public int HomeScore => _p.Home.Runs;

    /// <summary>
    /// 設計書15 Phase D-1: 今ここで <see cref="SetPitchBattingOverride"/>/<see cref="SetPitchDefenseOverride"/>
    /// を呼んだら、現在進行中の打席の何球目（0始まり）に適用されるか。<see cref="Advance"/> だけを使う限り常に0
    /// （＝打席の最初の球、従来どおり）。<see cref="AdvancePitch"/> で <see cref="GameStepKind.Pitch"/> に
    /// 止まった直後だけ、その球の添字に更新される。
    /// </summary>
    public int PendingPitchIndex { get; private set; }

    /// <summary>
    /// 中断保存の状態（シード or RNG状態＋確定打席数＋打席内の消化球数＋采配決定列）。
    /// <see cref="GameReplay.Restore"/> で復元できる。設計書17 F0 で Fork注入経路にも解禁した。
    /// </summary>
    public GameSaveState Save()
    {
        if (!_seedable && _rngState is null)
            throw new System.InvalidOperationException(
                "この MatchProgression は seed も捕捉可能なRNG状態も持たないため Save() で中断保存できません。");
        return new(_seed, _confirmed)
        {
            Decisions = _decisions.ToArray(),
            RngState = _rngState,
            ConfirmedPitchesInCurrentPa = _pitchIndexInCurrentPa,
        };
    }

    /// <summary>
    /// 采配窓（打席前）: 次打席の攻撃側に代打を送る。offenseIsAway=攻撃側が先攻(away)か。
    /// 適用できたら保存/委任でも再現できるよう決定列に記録する。采配なし（未呼び出し）ならバッチと一致。
    /// </summary>
    public bool PinchHitUpcoming(bool offenseIsAway, int benchIndex)
    {
        if (!SubstitutionCommands.PinchHit(_p, offenseIsAway, benchIndex)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.PinchHit, offenseIsAway, benchIndex));
        return true;
    }

    // ===== 選手交代（設計書09 §6）。打席境界の采配窓で呼ぶ。乱数を消費しないので帯は不変。 =====
    // 適用は必ず SubstitutionCommands（添字ベース）を通し、同じ関数を GameReplay も使う。
    // ＝「同シード＋同交代操作 → 同結果」と Save/Load 往復の一致が構造的に保証される（不変条件#2）。

    /// <summary>
    /// 今この打席境界で teamIsAway 側の監督が選べる交代の一覧（観測データ）。
    /// 攻撃中なら代打（次打者）／代走（塁上の走者）、守備中なら投手交代／守備交代／DH解除が候補になる。
    /// </summary>
    public SubstitutionOptions SubstitutionOptions(bool teamIsAway)
    {
        var team = _p.OffenseOf(teamIsAway);
        // 3アウト後は次の半イニング＝攻守が入れ替わる。試合開始前（未進行）は表＝先攻の攻撃から。
        var halfEnded = _p.CurrentOuts >= 3;
        var battingIsAway = halfEnded ? !_p.CurrentIsTop : _p.CurrentIsTop;
        var isOffense = battingIsAway == teamIsAway;

        var runners = new List<RunnerChoice>(3);
        if (isOffense && !halfEnded && !IsFinished && _p.CurrentBases is { } bases)
        {
            if (bases.First is { } r1) runners.Add(new RunnerChoice(0, r1));
            if (bases.Second is { } r2) runners.Add(new RunnerChoice(1, r2));
            if (bases.Third is { } r3) runners.Add(new RunnerChoice(2, r3));
        }

        return new SubstitutionOptions
        {
            TeamIsAway = teamIsAway,
            IsOffense = isOffense,
            IsFinished = IsFinished,
            Lineup = team.CurrentLineup,
            UpcomingBatterSlot = team.CurrentBatterOrder - 1,
            CurrentPitcher = team.CurrentPitcher,
            PitcherSlot = team.UsesDh ? -1 : team.PitcherSlot,
            UsesDh = team.UsesDh,
            DhSlot = team.DhSlot,
            Runners = runners,
            Reserves = team.AvailableReserves,
            UsedReserves = Unavailable(team.RosterReserves, team.AvailableReserves),
            ReliefCandidates = team.ReliefRankingCandidates,
        };
    }

    /// <summary>代打: 次打者を控え sub に代える。控えに sub が居なければ false。</summary>
    public bool PinchHit(bool teamIsAway, Player sub)
    {
        var idx = IndexOf(_p.OffenseOf(teamIsAway).Reserves, sub);
        return idx >= 0 && PinchHitUpcoming(teamIsAway, idx);
    }

    /// <summary>代走: baseIndex（0=一塁,1=二塁,2=三塁）の走者を控え sub に代える。塁の参照も差し替わる。</summary>
    public bool PinchRun(bool teamIsAway, int baseIndex, Player sub)
    {
        var idx = IndexOf(_p.OffenseOf(teamIsAway).Reserves, sub);
        if (idx < 0 || !SubstitutionCommands.PinchRun(_p, teamIsAway, baseIndex, idx)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.PinchRun, teamIsAway, idx,
            TargetIndex: baseIndex));
        return true;
    }

    /// <summary>投手交代: 控えの sub を指名して継投する（先頭固定ではない。issue #137）。</summary>
    public bool ChangePitcher(bool teamIsAway, Player sub)
    {
        var idx = IndexOf(_p.OffenseOf(teamIsAway).AvailablePitcherCandidates, sub);
        if (idx < 0 || !SubstitutionCommands.ChangePitcher(_p, teamIsAway, idx)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.ChangePitcher, teamIsAway, idx));
        return true;
    }

    /// <summary>
    /// 継投（issue #256 統合守備割当）。継投候補（<see cref="SubstitutionOptions.ReliefCandidates"/>＝
    /// 控え全員＋在フィールドの守備野手）から sub を指名してマウンドへ上げる。在フィールド野手なら
    /// 投野交代で守備穴を backfill する（<see cref="TeamState.RelieveWith"/>）。プレイヤーとAIが同じ候補集合・
    /// 同じルータ・同じ backfill 係数を通る。控えの指名は <see cref="ChangePitcher"/> と同じ結果になる。
    /// </summary>
    public bool Relieve(bool teamIsAway, Player sub)
    {
        var idx = ReferenceIndexOf(_p.OffenseOf(teamIsAway).ReliefRankingCandidates, sub);
        if (idx < 0 || !SubstitutionCommands.Relieve(_p, teamIsAway, idx)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.Relieve, teamIsAway, idx));
        return true;
    }

    /// <summary>
    /// 守備割当（issue #256 統合守備割当）。控えの sub を守備スロット slot に就かせる
    /// （守備固め／守備割当）。就いていた選手は退場する。役割（投/野）で候補を分けない（#93）。
    /// </summary>
    public bool DefenseAssign(bool teamIsAway, int slot, Player sub)
    {
        var idx = ReferenceIndexOf(_p.OffenseOf(teamIsAway).AvailablePitcherCandidates, sub);
        if (idx < 0 || !SubstitutionCommands.DefenseAssign(_p, teamIsAway, slot, idx)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.DefenseAssign, teamIsAway, idx, TargetIndex: slot));
        return true;
    }

    /// <summary>守備交代: 出場中の outgoing を控え sub に代える（守備位置を継承）。</summary>
    public bool DefensiveSub(bool teamIsAway, Player outgoing, Player sub)
    {
        var team = _p.OffenseOf(teamIsAway);
        var slot = IndexOf(team.CurrentLineup, outgoing);
        var idx = IndexOf(team.Reserves, sub);
        if (slot < 0 || idx < 0 || !SubstitutionCommands.DefensiveSub(_p, teamIsAway, slot, idx)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.DefensiveSub, teamIsAway, idx,
            TargetIndex: slot));
        return true;
    }

    /// <summary>
    /// 守備位置の入替＝コンバート（設計書09 §2 / issue #235）。出場中の2人の守備位置だけを交換する。
    /// <b>交代回数を消費しない</b>（選手の出入りが無い＝野球ルール上の「交代」ではない）が、
    /// リプレイ再現のために決定列へは積む（不変条件#2）。
    /// </summary>
    public bool SwapDefensivePositions(bool teamIsAway, int slotA, int slotB)
    {
        if (!SubstitutionCommands.SwapDefense(_p, teamIsAway, slotA, slotB)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.SwapDefense, teamIsAway, slotB,
            TargetIndex: slotA));
        return true;
    }

    /// <summary>
    /// コンバート継投（issue #235）。現投手を守備位置 at へ回し、reliever（控えの誰か）を
    /// 登板させる。at を守っていた野手が退場する。元投手は残留＝打席に立ち続け、後で再登板もできる。
    /// </summary>
    public bool ConvertPitcherAndRelieve(bool teamIsAway, Field.FieldPosition at, Player reliever)
    {
        var idx = IndexOf(_p.OffenseOf(teamIsAway).AvailablePitcherCandidates, reliever);
        if (idx < 0 || !SubstitutionCommands.ConvertRelief(_p, teamIsAway, at, idx)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.ConvertRelief, teamIsAway, idx, At: at));
        return true;
    }

    /// <summary>
    /// DH解除（不可逆・設計書09 §6）。at 指定＝DHの選手がその守備位置に就く（元の守備者が退場）、
    /// null＝DHの選手はそのまま退場。どちらも投手がDHの打順スロットへ入る。
    /// </summary>
    public bool ReleaseDh(bool teamIsAway, Field.FieldPosition? at)
    {
        if (!SubstitutionCommands.ReleaseDh(_p, teamIsAway, at)) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.ReleaseDh, teamIsAway, BenchIndex: 0, At: at));
        return true;
    }

    /// <summary>原本から「まだ使える分」を除いた差＝使い切った控え（原本の並びを保つ）。</summary>
    private static IReadOnlyList<Player> Unavailable(IReadOnlyList<Player> roster, IReadOnlyList<Player> available)
    {
        var used = new List<Player>();
        foreach (var p in roster)
            if (IndexOf(available, p) < 0) used.Add(p);
        return used;
    }

    private static int IndexOf(IReadOnlyList<Player> list, Player p)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == p) return i;
        return -1;
    }

    /// <summary>
    /// 参照一致で添字を引く（issue #256）。継投候補には在フィールドの実インスタンス（<see cref="TeamState._lineup"/>）が
    /// 含まれ、<see cref="TeamState.RelieveWith"/> が参照一致で在フィールド野手を判定するため、値一致では
    /// 取り違え得る（同能力の別選手）。UIは観測で得た実インスタンスをそのまま渡す。
    /// </summary>
    private static int ReferenceIndexOf(IReadOnlyList<Player> list, Player p)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], p)) return i;
        return -1;
    }

    // ===== 伝令（タイム。設計書09 §3・issue #173）。打席境界の采配窓でプレイヤーが発動する。 =====
    // 攻守各3回・延長は毎回+1（TeamState 側で管理・延長付与は GameEngine が行う）。乱数を消費しないので帯不変。

    /// <summary>プレイヤー発動の伝令の結果種別（<see cref="CallTimeout"/> の戻り値）。</summary>
    public enum PlayerTimeoutResult
    {
        /// <summary>残り0／試合終了で発動できなかった（伝令を消費していない）。</summary>
        Unavailable,
        /// <summary>攻撃伝令（打者・走者へ）を1回消費した。</summary>
        Offense,
        /// <summary>守備伝令（マウンドへ）を1回消費した。</summary>
        Defense,
    }

    /// <summary>
    /// いま（この打席境界で）自校（teamIsAway）が攻撃側（打者側）か。3アウト直後は次の半イニングへ
    /// 攻守が入れ替わるので、<see cref="SubstitutionOptions"/> と同じ規則で判定する（伝令の攻守判定に使う）。
    /// </summary>
    public bool IsTeamOnOffense(bool teamIsAway)
    {
        var halfEnded = _p.CurrentOuts >= 3;
        var battingIsAway = halfEnded ? !_p.CurrentIsTop : _p.CurrentIsTop;
        return battingIsAway == teamIsAway;
    }

    /// <summary>自校（teamIsAway）の攻撃伝令の残り回数（攻各3回・延長で+1, 設計書09 §3）。</summary>
    public int OffenseTimeoutsLeft(bool teamIsAway) => _p.OffenseOf(teamIsAway).OffenseTimeoutsLeft;

    /// <summary>自校（teamIsAway）の守備伝令の残り回数（守各3回・延長で+1, 設計書09 §3）。</summary>
    public int DefenseTimeoutsLeft(bool teamIsAway) => _p.OffenseOf(teamIsAway).DefenseTimeoutsLeft;

    /// <summary>
    /// いまこの局面で自校（teamIsAway）が伝令をかけられるか（攻撃中なら攻撃伝令、守備中なら守備伝令の
    /// 残りが1以上）。UI の「タイム」ボタンの活性判定に使う。
    /// </summary>
    public bool CanCallTimeout(bool teamIsAway)
    {
        if (IsFinished) return false;
        return IsTeamOnOffense(teamIsAway)
            ? OffenseTimeoutsLeft(teamIsAway) > 0
            : DefenseTimeoutsLeft(teamIsAway) > 0;
    }

    /// <summary>
    /// 設計書09 §3・issue #173: プレイヤーが今この打席境界で伝令をかける。自校（teamIsAway）が攻撃中なら
    /// 攻撃伝令（打者の緊張を数打席緩和）、守備中なら守備伝令（投手の動揺解除＋数打席の負補正緩和）を1回消費する。
    /// 残り0／試合終了なら <see cref="PlayerTimeoutResult.Unavailable"/> を返し、伝令を消費しない。
    ///
    /// <para>効果は既存のエンジン決定論経路（<c>TryUse*Timeout</c>＋<c>Start*Calm</c>／<c>ClearRattled</c>）を
    /// そのまま通す。これらは乱数を一切消費しないので、同シード＋同操作なら結果が再現し、統計帯・digest は不変。
    /// 効果は次に <see cref="Advance"/> で解決される打席から効く（今表示中の打席は既に確定済み）。
    /// AI/brain 駆動の伝令（<see cref="GameEngine"/>）と同じ伝令カウンタを共有する。</para>
    /// </summary>
    public PlayerTimeoutResult CallTimeout(bool teamIsAway)
    {
        if (IsFinished) return PlayerTimeoutResult.Unavailable;
        var team = _p.OffenseOf(teamIsAway);
        if (IsTeamOnOffense(teamIsAway))
        {
            if (!team.TryUseOffenseTimeout()) return PlayerTimeoutResult.Unavailable;
            team.StartOffenseCalm(_ctx.Tactics.TimeoutDurationPa);
            return PlayerTimeoutResult.Offense;
        }
        if (!team.TryUseDefenseTimeout()) return PlayerTimeoutResult.Unavailable;
        team.ClearRattled();
        team.StartDefenseCalm(_ctx.Tactics.TimeoutDurationPa);
        return PlayerTimeoutResult.Defense;
    }

    /// <summary>
    /// 1球采配（設計書15 Phase C-3）: 次の1球への打撃指示（強攻/待て）を予約する。
    /// offenseIsAway=打撃指示の対象（攻撃側）が先攻(away)か。null=指示解除。1球限り（消費後は自動で解除）。
    /// <see cref="ITacticsBrain"/> を経由しない手動操作＝プレイヤーの直接介入（PinchHitUpcoming と同型）。
    /// </summary>
    public void SetPitchBattingOverride(bool offenseIsAway, PitchBattingOverride? battingOverride)
    {
        _p.OffenseOf(offenseIsAway).SetPendingPitchBattingOverride(battingOverride);
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.PitchBattingOverride, offenseIsAway, BenchIndex: 0,
            Batting: battingOverride, PitchIndex: PendingPitchIndex));
    }

    /// <summary>
    /// 1球采配（設計書15 Phase C-3）: 次の1球への配球方針/ギア上書きを予約する。
    /// offenseIsAway=攻撃側が先攻(away)か（守備側はその逆側に適用）。両方nullで指示解除。1球限り。
    /// </summary>
    public void SetPitchDefenseOverride(bool offenseIsAway, PitchPolicy? policy, Pitching.PitcherGear? gear)
    {
        _p.OffenseOf(!offenseIsAway).SetPendingPitchDefenseOverride(policy, gear);
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.PitchDefenseOverride, offenseIsAway, BenchIndex: 0,
            Policy: policy, Gear: gear, PitchIndex: PendingPitchIndex));
    }

    /// <summary>
    /// 采配カード（2026-07-31）: 打撃姿勢を設定する。<b>解除するまで続く</b>（打席頭で毎回効く）。
    /// offenseIsAway=指示の対象（攻撃側）が先攻(away)か。<see cref="BattingApproach.Normal"/> で解除。
    /// 乱数を1発も消費しないので、同シード＋同操作なら結果が再現する。
    /// </summary>
    public void SetBattingApproach(bool offenseIsAway, BattingApproach approach)
    {
        _p.OffenseOf(offenseIsAway).SetBattingApproach(approach);
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.BattingApproach, offenseIsAway, BenchIndex: 0,
            Approach: approach, PitchIndex: PendingPitchIndex));
    }

    /// <summary>
    /// 采配カード（2026-07-31）: 守備の常時方針（配球・ギア・内野/外野の深さ）。<b>解除するまで続く</b>。
    /// teamIsAway=指示する側（守備側）が先攻か。null の項目は委任采配（brain）の判断のまま。
    /// </summary>
    public void SetStandingDefense(bool teamIsAway, PitchPolicy? policy, Pitching.PitcherGear? gear,
        DefenseDepth? infield, DefenseDepth? outfield)
    {
        _p.OffenseOf(teamIsAway).SetStandingDefense(policy, gear, infield, outfield);
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.StandingDefense, !teamIsAway, BenchIndex: 0,
            Policy: policy, Gear: gear, Infield: infield, Outfield: outfield, PitchIndex: PendingPitchIndex));
    }

    /// <summary>
    /// 采配カード（2026-07-31）: 次に解決される打席の攻撃サイン（エンドラン等）を予約する。<b>1打席限り</b>。
    /// null=解除。委任采配（brain）の判断より優先する。
    /// </summary>
    public void SetOffensiveSign(bool offenseIsAway, OffensiveSign? sign)
    {
        _p.OffenseOf(offenseIsAway).SetPendingOffensiveSign(sign);
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.OffensiveSign, offenseIsAway, BenchIndex: 0,
            Sign: sign, PitchIndex: PendingPitchIndex));
    }

    /// <summary>
    /// 采配カード（2026-07-31）: 次の1球で盗塁を試みる。<b>1球限り</b>。start=null で解除。
    /// 走者がいなければエンジン側の在塁判定で自然に不発になる（指示自体は無害）。
    /// </summary>
    public void SetStealAttempt(bool offenseIsAway, StartType? start, StealTarget target = StealTarget.Second)
    {
        _p.OffenseOf(offenseIsAway).SetPendingSteal(start, target);
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.StealAttempt, offenseIsAway, BenchIndex: 0,
            Start: start, StealTarget: target, PitchIndex: PendingPitchIndex));
    }

    /// <summary>次の打席を1つ解決して <see cref="Current"/> に載せる。試合終了なら false。</summary>
    public bool Advance()
    {
        if (IsFinished) return false;
        // 1球ごとの采配窓（GameStepKind.Pitch, 設計書15）は読み飛ばし、打席が確定した境界だけを表に出す。
        // 従来どおりの打席単位供給（Pitch 窓で一時停止したいなら AdvancePitch を使う＝Phase D-1）。
        // 打席境界が返らず暴走したら（issue #288）フリーズではなく例外で落とす（下の安全弁）。
        GameStep step;
        var steps = 0;
        do
        {
            if (++steps > MaxStepsPerAdvance) throw ProgressionRunaway();
            if (!_steps.MoveNext())
            {
                IsFinished = true;
                Current = null;
                return false;
            }
            step = _steps.Current;
        } while (step.Kind != GameStepKind.PlateAppearance);
        ConfirmPlateAppearance(step);
        return true;
    }

    /// <summary>
    /// 設計書15 Phase D-1: 真の1球進行。<see cref="GameStepKind.Pitch"/> 窓でも実際に止まり、
    /// 手動1球指示（<see cref="SetPitchBattingOverride"/>/<see cref="SetPitchDefenseOverride"/>）が
    /// 打席内の任意の球に届くようにする。無指示で呼び続けた場合は <see cref="Advance"/> と結果・RNG消費が完全一致する
    /// （進行粒度が細かくなるだけ＝帯不変）。
    /// </summary>
    public AdvancePitchResult AdvancePitch()
    {
        if (IsFinished) return AdvancePitchResult.Finished;
        if (!_steps.MoveNext())
        {
            IsFinished = true;
            Current = null;
            return AdvancePitchResult.Finished;
        }
        var step = _steps.Current;
        if (step.Kind == GameStepKind.Pitch)
        {
            PendingPitchIndex = _pitchIndexInCurrentPa;
            _pitchIndexInCurrentPa++;
            return AdvancePitchResult.Pitch;
        }
        ConfirmPlateAppearance(step);
        return AdvancePitchResult.PlateAppearance;
    }

    /// <summary>
    /// 直前に投げ終えた1球（<see cref="GameContext.CapturePitches"/> が真のときだけ非null）。
    ///
    /// <para>采配窓は各投球の<b>手前</b>にあるので、<see cref="AdvancePitch"/> が
    /// <see cref="AdvancePitchResult.Pitch"/> を返した時点で「1つ前の球」が投げ終わっている＝ここに載る。
    /// 打席の頭（まだ1球も投げていない窓）では前の打席の球が残るので、そこは <see cref="CurrentAtBat"/> を見る
    /// （<see cref="LiveAtBat.PitchNumberInPa"/> が1なら打席の頭）。</para>
    /// </summary>
    public LivePitch? LastPitch => _p.LastPitch;

    /// <summary>
    /// いま止まっている采配窓の局面＝<b>これから投げる1球</b>（<see cref="GameContext.CapturePitches"/> が
    /// 真のときだけ非null）。打席の頭でも埋まるので、UI は1球目を投げる前に打者を出してサインを受け付けられる。
    /// <see cref="AdvancePitch"/> が <see cref="AdvancePitchResult.Pitch"/> を返した直後に読む。
    /// </summary>
    public LiveAtBat? CurrentAtBat => _p.LiveAtBat;

    private void ConfirmPlateAppearance(GameStep step)
    {
        var e = _p.Log[step.LogIndex];

        // 投球列（設計書15 §4）: AtBatSession が解いた実データを最優先。バント/スクイズ等 PitchLog が無い
        // 経路（統一はPhase D）だけ、結果と整合する合成列にフォールバックする（打席固有シード＝メインRNG非依存）。
        PitchSequence pitchSeq;
        if (e.PitchLog is { Count: > 0 } pitchLog)
        {
            // 変換は PitchTokenFactory の1本に集約（1球ごとの供給 LivePitch と同じ経路＝描き方が食い違わない）。
            pitchSeq = new PitchSequence(
                pitchLog.Select(p => PitchTokenFactory.From(
                    p, _ctx, e.BaseFirstBefore, e.BaseSecondBefore, e.BaseThirdBefore)).ToList());
        }
        else
        {
            var seed = PitchSequenceSynthesizer.SeedFrom(step.LogIndex, step.Inning, step.IsTop, e.Pitches, e.Result);
            pitchSeq = PitchSequenceSynthesizer.Synthesize(e.Result, e.Pitches, seed);
        }
        Current = new LivePlateAppearance
        {
            Inning = step.Inning,
            IsTop = step.IsTop,
            AwayScore = AwayScore,
            HomeScore = HomeScore,
            BatterName = e.BatterName,
            RunsScored = e.RunsScored,
            Result = e.Result,
            Play = e.Timeline is null ? null : PlayTimelineAdapter.ToPlaybackPlay(e.Timeline),
            PitchSeq = pitchSeq,
            OutsBefore = e.OutsBefore,
            BaseFirstBefore = e.BaseFirstBefore,
            BaseSecondBefore = e.BaseSecondBefore,
            BaseThirdBefore = e.BaseThirdBefore,
            BaseFirstAfter = e.BaseFirstAfter,
            BaseSecondAfter = e.BaseSecondAfter,
            BaseThirdAfter = e.BaseThirdAfter,
            BatterOrder = e.BatterOrder,
            BatterSourceId = e.BatterSourceId,
            BatterBats = e.BatterBats,
            BatterPosition = e.BatterPosition,
            PitcherSourceId = e.PitcherSourceId,
            PitcherName = e.PitcherName ?? "",
            PitcherThrows = e.PitcherThrows,
            BatterNumber = e.BatterNumber,
            PitcherNumber = e.PitcherNumber,
            BatterConditionValue = e.BatterConditionValue,
            PitcherConditionValue = e.PitcherConditionValue,
        };
        _confirmed++;
        _pitchIndexInCurrentPa = 0;
        PendingPitchIndex = 0;
    }

    /// <summary>
    /// ライブ観戦のスナップショット（両チームの現ラインナップ＋現投手の今日の成績・現在の攻撃側/打者）。
    /// 3カラムのスタメン列とマッチアップHUDの今日成績を、UI再計算せずここから引く（不変条件: 数値はエンジン集計から）。
    /// <see cref="Advance"/> 後に呼ぶ（<see cref="Current"/> の攻撃側/打順を反映）。観測データ＝試合結果に影響しない。
    ///
    /// <para>球数（issue #397）: engine の球数集計（<see cref="TeamState.AddPitches"/>）は打席が確定した時に
    /// まとめて加算されるため、<see cref="AdvancePitch"/> で1球ごとに呼んでも据え置きに見える。ここでは
    /// <see cref="PendingPitchIndex"/>（進行中の打席で既に投げた球数・打席確定時に0へリセット済み＝二重計上なし）を
    /// 守備側の <see cref="LivePitcherToday"/> にだけ乗せる。攻撃側チームの現投手は今投げていないので常に0。
    /// 守備側の判定は <see cref="GameProgress.LiveAtBat"/>（<see cref="GameContext.CapturePitches"/> が真の時だけ
    /// 存在＝采配窓ごとに最新化される）から取る。存在しなければ判定不能として両チームとも0にする。</para>
    /// </summary>
    public MatchLiveSnapshot Snapshot()
    {
        var liveAtBat = _p.LiveAtBat;
        var awayIsDefending = liveAtBat is not null && !liveAtBat.IsTop;
        var homeIsDefending = liveAtBat is not null && liveAtBat.IsTop;
        var away = new LiveTeamSnapshot(_p.Away.LiveLineup(),
            _p.Away.LivePitcherLine(awayIsDefending ? PendingPitchIndex : 0),
            _p.Away.LiveReserves(), _p.Away.LiveReservePitchers());
        var home = new LiveTeamSnapshot(_p.Home.LiveLineup(),
            _p.Home.LivePitcherLine(homeIsDefending ? PendingPitchIndex : 0),
            _p.Home.LiveReserves(), _p.Home.LiveReservePitchers());
        return new MatchLiveSnapshot(away, home, Current?.IsTop ?? true, Current?.BatterOrder ?? 0,
            _p.Away.LiveLine(), _p.Home.LiveLine());
    }

    /// <summary>
    /// 対決ビューの背番号デカール・頭上フルネームラベル用（issue #393）。守備側チームの現在の守備位置→選手。
    /// <paramref name="offenseIsTop"/> は現在の攻撃側が先攻(表)なら true（守備側はその逆）。呼び出し側が
    /// 打球プレー開始のたびに呼び直す前提＝観測専用・試合結果に影響しない。
    /// </summary>
    public DuelFieldIdentity CurrentDefenseIdentity(bool offenseIsTop)
        => DuelFieldIdentity.Build(_p.OffenseOf(!offenseIsTop));

    /// <summary>確定済み打席のプレー記録（観測専用・読み取りのみ）。本日成績の略記列は UI がここから引く。</summary>
    public IReadOnlyList<PlayLogEntry> PlayLog => _p.Log;

    /// <summary>継投ログ（issue #209 表現。継投・投野交代・野手登板）。交代パネルの表示に使う。観測専用。</summary>
    public System.Collections.Generic.IReadOnlyList<PitchingChangeEvent> PitchingChanges => _p.PitchingChanges;

    /// <summary>
    /// スキップ: 以降の采配を委任AI（設計書11・自分の采配能力＝TacticalSense）へ委ね、残りを一括解決して
    /// 最終結果を返す。途中までの采配（適用済みの代打など）は状態に載っているので当然引き継がれる。
    /// managerIsAway=委任する監督のチームが先攻(away)か。委任先は該当チームのみ（相手は従来どおり）。
    /// </summary>
    public GameResult SkipDelegateToAi(bool managerIsAway, int managerTacticalSense, int tierRank = 5)
    {
        var brain = new AiTacticsBrain(AiProfile.Delegated(managerTacticalSense, tierRank));
        _p.OffenseOf(managerIsAway).OverrideTactics(brain);
        return DrainToEnd();
    }

    /// <summary>残りを采配追加なしで一括解決して最終結果を返す（「手動で最後まで」の残り消化）。</summary>
    public GameResult FinishRemaining() => DrainToEnd();

    // ===== シーク・早送り（設計書17 §6.2, F4）。すべて既存の再生機構の上に載る＝結果は変わらない。 =====

    /// <summary>N打席ぶん進める（早送り）。試合が終わったらそこで止まる。戻り値=実際に進めた打席数。</summary>
    public int AdvancePa(int n)
    {
        var moved = 0;
        while (moved < n && Advance()) moved++;
        return moved;
    }

    /// <summary>
    /// 今の半イニングが終わるまで進める（早送り）。<see cref="GameProgress.CurrentOuts"/> が3になった打席、
    /// または試合終了で止まる。戻り値=進めた打席数。
    /// </summary>
    public int AdvanceUntilInningEnd()
    {
        var moved = 0;
        while (Advance())
        {
            moved++;
            if (_p.CurrentOuts >= 3) break;
        }
        return moved;
    }

    /// <summary>
    /// 指定の位置（確定打席数, 打席内の消化球数）へシークする（設計書17 §6.2）。
    /// 実装は「先頭から再生し直す」だけなので、往復しても同じ場面・同じトレースになる（決定論そのもの）。
    /// 進む方向のシークでも再生し直す（現在位置からの差分進行と結果は同じだが、経路を1本に保つため）。
    ///
    /// <para><b>観測への影響</b>: 再生は実際の再シミュレーションなので、<c>CaptureTrace</c> が有効なら
    /// 再生ぶんの <see cref="Debugging.PitchTrace"/> がもう一度シンクへ流れる。HUD のリングバッファは
    /// 「今見えている試合」をそのまま映すので問題にならないが、JSONL へ書いている最中にシークすると
    /// 同じ試合が2本ぶん記録される点は意識しておくこと。</para>
    /// </summary>
    public void SeekTo(int plateAppearances, int pitchesInCurrentPa = 0)
    {
        if (plateAppearances < 0 || pitchesInCurrentPa < 0)
            throw new System.ArgumentOutOfRangeException(nameof(plateAppearances), "シーク位置は0以上である必要があります。");

        var save = Save() with
        {
            ConfirmedPlateAppearances = plateAppearances,
            ConfirmedPitchesInCurrentPa = pitchesInCurrentPa,
        };
        var resumed = GameReplay.Restore(_awayTeam, _homeTeam, _ctx, save, _scenarioStart);
        _p = resumed.Progress;
        _steps = resumed.Steps;

        // 再生後の見かけの状態を組み直す（スコアは _p 直読みなので同期不要）。
        _confirmed = _p.Log.Count;
        _pitchIndexInCurrentPa = pitchesInCurrentPa;
        PendingPitchIndex = pitchesInCurrentPa > 0 ? pitchesInCurrentPa - 1 : 0;
        IsFinished = false;
        Current = null;
    }

    /// <summary>
    /// 今の場面を指す再現トークン（設計書17 §3.3）。HUD に常時表示してコピーする用。
    /// RNG状態を捕捉できない乱数源で構築した場合は null。
    /// </summary>
    public Debugging.ReproToken? ReproToken()
    {
        var state = _rngState ?? (_seedable ? new Xoshiro256Random(_seed).CaptureState() : null);
        if (state is null) return null;
        return new Debugging.ReproToken(state, _confirmed, _pitchIndexInCurrentPa,
            Debugging.ReproToken.Fingerprint(_awayTeam, _homeTeam, _ctx));
    }

    /// <summary>次の1打席に効く強制発動を予約する（設計書17 §6.1, F4・デバッグ経路専用）。</summary>
    public void ForceNext(Debugging.ForcedOutcome outcome) => _p.ForceNext(outcome);

    private GameResult DrainToEnd()
    {
        var steps = 0;
        while (_steps.MoveNext())
        {
            if (++steps > MaxStepsPerAdvance) throw ProgressionRunaway();
            // Pitch 窓（設計書15）は打席確定ではないのでスコア加算・確定カウントの対象外。
            if (_steps.Current.Kind != GameStepKind.PlateAppearance) continue;
            _confirmed++;
        }
        IsFinished = true;
        Current = null;
        return GameEngine.BuildResult(_p);
    }

    /// <summary>進行体の暴走（issue #288）を表す例外。<see cref="MaxStepsPerAdvance"/> を超えたときに投げる。</summary>
    private static System.InvalidOperationException ProgressionRunaway()
        => new("MatchProgression の進行が安全上限（" + MaxStepsPerAdvance +
            " ステップ）を超えました。打席境界が返らず暴走しています（進行体の破損の疑い・issue #288）。");

    /// <summary>ここまでの進行から最終結果を組む（全打席 Advance し終えた後に呼ぶ）。</summary>
    public GameResult BuildResult() => GameEngine.BuildResult(_p);
}
