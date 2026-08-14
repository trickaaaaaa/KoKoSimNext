using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>打球の守備解決結果。</summary>
public enum BattedBallResult
{
    Foul,
    Out,
    Error,
    Single,
    Double,
    Triple,
    HomeRun,
}

/// <summary>
/// 失策の種類（issue #439 / #632）。Catch=捕球エラー（お手玉・落球）／Throw=悪送球／
/// Tunnel=トンネル（股・脇を抜けて球が外野へ行く）。
///
/// <para>Catch/Throw は「絵の選び方」だけの観測データだが、<b>Tunnel は球の行き先が違う</b>
/// ＝失策後の走者の進塁が幾何で決まる（不変条件#1）。issue #629 の失策連鎖（悪送球だけに乗る
/// 一律確率）と役割が分かれていて、こちらは<b>外野からの返球との時間勝負</b>になる。</para>
/// </summary>
public enum FieldingErrorKind { Catch, Throw, Tunnel }

/// <summary>
/// 守備解決の詳細（結果＋幾何・時間情報, CHANGELOG 32）。
/// 判定に使った着地点・滞空・捕球/送球時刻を捨てずに保持し、タイムライン出力の素材にする。
/// 時刻はすべて「接触（打った瞬間）＝0秒」基準。
/// </summary>
public sealed record FieldingPlay
{
    public required BattedBallResult Result { get; init; }
    /// <summary>着地点（X=+一塁側, Z=センター方向）[m]。</summary>
    public required double LandingX { get; init; }
    public required double LandingZ { get; init; }
    public required double HangTimeSeconds { get; init; }
    public required double ApexHeightM { get; init; }
    public required double RangeM { get; init; }
    public required double BearingDeg { get; init; }
    /// <summary>
    /// 打球の回収点（着地後の転がりの終端。X=+一塁側, Z=センター方向）[m]。
    /// <c>null</c> = 回収を解いていない経路（ファウル・本塁打・空中捕球）＝**着地点と同値**。
    /// 読む側は必ず <c>?? LandingX/Z</c> でフォールバックすること。
    ///
    /// nullable なのは「未設定」を表現可能にするため（Issue #315）。非nullable の既定 0 は
    /// 本塁という正当な座標なので、埋め忘れが型でも実行時でも検出されず、テストの手書き
    /// フィクスチャが誤った挙動を「正しい」として固定してしまう事故が実際に起きた（Issue #301）。
    /// </summary>
    public double? FieldedX { get; init; }
    public double? FieldedZ { get; init; }
    /// <summary>処理した野手（フライ捕球/ゴロ処理）。ファウル・本塁打では null。</summary>
    public FieldPosition? FielderRole { get; init; }
    /// <summary>野手が打球に到達（捕球/処理）した時刻[s]。</summary>
    public double? FieldedAtSeconds { get; init; }
    /// <summary>一塁送球が到達した時刻[s]（内野ゴロのみ）。<see cref="UnassistedPutout"/> のときは
    /// 送球ではなく「一塁手がベースを踏んで刺殺が成立した時刻」を表す。</summary>
    public double? ThrowArriveSeconds { get; init; }
    /// <summary>
    /// アンアシスト刺殺（3U, Issue #400）。処理野手が一塁手で、送球するより自分でベースを踏むほうが
    /// 速いと物理層の時間比較（握り替え0.70sを払う送球 vs 捕球点→ベースを走り込む自重）で決まったとき true。
    /// このとき <see cref="ThrowArriveSeconds"/> は走り込みの到達時刻＝刺殺成立時刻を表し、表示層は送球
    /// セグメントも投手カバーも生成しない。処理野手が一塁手の内野ゴロ以外では常に false（不変条件#1: 判定は
    /// 表示能力値でなく物理層の時間比較で決める）。
    /// </summary>
    public bool UnassistedPutout { get; init; }
    /// <summary>打者走者の一塁到達時刻[s]。</summary>
    public required double BatterToFirstSeconds { get; init; }
    public bool IsFly { get; init; }
    /// <summary>処理野手の送球速度[m/s]（本塁クロスプレーの中継起点, 設計書12 §3 F2）。処理野手なしでは null。</summary>
    public double? FielderThrowSpeedMps { get; init; }
    /// <summary>処理野手の守備(Fielding)。返球・中継の握り替えの能力伸縮に使う（Issue #36）。既定50＝恒等。</summary>
    public int FielderFielding { get; init; } = 50;
    /// <summary>処理野手の送球精度(ThrowAccuracy)。失策連鎖の能力駆動に使う（Issue #37）。既定50＝恒等。</summary>
    public int FielderThrowAccuracy { get; init; } = 50;
    /// <summary>
    /// 一塁での判定margin[s]（判定オーバーレイ, Issue #59）。守備所要−走者所要＋ForceOutMarginSeconds
    /// （0=判定境界、正=セーフ寄り、負=アウト寄り）。内野ゴロで一塁送球が絡んだ判定のみ非null。
    /// </summary>
    public double? JudgementMarginSeconds { get; init; }

    /// <summary>
    /// 空中捕球の余裕[s]（判定オーバーレイ「フェア」, Issue #328②）。CatchTimeBudget−到達所要
    /// （正=捕球に余裕あり、負=間に合わず落球）。フライ/ライナーのみ非null（ゴロ・本塁打・ファウルはnull）。
    /// </summary>
    public double? CatchMarginSeconds { get; init; }

    /// <summary>
    /// 失策の種類（issue #439）。<see cref="Result"/>==Error のときだけ非null。
    /// 引いたロールの結果を記録するだけ＝新規RNGなし（不変条件#2）。
    /// </summary>
    public FieldingErrorKind? ErrorKind { get; init; }

    /// <summary>
    /// 失策を犯した野手（issue #632）。トンネルでは<b>処理野手（回収する外野手）と別人</b>になるので、
    /// 失策の個人帰属はこちらから引く。null＝<see cref="FielderRole"/> と同じ（捕球エラー・悪送球）。
    /// </summary>
    public FieldPosition? ErrorFielderRole { get; init; }

    /// <summary>
    /// 失策が確定した瞬間[s]（表示専用, issue #632）。トンネルでは<b>抜かれた瞬間</b>＝
    /// 外野手が球を回収する <see cref="FieldedAtSeconds"/> よりずっと前。
    /// null＝従来どおり（捕球エラー＝処理点で弾いた時刻／悪送球＝一塁へ届くはずだった時刻）。
    /// </summary>
    public double? ErrorAtSeconds { get; init; }

