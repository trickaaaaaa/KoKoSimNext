using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.AtBat;

/// <summary>1球分の解決結果（設計書15 §2.1）。Phase A では打席が終わったか否かだけを露出する
/// （PitchRecord/Trajectory の露出は Phase B。ここで無理に前借りしない）。</summary>
/// <param name="EndsPlateAppearance">この投球で打席が確定したか。</param>
/// <param name="SqueezeRunnerCaughtAtThird">
/// スクイズのウエストを読まれ、三塁走者だけが挟殺された（設計書15 Phase D-2c）。打席は確定しない
/// （<see cref="PitchResolution.EndsPlateAppearance"/> は false のまま）。呼び出し側（GameEngine）が
/// 三塁走者を除去しアウトを1つ加算した上で、打者の打席続行（強攻フォールバック）へ進める。
/// </param>
public readonly record struct PitchResolution(bool EndsPlateAppearance, bool SqueezeRunnerCaughtAtThird = false);

/// <summary>
/// 打席解決パイプライン①〜⑥（設計書01 §2）を「再開可能な投球ステッパ」に作り替えたもの（設計書15 §2.1）。
/// 従来 <see cref="AtBatResolver.ResolveDetailed"/> の密ループが持っていたローカル変数（balls/strikes/球数/
/// スキル補正済み打撃係数）を state へ昇格させ、ループ制御を呼び出し側（GameEngine の1球境界）へ外出しする。
///
/// <para><b>不変条件（設計書15 Phase A）</b>: <see cref="ThrowNextPitch"/> を打席確定まで回した帰結は、
/// 従来の密ループと <b>打席結果・消費RNG・球数まで完全一致</b>する。1球内のRNG消費順（配球→散布→打者判断
/// →コンタクト→守備）は現行と1ビットも変えない。yield（球間の采配窓）は乱数を消費しない。</para>
///
/// <para>Phase A では通常打席の投球ループだけを担う。バント/スクイズ/盗塁/牽制/敬遠は GameEngine 側の
/// 従来経路のまま温存する（統一は Phase D）。</para>
/// </summary>
public sealed class AtBatSession
{
    private readonly BatterAttributes _batter;
    private readonly Player? _batterPlayer;
    private readonly Player? _thirdBaseRunner;
    private readonly double _squeezeWasteProbability;
    private readonly PitcherAttributes _pitcher;
    private readonly AtBatContext _ctx;
    private readonly BattingCoefficients _batting;

    private int _balls;
    private int _strikes;
    private int _pitchCount;
    private AtBatResult? _result;
    private readonly List<PitchRecord> _pitchLog = new();
    /// <summary>デバッグ観測（設計書17 §4.1）。<c>CaptureTrace</c> のときだけ実体を持つ。</summary>
    private readonly List<Debugging.PitchTrace>? _pitchTraces;
    private PitchTrajectoryFeatures? _lastPitchFeatures;
    // 投球開始時のカウント（観測用。RecordPitch はカウント加算「後」に呼ばれるため控えておく）。
    private int _ballsAtPitchStart;
    private int _strikesAtPitchStart;
    // 強制発動で固定された打席結果（設計書17 §6.1, F4）。デバッグ経路からのみ設定される。
    private PlateAppearanceResult? _forcedResult;

    private AtBatSession(
        BatterAttributes batter, Player? batterPlayer, Player? thirdBaseRunner, double squeezeWasteProbability,
        PitcherAttributes pitcher, AtBatContext ctx, BattingCoefficients batting)
    {
        _batter = batter;
        _batterPlayer = batterPlayer;
        _thirdBaseRunner = thirdBaseRunner;
        _squeezeWasteProbability = squeezeWasteProbability;
        _pitcher = pitcher;
        _ctx = ctx;
        _batting = batting;
        _pitchTraces = ctx.CaptureTrace ? new List<Debugging.PitchTrace>() : null;
    }