    // --- 打球のバウンド（Issue #63 / OPEN-QUESTIONS Q14） ---

    /// <summary>
    /// 打球の4分類（ゴロ/バウンド/ライナー/フライ）。物理層の積分結果から導出する。
    /// <c>null</c> = 未判定（分類が存在しないファウル。弾道積分を回す前に返す経路）。
    /// 既定を <c>Grounder</c> にすると分類を持たない打球まで「ゴロ」と名乗るので nullable（Issue #315）。
    /// </summary>
    public BattedBallClass? Class { get; init; }
    /// <summary>最大バウンド頂点[m]（接地後の弾みの最高到達点。フライ/ライナー捕球では0）。</summary>
    public double BounceApexHeightM { get; init; }
    /// <summary>接地後の弾みの回数（転がりへ移行するまで）。</summary>
    public int BounceCount { get; init; }
    /// <summary>イレギュラーバウンドが発生したか（エラー確率へ加算される）。</summary>
    public bool IrregularBounce { get; init; }
    /// <summary>バウンド/転がりで内野を抜けた打球か（外野へ二次展開した）。</summary>
    public bool ThroughInfield { get; init; }

    // --- 抜けた打球の「寄る/飛びつく」表示専用データ（Issue #336②） ---

    /// <summary>
    /// 内野を抜けた打球（<see cref="ThroughInfield"/>=true）で最も惜しく打球ラインに迫った内野手。
    /// タイムラインの「寄る／飛びつく」演出の入力（表示専用・判定不使用）。抜けていない打球や
    /// 該当が無い経路（想定外）では null。
    /// </summary>
    public FieldPosition? ClosestInfielderRole { get; init; }
    /// <summary>その内野手が打球ラインに最も近づいた点[m]（X=一塁側, Z=センター方向）。表示専用。</summary>
    public double? ClosestApproachX { get; init; }
    public double? ClosestApproachZ { get; init; }
}

/// <summary>
/// 守備解決（設計書01 §2⑥）。打球を弾道積分し、着地点・滞空時間と守備位置の幾何から
/// 捕球可否・送球 vs 走者を判定して安打/凡打/本塁打を導く。
/// </summary>
public static class FieldingResolver
{
    /// <summary>互換API（結果のみ）。</summary>
    public static BattedBallResult Resolve(
        BattedBall ball,
        FieldGeometry field,
        Aerodynamics aero,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        IRandomSource rng)
        => ResolveDetailed(ball, field, aero, batter, fielders, coeff, rng).Result;

    /// <summary>詳細解決。判定ロジック・乱数消費順は Resolve と完全に同一（決定論の後方互換）。</summary>
    public static FieldingPlay ResolveDetailed(
        BattedBall ball,
        FieldGeometry field,
        Aerodynamics aero,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        IRandomSource rng)
    {
        // 本塁→一塁の実戦タイム（設計書02 §4.1b）: 天井速度での距離÷速度＋実戦オフセット−左打者短縮。
        var handednessBonus = batter.Bats is Handedness.Left or Handedness.Switch
            ? coeff.LeftBatterFirstStepBonusSeconds : 0.0;
        var runnerTime = field.BaseDistanceM / batter.SpeedToFirstMps()
                         + coeff.RunnerReactionSeconds - handednessBonus;

        // フェア/ファウル: 方位角が±45°を越えたらファウル。
        if (Math.Abs(ball.BearingDeg) > 45.0)
        {
            return new FieldingPlay
            {
                Result = BattedBallResult.Foul,
                LandingX = 0, LandingZ = 0, HangTimeSeconds = 0, ApexHeightM = 0,
                RangeM = 0, BearingDeg = ball.BearingDeg, BatterToFirstSeconds = runnerTime,
            };
        }

        var ground = BallisticIntegrator.IntegrateToGround(
            ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), aero);

        var landing = ground.LandingPosition;
        var bearingRad = ball.BearingDeg * Math.PI / 180.0;
        var range = Math.Sqrt(landing.X * landing.X + landing.Z * landing.Z);
        var fenceDist = field.FenceDistance(bearingRad);

        // 4分類のうち「空を飛ぶ側」（Issue #63 (b)）。頂点÷水平到達距離が小さい＝低い弾道＝ライナー。
        // 地上側（ゴロ/バウンド）は内野ゴロ経路でバウンド積分の結果から決める。
        var airClass = ground.ApexHeightM / Math.Max(range, 1e-6) <= coeff.LinerApexRangeRatio
            ? BattedBallClass.Liner : BattedBallClass.Fly;

        // 回収点(FieldedX/Z)はここでは埋めない。転がりを解く経路だけが上書きし、それ以外は
        // null＝着地点フォールバックで同値になる（Issue #315）。本番でもフォールバックを通す
        // ことで、テストだけが踏む死んだ経路にならないようにしている。
        FieldingPlay Base(BattedBallResult result) => new()
        {
            Result = result,
            LandingX = landing.X,
            LandingZ = landing.Z,
            HangTimeSeconds = ground.HangTimeSeconds,
            ApexHeightM = ground.ApexHeightM,
            RangeM = range,
            BearingDeg = ball.BearingDeg,
            BatterToFirstSeconds = runnerTime,
            Class = airClass,
        };

        // 本塁打: フェンス距離を越えて着地し、かつ壁を越える高さで飛んでいる。
        if (range >= fenceDist && ground.ApexHeightM >= field.FenceHeightM)
        {
            return Base(BattedBallResult.HomeRun);
        }

        var isFly = ground.ApexHeightM >= coeff.FlyApexThresholdM;

        // 空中捕球判定（フライ／ライナー）。
        if (isFly)
        {
            var (catcher, reach) = NearestReach(fielders, landing);
            var catchBudget = CatchTimeBudget(ground.HangTimeSeconds, catcher, coeff);
            // 捕球余裕（正=余裕あり、負=間に合わず）。判定オーバーレイ「フェア」（Issue #328②）の表示だけでなく、
            // フライ落球の難度連動（issue #532）にも使う＝平凡な飛球ほど落球確率が下限へ落ちる物理量。
            var catchMargin = catchBudget - reach;
            if (reach <= catchBudget)
            {
                // フライ落球は「実際に捕る野手（NearestReach）」の Catching で引く（issue #532 C: 距離で選ぶ
                // NearestFielder との食い違いを解消）。難度は捕球余裕 catchMargin で伸縮（issue #532 A/B）。
                var result = MaybeFlyError(catcher, catchMargin, coeff, rng);
                return Base(result) with
                {
                    IsFly = true,
                    FielderRole = catcher.Position,
                    FieldedAtSeconds = ground.HangTimeSeconds,
                    FielderThrowSpeedMps = catcher.Attributes.ThrowSpeedMps,
                    FielderFielding = catcher.Attributes.Fielding,
                    FielderThrowAccuracy = catcher.Attributes.ThrowAccuracy,
                    CatchMarginSeconds = catchMargin,
                    // 空中捕球のエラーは常に捕球エラー（落球）。ロール結果の記録だけ＝新規RNGなし（issue #439）
                    ErrorKind = result == BattedBallResult.Error ? FieldingErrorKind.Catch : null,
                };
            }
            // 捕れないフライ → 安打。着地後の転がりと幾何・走力で塁打数を決める。
            return Hit(Base, ground, landing, fenceDist, field, batter, fielders, coeff, runnerTime)
                with { IsFly = true, CatchMarginSeconds = catchMargin };
        }