    /// <summary>現在のボールカウント（未消化の投球前の値）。</summary>
    public int Balls => _balls;

    /// <summary>現在のストライクカウント。</summary>
    public int Strikes => _strikes;

    /// <summary>ここまでに投じた球数。</summary>
    public int PitchCount => _pitchCount;

    /// <summary>ここまでに実際に解決した1球ごとの記録（設計書15 §4）。1球采配の状況入力・UI観測の両方から参照する。</summary>
    public IReadOnlyList<PitchRecord> PitchLog => _pitchLog;

    /// <summary>
    /// ここまでのデバッグ観測レコード（設計書17 §4.1）。<c>AtBatContext.CaptureTrace</c> が偽なら常に空。
    /// 打席解決層が埋められる範囲（意図・実着弾・打者判断・打球）だけが入っており、局面・状態・采配・RNG は
    /// 試合層（<see cref="Game.GameEngine"/>）が <c>with</c> で足してからシンクへ流す。
    /// </summary>
    public IReadOnlyList<Debugging.PitchTrace> PitchTraces
        => (IReadOnlyList<Debugging.PitchTrace>?)_pitchTraces ?? System.Array.Empty<Debugging.PitchTrace>();

    /// <summary>直前に投じた球の結果（設計書15 §2.3, 1球采配の状況入力）。まだ1球も投げていなければnull。</summary>
    public PitchKind? LastPitchKind => _pitchLog.Count > 0 ? _pitchLog[^1].Kind : null;

    /// <summary>
    /// 直前に投じた球の判定用弾道特徴量（設計書15 Phase E-1/E-2）。<see cref="TrajectoryFeatureTable"/> による
    /// 決定論的な補間参照で、毎球RNG非消費・観測用フル弾道（<see cref="PitchRecord.Trajectory"/>）とは無関係に
    /// 常時計算する。Phase E-2 から <see cref="Batting.ContactModel"/> の空振り判定へ接続済み。
    /// </summary>
    public PitchTrajectoryFeatures? LastPitchFeatures => _lastPitchFeatures;

    /// <summary>打席が確定したか（確定後は <see cref="ThrowNextPitch"/> を呼べない）。</summary>
    public bool IsComplete => _result is not null;

    /// <summary>確定した打席結果（<see cref="IsComplete"/> が真のときのみ）。</summary>
    public AtBatResult Result =>
        _result ?? throw new System.InvalidOperationException("打席はまだ確定していません。");