        // ゴロ/低いライナー。着地後のバウンド積分（Issue #63 (a)）をここで解く。
        if (range <= coeff.InfieldDepthM)
        {
            // イレギュラーはここで一律確率で抽選する（Issue #63 (c)）。
            var irregular = DrawIrregular(coeff, rng);
            var path = BouncePath.Compute(landing, ground.LandingVelocity, ground.HangTimeSeconds, coeff, irregular);
            var groundClass = path.MaxBounceApexM >= coeff.BouncerApexThresholdM
                ? BattedBallClass.Bouncer : BattedBallClass.Grounder;

            FieldingPlay GroundBase(BattedBallResult result) => Base(result) with
            {
                Class = groundClass,
                BounceApexHeightM = path.MaxBounceApexM,
                BounceCount = path.Hops.Count,
                IrregularBounce = path.Irregular,
            };

            // Issue #63 (b) やること2: バウンド/転がりで内野手を抜けて外野へ達する打球。
            // どの内野手も打球を（守備範囲内・グラブの届く高さで）内野の外縁より手前で捕えられず、
            // かつ外野まで運ぶ強い打球のときだけ「内野を抜けた」＝外野への二次展開。
            // この頭上抜け判定（crossBoundary かつ !InfielderCanCutOff）は Issue #261 でも一切変えない
            // ＝ThroughInfield 件数・#63 の帯を保存する。内野で収まる打球の「処理野手」だけを実捕球点へ移す。
            var crossBoundary = path.TimeCrossingRange(coeff.InfieldDepthM, coeff.InfieldInterceptStepSeconds);
            if (crossBoundary is { } oct && !InfielderCanCutOff(fielders, path, coeff))
            {
                // 抜けた先は Issue #24 で校正済みの集約転がりモデルへ引き継ぐ。
                var td = path.TouchdownAtOrAfter(oct);
                var through = td.Airborne
                    ? ExtraBaseResolver.ComputeRoll(td.Position, td.Velocity, td.Seconds, fenceDist, coeff)
                    : ExtraBaseResolver.RollFrom(
                        td.Position, path.DirectionAt(td.Seconds), path.HorizontalSpeedAt(td.Seconds),
                        td.Seconds, fenceDist, coeff);
                var result = HitFromRoll(GroundBase, through, field, batter, fielders, coeff, runnerTime)
                    with { ThroughInfield = true };
                // 表示専用（Issue #336②）: 最も惜しく迫った内野手と最接近点。判定・帯には無関係。
                if (FindClosestInfielderMiss(fielders, path, coeff) is { } miss)
                {
                    result = result with
                    {
                        ClosestInfielderRole = miss.Fielder.Position,
                        ClosestApproachX = miss.Point.X,
                        ClosestApproachZ = miss.Point.Z,
                    };
                }
                return result;
            }

            // 内野が処理 → 一塁送球 vs 打者走者。処理野手・処理時刻・捕球点はバウンド経路を辿って
            // 「打球が実際に転がり込む内野手」を実捕球点で決める（Issue #261。着地点決め打ちを廃止）。
            var (infielder, fieldTimeRaw, catchPoint, advanceM) = FieldInfieldGrounder(fielders, path, coeff);
            var fieldTime = fieldTimeRaw;
            // Issue #63 (a) やること1: 高く弾むバウンド（チョッパー）は落ちてくるまで野手が待たされる。
            // 待ち時間＝最大バウンド頂点からの自由落下時間×係数（バウンド分類のときだけ効かせる）。
            if (groundClass == BattedBallClass.Bouncer)
            {
                fieldTime += coeff.ChopperWaitFactor
                             * Math.Sqrt(2.0 * path.MaxBounceApexM / BouncePath.GravityMps2);
            }
            var throwDist = (field.FirstBase - catchPoint).Length;
            // 前進捕球のぶんだけ体勢が悪い（issue #630）。前進0＝従来と恒等。捕球点が本塁寄りへ動いて
            // 送球距離が縮むのを、握り替えの追加時間と送球精度の低下で食い戻す＝一方的な守備強化にしない。
            var chargeFrac = MaxChargeAdvanceM(infielder.Position, coeff) is var maxAdvance and > 0.0
                ? MathUtil.Clamp(advanceM / maxAdvance, 0.0, 1.0)
                : 0.0;
            var throwAccuracy = (int)Math.Round(
                infielder.Attributes.ThrowAccuracy - coeff.ChargeThrowAccuracyPenalty * chargeFrac);
            // 握り替え（トランスファー）を守備力で伸縮（Issue #36）。守備50で×1.0＝現行と恒等。
            var transfer = coeff.ThrowTransferSeconds
                           * infielder.Attributes.TransferFactor(coeff.ThrowTransferFieldingSlope, coeff.ThrowTransferFactorMin)
                           + coeff.ChargeThrowPenaltySeconds * chargeFrac;
            var throwTime = fieldTime + coeff.InfieldPlayOverheadSeconds
                            + transfer + throwDist / infielder.Attributes.ThrowSpeedMps;
            // アンアシスト刺殺（3U, Issue #400）: 処理野手が一塁手なら、投手カバーへ送球する以外に「自分で
            // ベースを踏む」選択肢を持つ。捕球点がベース至近なら握り替え0.70sを払わずに済むぶん自重で踏むほうが
            // 速い＝実際の 3U が発生する。所要は握り替え・送球を除いた「捕球点→一塁ベースを走り込む」時間で、
            // 送球所要と物理層の秒だけで比較して速いほうを採る（不変条件#1: 表示能力値から確率を引かない）。
            var unassistedPutout = false;
            var defenseTime = throwTime;
            if (infielder.Position == FieldPosition.FirstBase)
            {
                var carryTime = fieldTime + coeff.InfieldPlayOverheadSeconds
                                + throwDist / infielder.Attributes.SprintSpeedMps;
                if (carryTime < throwTime)
                {
                    unassistedPutout = true;
                    defenseTime = carryTime;
                }
            }
            // 判定margin（Issue #59）: 0=判定境界（=defenseTime+ForceOutMargin と runnerTime が拮抗）、
            // 正=セーフ寄り、負=アウト寄り。表示専用（判定・帯には影響しない）。
            var judgementMargin = defenseTime - runnerTime + coeff.ForceOutMarginSeconds;

            // イレギュラーバウンドは捕球ロールへ加算＝「アウトになる打球でも弾いて失策」になりうる（Issue #63 (c)）。
            var errorBonus = irregular.Active ? coeff.ErrorIrregularBonus : 0.0;
            if (defenseTime + coeff.ForceOutMarginSeconds <= runnerTime)
            {
                // 送球が間に合う → アウト。捕球ロール(Catching＋イレギュラー加算)→送球ロール(ThrowAccuracy)の
                // 2段階失策（Issue #37/#63, design-14 P1-6b）。失策ロールは実捕球点の野手で引く（Issue #261）。
                var (result, errorKind) = MaybeInfieldError(
                    fielders, catchPoint, infielder, coeff, rng, errorBonus, throwAccuracy);
                if (errorKind == FieldingErrorKind.Tunnel)
                {
                    // トンネル（issue #632）: 野手は触れず、球は捕球点から<b>その瞬間の速度のまま</b>
                    // 転がり続ける。以降は内野を抜けた打球とまったく同じ経路＝外野手が回収して返球する
                    // （回収野手・回収時刻・回収点・肩は HitFromRoll が埋める）ので、二塁走者の生還は
                    // 一律確率ではなく外野からの返球との時間勝負（HomePlayResolver）で決まる。
                    // 時刻は fieldTimeRaw（経路上の実時刻）で採る。fieldTime のチョッパー待ちは
                    // 「野手が落ちてくるのを待つ」時間なので、触らずに抜けた球には乗らない。
                    var through = ExtraBaseResolver.RollFrom(
                        catchPoint, path.DirectionAt(fieldTimeRaw), path.HorizontalSpeedAt(fieldTimeRaw),
                        fieldTimeRaw, fenceDist, coeff);
                    return HitFromRoll(GroundBase, through, field, batter, fielders, coeff, runnerTime) with
                    {
                        Result = BattedBallResult.Error,
                        ThroughInfield = true,
                        ErrorKind = FieldingErrorKind.Tunnel,
                        ErrorFielderRole = infielder.Position,     // 失策は抜かれた内野手に付く
                        ErrorAtSeconds = fieldTimeRaw,             // 「エラー」が出るのは抜かれた瞬間
                        // 表示専用（Issue #336②）: 抜かれた本人が打球ラインへ飛びつく絵の入力。
                        ClosestInfielderRole = infielder.Position,
                        ClosestApproachX = catchPoint.X,
                        ClosestApproachZ = catchPoint.Z,
                    };
                }
                return GroundBase(result) with
                {
                    FieldedX = catchPoint.X,
                    FieldedZ = catchPoint.Z,
                    FielderRole = infielder.Position,
                    FieldedAtSeconds = fieldTime,
                    ThrowArriveSeconds = defenseTime,
                    UnassistedPutout = unassistedPutout,
                    FielderThrowSpeedMps = infielder.Attributes.ThrowSpeedMps,
                    FielderFielding = infielder.Attributes.Fielding,
                    FielderThrowAccuracy = throwAccuracy,
                    JudgementMarginSeconds = judgementMargin,
                    ErrorKind = errorKind,
                };
            }
            // 間に合わず内野安打（失策ロールは引かない＝main の校正モデルを保つ）。
            return GroundBase(BattedBallResult.Single) with
            {
                FieldedX = catchPoint.X,
                FieldedZ = catchPoint.Z,
                FielderRole = infielder.Position,
                FieldedAtSeconds = fieldTime,
                ThrowArriveSeconds = defenseTime,
                UnassistedPutout = unassistedPutout,
                FielderThrowSpeedMps = infielder.Attributes.ThrowSpeedMps,
                FielderFielding = infielder.Attributes.Fielding,
                FielderThrowAccuracy = throwAccuracy,
                JudgementMarginSeconds = judgementMargin,
            };
        }