    /// <summary>
    /// 打席を開始する。スキルの打球挙動補正（設計書10）による打撃係数の1打席コピーはここで1回だけ作る
    /// （従来 <see cref="AtBatResolver.ResolveDetailed"/> のループ手前と同一・RNG非消費）。
    /// </summary>
    /// <param name="batterPlayer">
    /// 生の <see cref="Player"/>（設計書15 Phase D-2b）。バント解決（<see cref="BuntResolver"/>）が
    /// フォーム/スキル補正済みの <see cref="BatterAttributes"/> にはない生スキル値（Bunt/性格補正/走力）を
    /// 直接必要とするため、通常打席パイプラインとは別に保持する。null=バント系の1球指示を一切使わない
    /// 呼び出し（既存テスト等）向けの省略時既定＝実際にバントを試みると例外になる。
    /// </param>
    /// <param name="thirdBaseRunner">
    /// スクイズ（設計書15 Phase D-2c）で <see cref="SqueezeResolver"/> が必要とする三塁走者の生
    /// <see cref="Player"/>（走塁判断・走力）。null=スクイズ指示を一切使わない呼び出し向けの省略時既定＝
    /// 実際にスクイズを試みると例外になる。
    /// </param>
    /// <param name="squeezeWasteProbability">
    /// スクイズのウエスト（外し）を守備側が読み切る確率（設計書02 §4.4 / 09 §1）。打席頭の状況（捕手リード・
    /// アウト数・点差）で決まり打席中は不変なので、GameEngine が1回だけ計算して渡す。既定0＝外さない。
    /// </param>
    /// <param name="initialBalls">
    /// 開始ボールカウント（設計書17 §3.4, F2 場面ジャンプ）。既定0＝通常どおり 0-0 から。
    /// 途中カウントから始めても投球ループ・RNG消費順は一切変わらない（カウントは状態でしかない）。
    /// </param>
    /// <param name="initialStrikes">開始ストライクカウント。既定0。</param>
    /// <param name="forcedResult">
    /// 強制発動（設計書17 §6.1, F4）で固定する打席結果。非nullなら投球ループ自体をスキップし、
    /// 投球数0・RNG非消費でこの結果を確定させる（敬遠と同じ経路）。<b>打球の物理層は偽装しない</b>ので、
    /// 用途は下流（進塁・記録・タイムライン・UI）の検証に限る。
    /// </param>
    public static AtBatSession Begin(
        BatterAttributes batter, PitcherAttributes pitcher, AtBatContext ctx, Player? batterPlayer = null,
        Player? thirdBaseRunner = null, double squeezeWasteProbability = 0.0,
        int initialBalls = 0, int initialStrikes = 0,
        PlateAppearanceResult? forcedResult = null)
    {
        // スキルの打球挙動補正（設計書10）。行動特性・球質だけをここで反映（数値補正は実効能力側で適用済み）。
        // 広角打法（打球方向σ）・粘り打ち（ファウル率）は打撃係数の1打席コピーへ。スキルなしなら共有係数のまま。
        var batting = ctx.Batting;
        if (ctx.Skills.BearingSigmaFactor != 1.0 || ctx.Skills.FoulShareFactor != 1.0)
        {
            batting = batting with
            {
                BearingSigma = batting.BearingSigma * ctx.Skills.BearingSigmaFactor,
                FoulShare = MathUtil.Clamp(batting.FoulShare * ctx.Skills.FoulShareFactor, 0.0, 0.9),
            };
        }

        var session = new AtBatSession(
            batter, batterPlayer, thirdBaseRunner, squeezeWasteProbability, pitcher, ctx, batting);
        session._balls = initialBalls;
        session._strikes = initialStrikes;
        session._forcedResult = forcedResult;
        return session;
    }