        // 内野を抜けた（外野への低い打球）→ 安打。転がりと幾何・走力で塁打数を決める。
        return Hit(Base, ground, landing, fenceDist, field, batter, fielders, coeff, runnerTime);
    }

    /// <summary>
    /// イレギュラーバウンドの抽選（Issue #63 (c)。一律確率＝球場の土質との接続は将来の別Issue）。
    /// 乱数消費: 発生判定1回＋発生時に横ぶれ・反発の2回。
    /// </summary>
    private static IrregularBounce DrawIrregular(FieldingCoefficients coeff, IRandomSource rng)
    {
        if (!MathUtil.Chance(coeff.IrregularBounceProb, rng)) return IrregularBounce.None;
        var lateral = (rng.NextDouble() * 2.0 - 1.0) * coeff.IrregularBounceLateralDeg;
        var scale = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * coeff.IrregularBounceRestitutionSwing;
        return new IrregularBounce(true, lateral, scale);
    }

    /// <summary>
    /// いずれかの内野手が、打球が内野の外縁を越える前に守備範囲（横の半径＋グラブの届く高さ）で
    /// 打球のライン上を捕えられるか（Issue #63 やること2の判定）。乱数は消費しない＝決定論。
    /// 捕えられない＝ホールを抜く／頭上を越すほど強い打球のときだけ内野を抜けたと判定する。
    /// </summary>
    private static bool InfielderCanCutOff(
        IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients coeff)
    {
        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        var radius = Math.Max(0.0, coeff.InfielderFieldingRadiusM);
        for (var t = path.HangTimeSeconds; ; t += step)
        {
            var p = path.PositionAt(t);
            if (Math.Sqrt(p.X * p.X + p.Z * p.Z) > coeff.InfieldDepthM) return false; // 内野を出た
            if (path.HeightAt(t) <= coeff.InfielderReachHeightM)                        // グラブの届く高さ
            {
                foreach (var f in fielders)
                {
                    if (!IsInfield(f)) continue;
                    var travel = Math.Max(0.0, (p - f.Location).Length - radius);
                    var reach = travel / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
                    if (reach <= t) return true; // この時刻・位置で捕えられる
                }
            }
        }
    }

    /// <summary>
    /// 内野を抜けた打球（<see cref="InfielderCanCutOff"/>=false）について、最も打球ラインに迫った
    /// 内野手とその最接近点を求める（Issue #336②・表示専用）。<see cref="InfielderCanCutOff"/> と
    /// 同じ経路・同じ式（守備範囲半径込みの到達可否）を辿るだけの副産物で、判定・帯には一切使わない。
    /// 乱数は消費しない（決定論）。誰も打球の高さ帯を通らない（想定外）場合のみ null。
    /// </summary>
    private static (Fielder Fielder, Vector3D Point)? FindClosestInfielderMiss(
        IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients coeff)
    {
        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        var radius = Math.Max(0.0, coeff.InfielderFieldingRadiusM);
        Fielder? best = null;
        var bestPoint = default(Vector3D);
        var bestShortfall = double.PositiveInfinity;
        for (var t = path.HangTimeSeconds; ; t += step)
        {
            var p = path.PositionAt(t);
            if (Math.Sqrt(p.X * p.X + p.Z * p.Z) > coeff.InfieldDepthM) break; // 内野を出た
            if (path.HeightAt(t) > coeff.InfielderReachHeightM) continue;     // グラブが届かない高さ
            foreach (var f in fielders)
            {
                if (!IsInfield(f)) continue;
                var travel = Math.Max(0.0, (p - f.Location).Length - radius);
                var reach = travel / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
                var shortfall = reach - t; // 正=間に合わない。最小=最も惜しかった。
                if (shortfall < bestShortfall)
                {
                    bestShortfall = shortfall;
                    best = f;
                    bestPoint = p;
                }
            }
        }
        return best is null ? null : (best, bestPoint);
    }

    /// <summary>
    /// 内野ゴロの処理野手・処理時刻・捕球点を、バウンド経路（Issue #63 の <see cref="BouncePath"/>）を辿って決める
    /// （Issue #261）。着地点（本塁前でワンバウンドした一点）ではなく「打球が実際に転がり込む内野手」が
    /// 自分の守備範囲で処理する。乱数は消費しない＝決定論（不変条件#2）。
    ///
    /// 経路を時間刻みで走査し、内野手ごとに次の2つを集める:
    /// <list type="bullet">
    /// <item><b>処理野手（誰が）</b>: グラブの届く高さ・内野内を通る点で「その時刻までにその点へ実際に届く」
    ///   最初の内野手。到達判定には遊びの半径を与えず実距離で見る（<see cref="InfielderCanCutOff"/> の頭上抜け判定
    ///   ＝帯校正ノブ <see cref="FieldingCoefficients.InfielderFieldingRadiusM"/> はダイビングを含む広い半径で、
    ///   これを流用すると通り道の近くを通っただけの打球までマウンドの投手が吸い込む＝本Issueの不具合になる）。
    ///   投手はフォロースルー直後で守備体勢が崩れているぶん追加の反応遅延を負う
    ///   （<see cref="FieldingCoefficients.PitcherFieldingReactionPenaltySeconds"/>）。</item>
    /// <item><b>捕球点・処理時刻（どこで・いつ）</b>: 基準は処理野手の守備位置に最も近く打球が通る点
    ///   （＝最短の守備範囲）で、強い打球は<b>自分の間合いで</b>捕る（処理時刻は打球がその点へ来る時刻＝打球到達に
    ///   間に合えば待ち、遅れれば追いつく時刻）。緩い打球だけ前へ出て早い時刻で捕る
    ///   （<see cref="ChargeCatch"/>＝issue #630。従来はここが常に「待ち」で、ボテボテのゴロほど長く突っ立って
    ///   待つ絵と内野安打を作っていた）。</item>
    /// </list>
    /// 誰も間に合わないまま内野内で失速して死ぬ緩いゴロは、打球が最も近くを通った内野手が回収する（内野安打寄り）。
    /// </summary>
    private static (Fielder Fielder, double Time, Vector3D Point, double AdvanceM) FieldInfieldGrounder(
        IReadOnlyList<Fielder> fielders, BouncePath path, FieldingCoefficients coeff)
    {
        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        var end = path.SettleAtSeconds;

        var infield = new List<Fielder>(5);
        foreach (var f in fielders)
        {
            if (IsInfield(f)) infield.Add(f);
        }
        var n = infield.Count;
        var gloveTime = new double[n];      // 実捕球できる最初のボール時刻（∞=最後まで間に合わない）
        var cutTime = new double[n];        // 守備範囲(半径)込みで触れる最初の時刻＝InfielderCanCutOff と同式
        var cutPoint = new Vector3D[n];     // その触れる点
        var zoneDist = new double[n];       // 守備位置への最接近距離
        var zoneBallTime = new double[n];   // その最接近点を打球が通る時刻
        var zonePoint = new Vector3D[n];    // その最接近点
        var cutRadius = Math.Max(0.0, coeff.InfielderFieldingRadiusM);
        for (var k = 0; k < n; k++)
        {
            gloveTime[k] = double.PositiveInfinity;
            cutTime[k] = double.PositiveInfinity;
            cutPoint[k] = path.Landing;
            zoneDist[k] = double.MaxValue;
            zoneBallTime[k] = path.HangTimeSeconds;
            zonePoint[k] = path.Landing;
        }

        for (var t = path.HangTimeSeconds; t <= end + step; t += step)
        {
            var tt = Math.Min(t, end);
            var p = path.PositionAt(tt);
            if (Math.Sqrt(p.X * p.X + p.Z * p.Z) > coeff.InfieldDepthM) break; // 内野を出た
            var aboveGlove = path.HeightAt(tt) > coeff.InfielderReachHeightM;  // グラブより高い（またぐ）
            for (var k = 0; k < n; k++)
            {
                var f = infield[k];
                var dist = (p - f.Location).Length;
                if (dist < zoneDist[k]) { zoneDist[k] = dist; zoneBallTime[k] = tt; zonePoint[k] = p; }
                if (aboveGlove) continue;
                if (double.IsPositiveInfinity(cutTime[k]))
                {
                    // 頭上抜け判定（InfielderCanCutOff）と**同じ式**。あちらが「抜けていない」と言った
                    // 打球は、必ずここで誰かが触れる時刻・点を持つ＝2つの判定が食い違わない。
                    var travel = Math.Max(0.0, dist - cutRadius);
                    if (travel / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds <= tt)
                    {
                        cutTime[k] = tt;
                        cutPoint[k] = p;
                    }
                }
                if (!double.IsPositiveInfinity(gloveTime[k])) continue;
                var reach = dist / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
                if (f.Position == FieldPosition.Pitcher) reach += coeff.PitcherFieldingReactionPenaltySeconds;
                if (reach <= tt) gloveTime[k] = tt; // この点・時刻で実捕球できる
            }
        }

        // 処理野手＝最初に実捕球できる内野手。誰も間に合わなければ最接近が最小の内野手（ゾーン最寄り）。
        var best = -1;
        var bestGlove = double.PositiveInfinity;
        for (var k = 0; k < n; k++)
        {
            if (gloveTime[k] < bestGlove) { bestGlove = gloveTime[k]; best = k; }
        }
        if (best < 0)
        {
            // 実距離では誰も捕れない。**まず頭上抜け判定と同じ式（守備範囲の半径込み＝突っ込む/飛びつく）
            // で触れる野手を採る。** ここを飛ばすと、「抜けていない」と判定された打球なのに
            // 処理野手が居ないことになり、下のゾーン最寄りが打球を内野の外まで追いかける
            // ——BouncePath は内野専用モデル（自由転がりは v²/2a で最大180m）なので、
            // 長い時刻で引くと適用範囲の外へ外挿する（実測: 捕球点が本塁から193m）。
            var bestCut = double.PositiveInfinity;
            for (var k = 0; k < n; k++)
            {
                if (cutTime[k] < bestCut) { bestCut = cutTime[k]; best = k; }
            }
            if (best >= 0) return (infield[best], cutTime[best], cutPoint[best], 0.0);

            // 誰も触れない＝内野内で失速して死ぬ緩いゴロ（打球は内野の中で止まっている）。
            // 最も近くを通った内野手が拾いに行く（内野安打寄り）。
            var bestZone = double.MaxValue;
            for (var k = 0; k < n; k++)
            {
                if (zoneDist[k] < bestZone) { bestZone = zoneDist[k]; best = k; }
            }
        }
        if (best < 0)
        {
            // 内野内をグラブの届く高さで通る点が皆無（想定外）→ 従来どおり着地点の最寄り内野手。
            var fallback = NearestInfielder(fielders, path.Landing);
            return (fallback, ReachTime(fallback, path.Landing), path.Landing, 0.0);
        }

        var chosen = infield[best];
        var reachToZone = ReachTime(chosen, zonePoint[best]);
        // 打球到達に間に合う＝待つ余裕がある。緩い打球はその余裕で前へ出る（issue #630）。
        if (reachToZone <= zoneBallTime[best])
        {
            var charge = ChargeCatch(
                chosen, path, zoneDist[best], zoneBallTime[best], zonePoint[best], coeff);
            return (chosen, charge.Time, charge.Point, charge.AdvanceM);
        }

        // 間に合わない＝追いかける。**1回の代入では解けない**: 「自分が最接近点へ着く時刻の打球位置」は
        // そのぶん先へ転がっているので、そこへはまだ届いていない。以前はここで1回だけ代入しており、
        // 野手が到底走れない距離を「走ったこと」にしていた（実測: マウンドの投手が 31m を 0.89秒＝
        // 35 m/s。処理時刻 FieldedAtSeconds はアウト/セーフ判定に入るので、守備に甘い側の誤差だった）。
        // ここへ来るのは緩いゴロだけ（強い打球は上の cutTime で内野内に決着している）ので、
        // 打球は内野の中で止まる＝この反復は単調増加で必ず収束する（実測の遅れは最大 0.023秒）。
        var time = reachToZone;
        for (var i = 0; i < CatchUpIterations; i++)
        {
            var next = ReachTime(chosen, path.PositionAt(time));
            if (next <= time + CatchUpToleranceSeconds) break;
            time = next;
        }
        return (chosen, time, path.PositionAt(time), 0.0);
    }

    /// <summary>
    /// 前進捕球（チャージ, issue #630）。待って捕るなら <paramref name="zoneBallTime"/> に
    /// <paramref name="zonePoint"/> で捕る打球について、<b>それより早く捕れる本塁寄りの点</b>を返す。
    ///
    /// <para>前へ出られる距離は「打球の緩さ」で決める:
    /// 上限 ×(1 − 待って捕るときの打球速度 / <see cref="FieldingCoefficients.ChargeSlowBallSpeedMps"/>)。
    /// <b>強い打球は上限0＝走査に入らず従来と完全に同じ捕球点・処理時刻を返す</b>（帯・絵とも不変）。
    /// しきいをまたぐ瞬間に前進量が飛ばないよう、比例で0へ落とす。</para>
    ///
    /// <para>「間に合うか」は処理野手の割り当て（<c>gloveTime</c>）と<b>同じ式</b>で見る
    /// ＝守備範囲の半径を足さない実距離＋投手の追加反応遅延。乱数は引かない（幾何と時間だけ）。</para>
    /// </summary>
    private static (double Time, Vector3D Point, double AdvanceM) ChargeCatch(
        Fielder f, BouncePath path, double zoneDist, double zoneBallTime, Vector3D zonePoint,
        FieldingCoefficients coeff)
    {
        var slowLimit = coeff.ChargeSlowBallSpeedMps;
        if (slowLimit <= 0.0) return (zoneBallTime, zonePoint, 0.0);
        var slowness = MathUtil.Clamp(
            (slowLimit - path.HorizontalSpeedAt(zoneBallTime)) / slowLimit, 0.0, 1.0);
        var cap = slowness * MaxChargeAdvanceM(f.Position, coeff);
        if (cap <= 0.0) return (zoneBallTime, zonePoint, 0.0);

        var reaction = f.Attributes.ReactionDelaySeconds
                       + (f.Position == FieldPosition.Pitcher ? coeff.PitcherFieldingReactionPenaltySeconds : 0.0);
        var step = Math.Max(0.005, coeff.InfieldInterceptStepSeconds);
        for (var t = path.HangTimeSeconds; t < zoneBallTime; t += step)
        {
            if (path.HeightAt(t) > coeff.InfielderReachHeightM) continue;  // グラブが届かない高さ
            var p = path.PositionAt(t);
            var dist = (p - f.Location).Length;
            if (dist - zoneDist > cap) continue;                           // ここまでは出られない
            if (dist / f.Attributes.SprintSpeedMps + reaction > t) continue; // その時刻までに届かない
            return (t, p, dist - zoneDist);                                // 最も早い＝いちばん前で捕る点
        }
        return (zoneBallTime, zonePoint, 0.0);
    }

    /// <summary>前へ出られる距離の上限[m]。コーナー（投手・一塁手・三塁手）は一塁が近く思い切って
    /// 突っ込める／ミドル（二塁手・遊撃手）は送球が長く体勢を崩せない（issue #630）。</summary>
    private static double MaxChargeAdvanceM(FieldPosition position, FieldingCoefficients coeff)
        => position is FieldPosition.Pitcher or FieldPosition.FirstBase or FieldPosition.ThirdBase
            ? coeff.ChargeMaxAdvanceCornerM
            : coeff.ChargeMaxAdvanceMiddleM;


    /// <summary>追いかけ捕球の反復上限（打球が止まれば数回で収まる。無限ループの保険）。</summary>
    private const int CatchUpIterations = 24;

    /// <summary>追いかけ捕球の収束判定[s]。</summary>
    private const double CatchUpToleranceSeconds = 0.005;

    private static bool IsInfield(Fielder f)
        => f.Position is FieldPosition.FirstBase or FieldPosition.SecondBase
            or FieldPosition.ThirdBase or FieldPosition.Shortstop or FieldPosition.Pitcher;

    /// <summary>
    /// 空中で捕れなかった打球（＝安打）の解決。着地後の転がり → 回収 → 各塁への送球 vs 打者走者。
    /// 乱数は消費しない（決定論・進塁判断は幾何と時間のみで決まる）。
    /// </summary>
    private static FieldingPlay Hit(
        Func<BattedBallResult, FieldingPlay> baseOf,
        BallisticIntegrator.GroundResult ground,
        Vector3D landing,
        double fenceDistanceM,
        FieldGeometry field,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        double runnerTime)
    {
        var roll = ExtraBaseResolver.ComputeRoll(
            landing, ground.LandingVelocity, ground.HangTimeSeconds, fenceDistanceM, coeff);
        return HitFromRoll(baseOf, roll, field, batter, fielders, coeff, runnerTime);
    }

    /// <summary>転がり経路から回収・塁打数を決めて FieldingPlay を組む（乱数不使用・決定論）。</summary>
    private static FieldingPlay HitFromRoll(
        Func<BattedBallResult, FieldingPlay> baseOf,
        ExtraBaseResolver.RollPath roll,
        FieldGeometry field,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        double runnerTime)
    {
        var (retriever, retrieveAt) = ExtraBaseResolver.Retrieve(fielders, roll, coeff);
        var result = ExtraBaseResolver.ResolveBases(
            roll, retriever, retrieveAt, field, batter, runnerTime, coeff);
        var fieldedPoint = roll.PositionAt(retrieveAt);
        return baseOf(result) with
        {
            FieldedX = fieldedPoint.X,
            FieldedZ = fieldedPoint.Z,
            FielderRole = retriever.Position,
            FieldedAtSeconds = retrieveAt,
            FielderThrowSpeedMps = retriever.Attributes.ThrowSpeedMps,
            FielderFielding = retriever.Attributes.Fielding,
            FielderThrowAccuracy = retriever.Attributes.ThrowAccuracy,
        };
    }

    /// <summary>
    /// 空中捕球で走れる時間の上限[s]。滞空時間比例（＝深い飛球ほど守備範囲が広がる）に
    /// 絶対上限を掛け、走路取りの巧拙（守備力）で伸縮させる。守備50・上限未満では従来と同一。
    /// </summary>
    private static double CatchTimeBudget(double hangTime, Fielder fielder, FieldingCoefficients coeff)
    {
        var routeFactor = 1.0 + (fielder.Attributes.Fielding - 50) * coeff.CatchReachFieldingSlope;
        return Math.Min(hangTime * coeff.CatchReachFactor, coeff.CatchReachCapSeconds) * routeFactor;
    }

    /// <summary>捕球エラー（Catching）ロール。rng を1回消費（守備力連動, issue #123）。
    /// extraProb はイレギュラーバウンド等の失策確率への加算（Issue #63・既定0で恒等）。</summary>
    private static bool CatchErrorOccurs(
        IReadOnlyList<Fielder> fielders, Vector3D landing, FieldingCoefficients coeff, IRandomSource rng,
        double extraProb = 0.0)
    {
        var f = NearestFielder(fielders, landing);
        // 守備力連動は非対称: 平均(50)より下（弱小）は急傾斜で大量失策へ、上（精鋭）は緩傾斜で
        // 「守備を上げるほど失策が減る」勾配を残す（下限へ潰さない, issue #123）。
        var catchingDelta = f.Attributes.Catching - 50;
        var slope = catchingDelta < 0 ? coeff.ErrorCatchingSlope : coeff.ErrorCatchingSlopeStrong;
        var errProb = coeff.ErrorBaseProb - catchingDelta * slope + extraProb;
        return MathUtil.Chance(MathUtil.Clamp(errProb, coeff.ErrorMinProb, coeff.ErrorMaxProb), rng);
    }

    /// <summary>
    /// フライ落球の失策確率（issue #532）。捕球余裕 <paramref name="catchMargin"/>[s] から難度を出し、
    /// フライ専用の基準確率 <see cref="FieldingCoefficients.FlyErrorBaseProb"/> を伸縮させる。
    /// 難度 = clamp(1 − catchMargin/RoutineMargin, 0, 1)＝余裕が大きい平凡な飛球ほど 0（落球確率が下限へ）、
    /// 余裕≈0 の際どい飛球で 1（基準確率フル）。守備力連動は内野ゴロと同じ非対称スロープ（弱小側は急・精鋭側は緩）を
    /// 難度と同時に掛け、平凡な飛球はどの守備力でもほぼ落とさない一方、際どい飛球で守備力差が出るようにする。
    /// </summary>
    private static double FlyCatchErrorProb(Fielder catcher, double catchMargin, FieldingCoefficients coeff)
    {
        var routine = Math.Max(1e-9, coeff.FlyErrorRoutineMarginSeconds);
        var difficulty = MathUtil.Clamp(1.0 - catchMargin / routine, 0.0, 1.0);
        var catchingDelta = catcher.Attributes.Catching - 50;
        var slope = catchingDelta < 0 ? coeff.ErrorCatchingSlope : coeff.ErrorCatchingSlopeStrong;
        var errProb = (coeff.FlyErrorBaseProb - catchingDelta * slope) * difficulty;
        return MathUtil.Clamp(errProb, coeff.ErrorMinProb, coeff.ErrorMaxProb);
    }

    /// <summary>フライ落球の判定（rng を1回消費＝従来のフライ捕球エラー判定と乱数消費順は同一, issue #532）。
    /// 落球確率だけが難度・実捕球者連動へ変わる（結果は変わる＝決定論 baseline 再生成を伴う。不変条件#2 は保たれる）。</summary>
    private static BattedBallResult MaybeFlyError(
        Fielder catcher, double catchMargin, FieldingCoefficients coeff, IRandomSource rng)
        => MathUtil.Chance(FlyCatchErrorProb(catcher, catchMargin, coeff), rng)
            ? BattedBallResult.Error : BattedBallResult.Out;

    /// <summary>
    /// 内野ゴロ→一塁送球のエラー判定（Issue #37, design-14 P1-6b）。捕球ロール(Catching)→送球ロール(ThrowAccuracy)の
    /// 2段階。送球ロールは <see cref="FieldingCoefficients.ThrowErrorBaseProb"/> &gt; 0 のときのみ引く＝既定0では
    /// 捕球ロールのみ（現行と乱数消費順・結果とも完全一致, 決定論不変）。extraProb はイレギュラーバウンドを
    /// 捕球ロールへ加算する（Issue #63・既定0で恒等）。
    /// </summary>
    private static (BattedBallResult Result, FieldingErrorKind? ErrorKind) MaybeInfieldError(
        IReadOnlyList<Fielder> fielders, Vector3D landing, Fielder infielder,
        FieldingCoefficients coeff, IRandomSource rng, double extraProb = 0.0, int? throwAccuracy = null)
    {
        if (CatchErrorOccurs(fielders, landing, coeff, rng, extraProb))
        {
            // 捕球エラーの内訳（issue #632）: 一定割合は股・脇を抜けて外野へ行く＝トンネル。
            // 失策の総数は変えない（お手玉が減ってトンネルが増える）＝#123/#532 の失策率校正を保つ。
            // TunnelShareProb=0 のときはロール自体を引かない＝乱数消費順・結果とも完全に従来どおり
            // （ThrowErrorBaseProb と同じ恒等の作法）。
            if (coeff.TunnelShareProb > 0.0 && MathUtil.Chance(coeff.TunnelShareProb, rng))
                return (BattedBallResult.Error, FieldingErrorKind.Tunnel);
            return (BattedBallResult.Error, FieldingErrorKind.Catch);
        }
        if (coeff.ThrowErrorBaseProb > 0.0)
        {
            // 送球精度が高いほど悪送球が減る。クランプは捕球エラーと共通の min/max を流用。
            // throwAccuracy は前進捕球の低下を織り込んだ実効精度（issue #630。null＝素の精度）。
            var accuracy = throwAccuracy ?? infielder.Attributes.ThrowAccuracy;
            var errProb = coeff.ThrowErrorBaseProb - (accuracy - 50) * coeff.ThrowErrorAccuracySlope;
            if (MathUtil.Chance(MathUtil.Clamp(errProb, coeff.ErrorMinProb, coeff.ErrorMaxProb), rng))
            {
                return (BattedBallResult.Error, FieldingErrorKind.Throw);
            }
        }
        return (BattedBallResult.Out, null);
    }

    private static double ReachTime(Fielder f, Vector3D target)
    {
        var dist = (target - f.Location).Length;
        return dist / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
    }

    private static (Fielder Fielder, double Time) NearestReach(IReadOnlyList<Fielder> fielders, Vector3D target)
    {
        Fielder best = fielders[0];
        var bestT = double.MaxValue;
        foreach (var f in fielders)
        {
            if (f.Position is FieldPosition.Catcher) continue;
            var t = ReachTime(f, target);
            if (t < bestT) { bestT = t; best = f; }
        }
        return (best, bestT);
    }

    private static Fielder NearestFielder(IReadOnlyList<Fielder> fielders, Vector3D target)
    {
        Fielder best = fielders[0];
        var bestD = double.MaxValue;
        foreach (var f in fielders)
        {
            var d = (target - f.Location).LengthSquared;
            if (d < bestD) { bestD = d; best = f; }
        }
        return best;
    }

    private static Fielder NearestInfielder(IReadOnlyList<Fielder> fielders, Vector3D target)
    {
        Fielder? best = null;
        var bestD = double.MaxValue;
        foreach (var f in fielders)
        {
            if (f.Position is not (FieldPosition.FirstBase or FieldPosition.SecondBase
                or FieldPosition.ThirdBase or FieldPosition.Shortstop or FieldPosition.Pitcher))
            {
                continue;
            }
            var d = (target - f.Location).LengthSquared;
            if (d < bestD) { bestD = d; best = f; }
        }
        return best ?? fielders[0];
    }
}