    /// <summary>
    /// 次の1球を解決してカウント/球数を進める。従来密ループの1イテレーションと同じ分岐・同じRNG順を辿る。
    /// 打席が確定したら <see cref="Result"/> を確定させ <c>EndsPlateAppearance=true</c> を返す。
    /// </summary>
    /// <param name="rng">主RNG。</param>
    /// <param name="battingOverride">
    /// この球だけの打撃指示（設計書15 §2.3, Q12-3: 方針を単純上書き）。null=方針まかせ＝従来と完全一致。
    /// </param>
    /// <param name="pitchOverride">この球だけの配球ウェイト上書き。null=打席頭の方針（<see cref="AtBatContext.Directive"/>）。</param>
    /// <param name="gearOverride">この球だけの投手ギア上書き。null=打席頭の方針（<see cref="AtBatContext.Gear"/>）。</param>
    public PitchResolution ThrowNextPitch(
        IRandomSource rng,
        Tactics.PitchBattingOverride? battingOverride = null,
        Tactics.PitchDirective? pitchOverride = null,
        PitcherGear? gearOverride = null)
    {
        if (_result is not null)
            throw new System.InvalidOperationException("打席は既に確定しています。ThrowNextPitch は呼べません。");

        // 敬遠（design-14 P1-3・設計書15 Phase D-2a）: 統一ステッパでも申告制のまま＝投球数0・RNG非消費で
        // 即確定する。この打席は1球も投げないまま終わる（以降 ThrowNextPitch は呼ばれない）。
        if (_ctx.IntentionalWalk)
        {
            _result = new AtBatResult(PlateAppearanceResult.Walk, _pitchCount) { PitchLog = _pitchLog };
            return new PitchResolution(EndsPlateAppearance: true);
        }

        // 強制発動（設計書17 §6.1, F4）: 抽選をスキップして打席結果だけを固定する。敬遠と同じく
        // 投球数0・RNG非消費で即確定する（＝物理層を1回も回さない＝偽装しない）。
        if (_forcedResult is { } forced)
        {
            _forcedResult = null;
            _result = new AtBatResult(forced, _pitchCount) { PitchLog = _pitchLog };
            return new PitchResolution(EndsPlateAppearance: true);
        }

        var ctx = _ctx;
        var batter = _batter;
        var pitcher = _pitcher;
        var batting = _batting;

        var pitchIndex = _pitchCount; // 0始まりの投球番号（従来ループの pitch 変数）
        _pitchCount = pitchIndex + 1; // 従来の pitch + 1（=消費球数）
        _ballsAtPitchStart = _balls;   // 観測用（設計書17 §4.1）。以降のカウント加算より前の値。
        _strikesAtPitchStart = _strikes;

        // ① 配球（球種選択＋毎球球速サンプリング §1.1d） → ② 投球生成（狙い＋コントロールσ散布）
        var gear = gearOverride ?? ctx.Gear;
        var directive = pitchOverride ?? ctx.Directive;
        var plan = PitchSelection.Select(pitcher, ctx.StrikeZone, batting, ctx.Pitching, rng, gear, directive, ctx.CatcherLead);
        // クセ球/荒れ球（設計書10）: 球速に依らない球威の上乗せ（空振り誘発の質的個性）。
        if (ctx.Skills.StuffBonus != 0.0) plan = plan with { Stuff = plan.Stuff + ctx.Skills.StuffBonus };
        var loc = ControlScatter.Sample(plan.AimX, plan.AimY, pitcher.Control, ctx.Pitching, rng);
        var inZone = ctx.StrikeZone.Contains(loc.X, loc.Y);

        // 判定用弾道特徴量（設計書15 Phase E-1）: テーブル補間・RNG非消費。Phase E-2 から④の空振り判定へ接続。
        _lastPitchFeatures = ComputeFeatures(plan);

        // 死球（HBP, design-14 未決F・2026-07-20）: 散布結果が打者の体側ウィンドウ（X負側・高さ帯）へ入り、
        // 回避に失敗した球。ボールデッド＝打席即終了（進塁はフォースのみ＝BaserunningModel が Walk 同様に解く）。
        // バント/スクイズ構え中でも体に当たれば死球（各Resolverより先に判定）。回避ロールはウィンドウ内のみ
        // rng を消費する（FieldersChoiceProb ガードと同じ流儀）。
        if (loc.X <= -ctx.Pitching.HbpBodyEdgeM
            && loc.Y >= ctx.Pitching.HbpBodyBottomM && loc.Y <= ctx.Pitching.HbpBodyTopM
            && !MathUtil.Chance(ctx.Pitching.HbpDodgeProb, rng))
        {
            RecordPitch(PitchKind.HitByPitch, plan, loc, inZone);
            return Finish(PlateAppearanceResult.HitByPitch);
        }

        // バント/セーフティバント（design-14・設計書02 §4.3・設計書15 Phase D-2b）: 通常の打者判断/コンタクト
        // パイプラインとは別の専用解決（BuntResolver）へ分岐する。球種/コースは実データとして記録するが
        // （PitchSelection/ControlScatterは通常どおり消費済み）、結果自体は BuntResolver が決める。
        if (battingOverride is Tactics.PitchBattingOverride.Bunt or Tactics.PitchBattingOverride.SafetyBunt)
        {
            if (_batterPlayer is null)
                throw new System.InvalidOperationException(
                    "バント指示を受けましたが AtBatSession.Begin に batterPlayer が渡されていません。");
            var safety = battingOverride == Tactics.PitchBattingOverride.SafetyBunt;
            var bunt = BuntResolver.Resolve(_batterPlayer, pitcher, safety, ctx.Baserunning, rng);
            switch (bunt)
            {
                case BuntResult.MissedBunt:
                    _strikes++;
                    RecordPitch(PitchKind.SwingingStrike, plan, loc, inZone, swung: true);
                    return Continue();

                case BuntResult.Foul:
                    _strikes++;
                    RecordPitch(PitchKind.Foul, plan, loc, inZone, swung: true);
                    return Continue();

                case BuntResult.PopOut:
                    RecordPitch(PitchKind.InPlay, plan, loc, inZone, swung: true);
                    return FinishBunt(PlateAppearanceResult.InPlayOut, bunt);

                case BuntResult.SacrificeSuccess:
                    RecordPitch(PitchKind.InPlay, plan, loc, inZone, swung: true);
                    return FinishBunt(PlateAppearanceResult.InPlayOut, bunt);

                case BuntResult.InfieldHit:
                    RecordPitch(PitchKind.InPlay, plan, loc, inZone, swung: true);
                    return FinishBunt(PlateAppearanceResult.Single, bunt);
            }
        }

        // スクイズ（design-14・設計書02 §4.4・設計書15 Phase D-2c）: バントと同様に実球を生成してから
        // SqueezeResolver へ委ねる。結果判定はSqueezeResolverのまま変えない。1球で必ず決着する
        // （2ストライク未満は継続するバントと異なり、呼び出し側は最初の1球にしかこの上書きを渡さない）。
        if (battingOverride == Tactics.PitchBattingOverride.Squeeze)
        {
            if (_batterPlayer is null)
                throw new System.InvalidOperationException(
                    "スクイズ指示を受けましたが AtBatSession.Begin に batterPlayer が渡されていません。");
            if (_thirdBaseRunner is null)
                throw new System.InvalidOperationException(
                    "スクイズ指示を受けましたが AtBatSession.Begin に thirdBaseRunner が渡されていません。");
            var sq = SqueezeResolver.Resolve(
                _batterPlayer, _thirdBaseRunner, pitcher, _squeezeWasteProbability, ctx.Baserunning, rng);

            if (!sq.BatterOut && sq.Runs == 0 && sq.RunnerOut)
            {
                // ウエストを読まれた（守備がピッチアウトで対抗）: 打者はバントを試みられずボール、
                // 飛び出した三塁走者が挟殺される。打席は確定せず、以降は強攻へフォールバックする。
                _balls++;
                RecordPitch(PitchKind.Ball, plan, loc, inZone);
                return new PitchResolution(EndsPlateAppearance: false, SqueezeRunnerCaughtAtThird: true);
            }

            var kind = sq.Bunt switch
            {
                BuntResult.MissedBunt => PitchKind.SwingingStrike,
                BuntResult.Foul => PitchKind.Foul,
                _ => PitchKind.InPlay, // PopOut / SacrificeSuccess / InfieldHit
            };
            RecordPitch(kind, plan, loc, inZone, swung: true);
            var sqResult = sq.Bunt == BuntResult.InfieldHit ? PlateAppearanceResult.Single : PlateAppearanceResult.InPlayOut;
            return FinishSqueeze(sqResult, sq);
        }

        // ③ 打者判断（「待て」サインの初球は必ず見送り, 設計書09 §1）。1球指示（battingOverride）は
        // 方針・初球からスイングのスキル抽選より常に優先し、いずれもRNGを追加消費しない（単純上書き）。
        var forcedTake = battingOverride == Tactics.PitchBattingOverride.ForceTake
            || (battingOverride is null && ctx.TakeFirstPitch && pitchIndex == 0);
        // 初球から振る（設計書10）: 初球にスイングを仕掛ける（積極性の行動特性）。スキルなしは draw しない。
        var forcedSwing = battingOverride == Tactics.PitchBattingOverride.ForceSwing
            || (battingOverride is null && !forcedTake && pitchIndex == 0 && ctx.Skills.FirstPitchSwingProb > 0.0
                && MathUtil.Chance(ctx.Skills.FirstPitchSwingProb, rng));
        var distanceOutsideM = ctx.StrikeZone.DistanceOutsideM(loc.X, loc.Y);
        // 観測用のスイング確率（設計書17 §4.1）。DecideSwing が内部で使うのと同じ純関数で、乱数を消費しない。
        // CaptureTrace が偽なら1回も呼ばない＝既定パスはゼロコスト。
        var swingProb = ctx.CaptureTrace
            ? BatterDecision.SwingProbability(
                inZone, distanceOutsideM, _lastPitchFeatures!.Value.BreakMagnitudeM, batter, batting)
            : 0.0;
        if (!forcedSwing && (forcedTake
            || !BatterDecision.DecideSwing(inZone, distanceOutsideM, _lastPitchFeatures!.Value.BreakMagnitudeM, batter, batting, rng)))
        {
            if (inZone)
            {
                _strikes++;
                RecordPitch(PitchKind.CalledStrike, plan, loc, inZone, swingProb);
            }
            else
            {
                _balls++;
                RecordPitch(PitchKind.Ball, plan, loc, inZone, swingProb);
            }

            if (_strikes >= 3) return Finish(PlateAppearanceResult.Strikeout);
            if (_balls >= 4) return Finish(PlateAppearanceResult.Walk);
            return Continue();
        }

        // ④ コンタクト判定 → ⑤ 打球生成（広角打法/粘り打ちは batting コピーへ反映済み）
        var (outcome, ball) = ContactModel.Resolve(batter, plan, _lastPitchFeatures!.Value, inZone, batting, rng);
        switch (outcome)
        {
            case ContactOutcome.Whiff:
                _strikes++;
                RecordPitch(PitchKind.SwingingStrike, plan, loc, inZone, swingProb, swung: true);
                if (_strikes >= 3) return Finish(PlateAppearanceResult.Strikeout);
                return Continue();

            case ContactOutcome.Foul:
                if (_strikes < 2) _strikes++;
                RecordPitch(PitchKind.Foul, plan, loc, inZone, swingProb, swung: true, ball: ball);
                return Continue();

            case ContactOutcome.InPlay:
                // ⑥ 守備解決（詳細版: 判定・乱数順は従来と同一。幾何・時刻を保持しタイムラインへ）
                var play = FieldingResolver.ResolveDetailed(
                    ball!, ctx.Field, ctx.Aerodynamics, batter, ctx.Fielders, ctx.Fielding, rng);
                if (play.Result == BattedBallResult.Foul)
                {
                    if (_strikes < 2) _strikes++;
                    RecordPitch(PitchKind.Foul, plan, loc, inZone, swingProb, swung: true, ball: ball);
                    return Continue();
                }
                RecordPitch(PitchKind.InPlay, plan, loc, inZone, swingProb, swung: true, ball: ball);

                var pa = play.Result switch
                {
                    BattedBallResult.Single => PlateAppearanceResult.Single,
                    BattedBallResult.Double => PlateAppearanceResult.Double,
                    BattedBallResult.Triple => PlateAppearanceResult.Triple,
                    BattedBallResult.HomeRun => PlateAppearanceResult.HomeRun,
                    BattedBallResult.Error => PlateAppearanceResult.ReachedOnError,
                    _ => PlateAppearanceResult.InPlayOut,
                };

                // タイムライン出力（CHANGELOG 32: 既定オフ。UI再生時のみ構築＝乱数不使用）。
                Timeline.PlayTimeline? timeline = null;
                if (ctx.CaptureTimeline)
                {
                    timeline = Timeline.TimelineBuilder.BuildBattedBall(
                        play, ctx.Fielders, ctx.Field, ctx.Formations, ctx.RunnersOn,
                        Timeline.TimelineBuilder.DescribeResult(play));
                }
                _result = new AtBatResult(pa, _pitchCount)
                {
                    Timeline = timeline,
                    // F2: 本塁クロスプレー（バックホーム憤死）が結果に効くため、幾何を常時保持する
                    // （F1では捕捉時のみ＝統計シムゼロコストだったが、F2で判定入力になったので常時計算）。
                    Play = play,
                    PitchLog = _pitchLog,
                };
                return new PitchResolution(EndsPlateAppearance: true);
        }

        // ContactOutcome の網羅漏れは想定しない（従来 switch も同様に continue へ落ちる）。
        return Continue();
    }

    /// <summary>
    /// この1球の解決済みの値をそのまま写す（設計書15 §4）。新たな抽選はしない。
    /// <c>CaptureTrace</c> のときはデバッグ観測（設計書17 §4.1）も同じ値から1件組む。
    /// </summary>
    /// <param name="inZone">実着弾がストライクゾーン内か（呼び出し前に算出済み）。</param>
    /// <param name="swingProbability">この球のスイング確率（純関数・観測専用。判定に使った値そのもの）。</param>
    /// <param name="swung">打者が振ったか（バント/スクイズの試行もスイング扱い）。</param>
    /// <param name="ball">コンタクトした打球（InPlay/Foul でなければ null）。</param>
    private void RecordPitch(
        PitchKind kind, PitchPlan plan, ControlScatter.Location loc,
        bool inZone, double swingProbability = 0.0, bool swung = false, Batting.BattedBall? ball = null)
    {
        // 弾道積分は毎球RK4×2本(スピンあり/なし)とコストが高いため、Timeline同様CaptureTimeline時のみ
        // （観戦する試合だけ）計算する。既定（統計シム・裏試合）はゼロコスト（設計書15 §4「Phase Bの作業」）。
        var trajectory = _ctx.CaptureTimeline ? ComputeTrajectory(plan) : null;
        _pitchLog.Add(new PitchRecord(kind, _balls, _strikes, plan.Type, loc.X, loc.Y, plan.VelocityKmh, trajectory));

        if (_pitchTraces is null) return;
        var f = _lastPitchFeatures ?? default;
        _pitchTraces.Add(new Debugging.PitchTrace
        {
            // PitchRecord の Balls/Strikes は「この球の解決後」の値だが、トレースは「投じる前の状況」を持つ
            // （JSONL の "cnt" が実況のカウント表示と一致するため）。投球開始時に控えた値を使う。
            BallsBefore = _ballsAtPitchStart,
            StrikesBefore = _strikesAtPitchStart,
            PitchNoInPa = _pitchCount,
            PlanType = plan.Type,
            PlanAimX = plan.AimX,
            PlanAimY = plan.AimY,
            PlanVelocityKmh = plan.VelocityKmh,
            PlanStuff = plan.Stuff,
            ActualX = loc.X,
            ActualY = loc.Y,
            ActualKmh = plan.VelocityKmh,
            FlightTimeSeconds = f.FlightTimeSeconds,
            InducedVerticalBreakM = f.InducedVerticalBreakM,
            InducedHorizontalBreakM = f.InducedHorizontalBreakM,
            InZone = inZone,
            SwingProbability = swingProbability,
            Swung = swung,
            Kind = kind,
            ExitVelocityKmh = ball?.ExitVelocityKmh,
            LaunchAngleDeg = ball?.LaunchAngleDeg,
            SprayAngleDeg = ball?.BearingDeg,
        });
    }

    /// <summary>
    /// 弾道は観測専用（設計書15 §0.1 Q12-5）。<see cref="PitchSimulator"/> はRNGを使わない決定論積分で、
    /// 判定には使わない（Phase E で接続）。回転数はキレ(Sharpness)の線形写像（暫定式・観測専用のため帯に無関係）、
    /// 回転軸は全球種バックスピン固定（横変化は現モデルでは扱わない）。
    /// </summary>
    private PitchTrajectory ComputeTrajectory(PitchPlan plan)
    {
        var rpm = RpmForPlan(plan);
        var spec = new PitchSpec { SpeedKmh = plan.VelocityKmh, SpinRadPerSec = PitchSpec.BackspinFromRpm(rpm) };
        return PitchSimulator.Simulate(spec, _ctx.Aerodynamics, _ctx.Mound);
    }

    /// <summary>
    /// 判定用弾道特徴量を <see cref="TrajectoryFeatureTable"/> の補間で求める（設計書15 Phase E-1）。
    /// 毎球呼ぶが O(1) の配列参照のみ・RNG非消費。<see cref="ComputeTrajectory"/>（観測専用・毎回RK4）とは別経路。
    /// </summary>
    private PitchTrajectoryFeatures ComputeFeatures(PitchPlan plan)
    {
        var rpm = RpmForPlan(plan);
        var table = TrajectoryFeatureTable.GetOrBuild(_ctx.Aerodynamics, _ctx.Mound);
        return table.Lookup(plan.VelocityKmh, rpm);
    }

    /// <summary>キレ(Sharpness)→rpm の写像（設計書15 §0.1 Q12-5、観測用ComputeTrajectoryと判定用ComputeFeaturesで共有）。</summary>
    private double RpmForPlan(PitchPlan plan)
    {
        var sharpness = 50;
        foreach (var slot in _pitcher.EffectiveRepertoire)
        {
            if (slot.Type == plan.Type) { sharpness = slot.Sharpness; break; }
        }
        return _ctx.Pitching.SpinRpmBase + (sharpness - 50) * _ctx.Pitching.SpinRpmPerSharpness;
    }

    /// <summary>enum 結果で打席を確定させる（三振/四球）。従来ループの early return と同一。</summary>
    private PitchResolution Finish(PlateAppearanceResult result)
    {
        _result = new AtBatResult(result, _pitchCount) { PitchLog = _pitchLog };
        return new PitchResolution(EndsPlateAppearance: true);
    }

    /// <summary>
    /// バント試行で打席を確定させる（設計書15 Phase D-2b）。<see cref="AtBatResult.BuntOutcome"/> に詳細結果を
    /// 残し、GameEngine 側が PopOut/SacrificeSuccess/InfieldHit を区別して進塁（AdvanceOnBunt）を分岐できるようにする。
    /// </summary>
    private PitchResolution FinishBunt(PlateAppearanceResult result, BuntResult bunt)
    {
        _result = new AtBatResult(result, _pitchCount) { PitchLog = _pitchLog, BuntOutcome = bunt };
        return new PitchResolution(EndsPlateAppearance: true);
    }

    /// <summary>
    /// スクイズ試行で打席を確定させる（設計書15 Phase D-2c）。<see cref="AtBatResult.Squeeze"/> に詳細結果を
    /// 残し、GameEngine 側が得点・進塁・アウト計上を分岐できるようにする。
    /// </summary>
    private PitchResolution FinishSqueeze(PlateAppearanceResult result, SqueezeOutcome squeeze)
    {
        _result = new AtBatResult(result, _pitchCount) { PitchLog = _pitchLog, Squeeze = squeeze };
        return new PitchResolution(EndsPlateAppearance: true);
    }

    /// <summary>打席継続。規定球数上限（設計書: MaxPitches）に達したら安全側で凡打確定＝従来の loop 脱出後の挙動。</summary>
    private PitchResolution Continue()
    {
        if (_pitchCount >= AtBatResolver.MaxPitches)
        {
            // 規定球数上限（ファウル粘り等）。安全側で凡打扱い（従来 for ループを抜けた後の return と同一）。
            _result = new AtBatResult(PlateAppearanceResult.InPlayOut, AtBatResolver.MaxPitches) { PitchLog = _pitchLog };
            return new PitchResolution(EndsPlateAppearance: true);
        }
        return new PitchResolution(EndsPlateAppearance: false);
    }
}
