using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 走者1人の移動記録（タイムライン用, CHANGELOG 32）。FromBase: 0=打席, 1〜3=各塁。ToBase: 4=本塁生還。
/// </summary>
public sealed record RunnerMove(Player Runner, int FromBase, int ToBase, bool Out)
{
    /// <summary>
    /// 判定オーバーレイ（Issue #59）: 守備との時間の勝負で決着した際、margin絶対値が
    /// <see cref="BaserunningCoefficients.CloseCallMarginSeconds"/> 未満（際どい）だったか。
    /// 判定を伴わない進塁（無風の進塁等）は既定 false。表示専用・結果不変。
    /// </summary>
    public bool CloseCall { get; init; }

    /// <summary>
    /// 判定がこの走者に課した追加スタート遅延[s]（打球を読んでから走り出す＝G1のゴロ本塁レース）。
    /// 表示側は <c>RunnerClock</c> へこれを渡すことで判定とまったく同じ到達時刻になる（Issue #316）。
    /// 既定0＝追加遅延なし（ヒットでの進塁・フォース進塁・進塁打）。
    /// 時間の勝負で憤死した走者には、判定が負わせた出遅れ（<see cref="HomePlayResolver.RaceVerdict.RunnerDelaySeconds"/>）も
    /// ここに足し込む（issue #801）＝走者が送球より先に着く絵でアウトにならない。
    /// </summary>
    public double ExtraStartDelaySeconds { get; init; }

    /// <summary>
    /// この走者へのプレーで投げた送球の逸れ（issue #859）。判定（遅れ・悪送球）に使った値そのもの＝絵は受け手を
    /// この逸れへ寄せて描く。勝負を抽選しない送球・送球の無い移動は null（絵の側が別に決める）。
    /// </summary>
    public Fielding.ThrowScatterModel.Scatter? ThrowScatter { get; init; }

    /// <summary>
    /// この封殺のあと一塁へ転送した送球（併殺のピボット・本封→一封・自己封殺→一塁）の逸れ（issue #859）。
    /// 判定に使った値そのもの。転送しない／勝負を抽選しない経路は null。
    /// </summary>
    public Fielding.ThrowScatterModel.Scatter? RelayThrowScatter { get; init; }

    /// <summary>
    /// タイムライン表示専用: タッチアップ（フライ捕球を待ってから走る）の走者なら、その**捕球時刻**[s]
    /// （接触基準＝<see cref="HomePlaySituation.BallFieldedAtSeconds"/>）。null＝タッチアップでない。
    ///
    /// <para>判定側（<see cref="HomePlayResolver.TagUpRunnerTimeSeconds"/>）は捕球起点・二次リードなしで
    /// 走者の時計を回すのに、表示側は塁上の走者を一律「接触＋反応遅れ」で走らせていた（issue
    /// score-display-before-home-touch）。結果、犠飛の走者は<b>打った瞬間に三塁を離れ</b>（実測 208/208 件・
    /// 中央値 4.04 秒早い）、得点も本塁到達＝捕球のころに入っていた。ここに捕球時刻を載せて
    /// <see cref="HomePlayResolver.TagUpContextOf"/> と組で使うと、レッグが判定と同じ時計になる。</para>
    ///
    /// <para>乱数不使用・判定不変の観測値の写し（<c>ExtraStartDelaySeconds</c> と同じ扱い）。</para>
    /// </summary>
    public double? TagUpCatchAtSeconds { get; init; }

    /// <summary>
    /// タイムライン表示専用（issue #489）: この移動の到達がフォース（塁を踏むだけでアウト。
    /// タッチの芝居を出さない）か、タッチプレー（野手が走者へタッチしにいく）かの区別。
    /// 判定・帯には無関係＝乱数を消費せず、確定済みの Out と組み合わせて表示側が演技を選ぶだけ。
    /// 既定 false（タッチ）。フォースは「打者走者の一塁」「二塁封殺（併殺・野選）」
    /// 「満塁ゴロの本塁封殺」の3種のみ true にする（design-18 §4.5, issue #489）。
    /// </summary>
    public bool Force { get; init; }

    /// <summary>
    /// タイムライン表示専用（Q31）: この Out が併殺の「代替一段目」（三塁手/捕手/投手が自打球を自分で
    /// ベースへ運んで封殺＝送球なし・中継〈ピボット〉なし）か。既存の「内野前進時の単独本塁封殺」
    /// （<see cref="ApplyInPlayOut"/> の r3Forced×InfieldDepth.In 分岐）も同じ
    /// FromBase=3/ToBase=4/Out=true/Force=true の形を作るため、後続（一塁への転送送球）が伴う併殺の絵か、
    /// 単発の本塁封殺の絵かを <c>Force</c> フラグだけでは区別できない。乱数不使用・判定不変。
    /// </summary>
    public bool SelfForceDoublePlayLead { get; init; }

    /// <summary>
    /// タイムライン表示専用（issue 3-6-tag-double-play）: この move は「一塁手が一塁を自己フォースしてから
    /// 二塁へ送球してタッチ」という併殺の代替（3-6, タッチ併殺）か。<c>FromBase=1/ToBase=2</c> の move に載る。
    /// 二塁はフォースでなくタッチプレーなので <see cref="Force"/> は常に false（標準の二塁封殺ルートとの
    /// 見た目の区別はこのフラグで行う）。乱数不使用・判定不変。
    /// </summary>
    public bool FirstBaseSelfForceTag { get; init; }

    /// <summary>
    /// この封殺アウトは<b>先の走者を封殺するだけ</b>で、一塁へは転送しない（issue #797）。併殺の見込みが薄いが
    /// 封殺は十分間に合う場面（<see cref="BaserunningCoefficients.DoublePlayForceOnlyMaxRunnerSuccess"/>）。
    /// 打者は一塁に残る（打数1・安打0。野選には数えない）。絵は一塁への転送を描かない。乱数不使用。
    /// </summary>
    public bool ForceOnly { get; init; }

    /// <summary>
    /// タイムライン表示専用（issue #741）: 満塁・内野前進の本塁封殺（送球で三塁走者を本塁でアウト）のあと、
    /// 捕手が一塁へ転送したか。打者が一塁でアウト（本封→一封の併殺）か野選で生きたかは呼び出し側の
    /// batterSafeOnFc で分かる。乱数不使用・判定不変。
    /// </summary>
    public bool HomeForceRelay { get; init; }

    /// <summary>
    /// タイムライン表示専用（issue fly-out-runner-dp-table-and-doubled-off-depiction）: 飛び出し（ライナー併殺の
    /// 物理レース <see cref="DoubledOffResolver"/>）を走った一塁走者なら、守備の戻し送球が一塁に届く時刻[s]
    /// （接触基準）。<c>FromBase == ToBase == 1</c> の move（憤死=Out／帰塁セーフ=!Out）にだけ載る。
    /// null＝飛び出しの勝負ではない。判定・帯には無関係（判定側で計算済みの値の写し）。
    /// </summary>
    public double? DoubledOffThrowArriveSeconds { get; init; }

    /// <summary>同上・捕球時点で走者が塁から離れていた距離[m]（判定の戻り距離＝絵の折り返し後の戻り）。</summary>
    public double DoubledOffLeadM { get; init; }

    /// <summary>同上・飛び出しの最大リード[m]（コミット上限まで走った点。長い滞空ではここから捕球まで塁の方へ戻る）。</summary>
    public double DoubledOffMaxLeadM { get; init; }

    /// <summary>
    /// タイムライン表示専用（issue #1130）: フライを読んで待った走者の、待った位置（元の塁からの距離[m]。0＝塁に付いた）。
    /// null＝待っていない（打った瞬間に走った・フライでない）。安打では <see cref="FlyResumeAtSeconds"/> まで止まってから走る。
    /// 捕られた打球では <c>FromBase == ToBase</c> の move に載り、ここから元の塁へ戻る（勝負なら <see cref="DoubledOffThrowArriveSeconds"/> も載る）。
    /// 判定に使った値の写し（乱数不使用）。
    /// </summary>
    public double? FlyHoldLeadM { get; init; }

    /// <summary>同上・待った走者が、打球が落ちたのを見て走り出した時刻[s]（接触基準）。安打のときだけ。
    /// 判定の追加スタート遅延（<see cref="ExtraStartDelaySeconds"/>）はこの時刻から残りを走る秒に揃えてある。</summary>
    public double? FlyResumeAtSeconds { get; init; }
}

/// <summary>
/// 打席結果に応じて走者を進め、得点と追加アウト（併殺）を返す（設計書02 §4.1 の進塁を確率化）。
/// </summary>
public static class BaserunningModel
{
    /// <summary>進塁を適用。currentOuts はこの打席前のアウト数。戻り値=(得点, 追加アウト)。</summary>
    public static (int Runs, int ExtraOuts) Apply(
        BaseState bases, PlateAppearanceResult result, Player batter,
        int currentOuts, BaserunningCoefficients coeff, IRandomSource rng, HomePlayContext? home = null)
    {
        var (runs, extraOuts, _, _, _, _) = ApplyDetailed(bases, result, batter, currentOuts, coeff, rng, collectMoves: false, home);
        return (runs, extraOuts);
        // 注: ApplyDetailed の第3要素は (Home, Third) の憤死内訳（本塁クロスプレー/三塁到達レース）。
    }

    /// <summary>この打席で打者自身がアウトになるか。野選（FC）成立時は先行走者のみアウトで打者は生存（design-14 P1-1）。
    /// 振り逃げ（design-14 P1-2）成立時は三振でも打者は生存。</summary>
    public static bool IsBatterOut(PlateAppearanceResult result, bool batterSafeOnFc, bool droppedThirdStrikeReached = false)
        => (result is PlateAppearanceResult.Strikeout && !droppedThirdStrikeReached)
           || (result is PlateAppearanceResult.InPlayOut && !batterSafeOnFc);

    /// <summary>
    /// この着弾コースが「捕手が捕り損ねうる球」か（issue #525）。暴投・捕逸・振り逃げの抽選対象を
    /// 種別（Ball/CalledStrike/…）でなく実着弾コースで決める唯一の判定式（判定は1箇所に集約する・監督判断 2026-08-07）。
    /// 低い球（<see cref="BaserunningCoefficients.BatteryMissLowY"/> 未満＝ワンバウンド相当）または大きく外れた球
    /// （<see cref="Field.StrikeZone.DistanceOutsideM"/> が <see cref="BaserunningCoefficients.BatteryMissOutsideM"/> 以上）
    /// のみ対象。低い球の閾値は <see cref="Field.StrikeZone.BottomM"/>（=0.50）より下に置いてあるため、
    /// ストライクゾーンに来た球（捕れる球）は構造的に対象外になる＝ゾーン内の球で暴投・捕逸・振り逃げは起きない。
    /// </summary>
    public static bool CanBatteryMiss(
        double locationX, double locationY, Field.StrikeZone zone, BaserunningCoefficients coeff)
        => locationY < coeff.BatteryMissLowY
           || zone.DistanceOutsideM(locationX, locationY) >= coeff.BatteryMissOutsideM;

    /// <summary>暴投・パスボール（design-14 P2-8）: 全走者が1つ進塁し、三塁走者は生還する。戻り値=得点。</summary>
    public static int ApplyBatteryMiss(BaseState bases)
    {
        var runs = 0;
        if (bases.Third is not null) { runs++; bases.Third = null; }
        if (bases.Second is not null) { bases.Third = bases.Second; bases.Second = null; }
        if (bases.First is not null) { bases.Second = bases.First; bases.First = null; }
        return runs;
    }

    /// <summary>
    /// 進塁の詳細版（走者の動きを記録, タイムライン用）。判定・乱数消費順は Apply と同一。
    /// BaseOuts=(Home, Third) 塁上での憤死内訳。Home=本塁クロスプレー（F2/G1）、Third=単打の一塁→三塁
    /// レース（Issue #89）で刺された走者数（いずれも ExtraOuts の内数。統計参考値）。
    /// BatterSafeOnFc=野選（FC, design-14 P1-1）成立で打者が一塁生存したか。
    /// ErrorExtraAdvanceOccurred=失策連鎖（design-14 P1-6）が成立したか（統計参考値）。
    /// plainOut=打球の無い凡打（球数打ち切り）。走者を動かさず打者だけアウトにする。
    /// </summary>
    public static (int Runs, int ExtraOuts, (int Home, int Third) BaseOuts, bool BatterSafeOnFc,
        bool ErrorExtraAdvanceOccurred, IReadOnlyList<RunnerMove> Moves) ApplyDetailed(
        BaseState bases, PlateAppearanceResult result, Player batter,
        int currentOuts, BaserunningCoefficients coeff, IRandomSource rng, bool collectMoves = true,
        HomePlayContext? home = null, StartType r1Start = StartType.Normal, int errorThrowAccuracy = 50,
        Fielding.FieldingErrorKind? errorKind = null, bool r1StealBreak = false,
        bool r2StealBreak = false, bool r3StealBreak = false, bool plainOut = false)
    {
        var mv = collectMoves ? new List<RunnerMove>() : null;
        // 打球の無い凡打（規定球数での打ち切り・issue #1241）: 走者は動かず打者だけがアウト。旧テーブルの併殺・犠飛・野選を引かない（乱数も引かない）。
        if (plainOut && result == PlateAppearanceResult.InPlayOut) return (0, 0, (0, 0), false, false, Moves(mv));
        var r1 = bases.First;
        var r2 = bases.Second;
        var r3 = bases.Third;
        // フライの読み（issue #1130）: 2アウト未満のフライで、塁上の走者が打った瞬間に走るか待つかを決める。
        // 読みの乱数は走塁の解決より先に、一塁→二塁→三塁の順で引く（際どい打球だけ・不変条件#2）。
        var flyReads = ReadFlyRunners(result, r1, r2, r3, currentOuts, home, coeff, rng,
            r1Start, r1StealBreak, r2StealBreak, r3StealBreak);

        switch (result)
        {
            case PlateAppearanceResult.Strikeout:
                return (0, 0, (0, 0), false, false, Moves(mv));

            case PlateAppearanceResult.Walk:
            case PlateAppearanceResult.HitByPitch: // 死球はボールデッド＝四球と同じフォース進塁のみ
            {
                var (runs, outs) = ApplyWalk(bases, batter, r1, r2, r3, mv);
                return (runs, outs, (0, 0), false, false, Moves(mv));
            }

            case PlateAppearanceResult.Single:
            {
                var (runs, outs, homeOuts, thirdOuts, forcedAtSecond) = ApplyRunnerAdvance(bases, batter, r1, r2, r3, currentOuts, coeff, rng, isDouble: false, mv, home, flyReads);
                // 外野ゴロ（issue #1130）: 一塁走者が二塁で封殺された＝打者に安打は付かない（野選）。BatterSafeOnFc で伝える。
                return (runs, outs, (homeOuts, thirdOuts), forcedAtSecond, false, Moves(mv)); // 安打の追加アウト=本塁憤死＋三塁憤死（＋外野ゴロの封殺）
            }

            case PlateAppearanceResult.ReachedOnError:
            {
                var (runs, outs, homeOuts, thirdOuts, _) = ApplyRunnerAdvance(bases, batter, r1, r2, r3, currentOuts, coeff, rng, isDouble: false, mv, home);
                // 失策の連鎖（design-14 P1-6）: 悪送球で全走者＋打者が追加1個進む。既定オフ(ErrorExtraAdvanceProb=0)
                // では MathUtil.Chance 自体を呼ばずrng消費ゼロ＝Single と乱数消費順・結果とも完全一致。
                var errorExtraAdvanceOccurred = false;
                if (coeff.ErrorExtraAdvanceProb > 0.0)
                {
                    var (extraRuns, occurred) = ApplyErrorExtraAdvance(bases, batter, currentOuts, outs, coeff, rng, mv, errorThrowAccuracy, errorKind);
                    runs += extraRuns;
                    errorExtraAdvanceOccurred = occurred;
                }
                return (runs, outs, (homeOuts, thirdOuts), false, errorExtraAdvanceOccurred, Moves(mv));
            }

            case PlateAppearanceResult.Double:
            {
                var (runs, outs, homeOuts, thirdOuts, _) = ApplyRunnerAdvance(bases, batter, r1, r2, r3, currentOuts, coeff, rng, isDouble: true, mv, home, flyReads);
                return (runs, outs, (homeOuts, thirdOuts), false, false, Moves(mv));
            }

            case PlateAppearanceResult.Triple:
            {
                var runs = bases.RunnerCount;
                if (r3 is not null) mv?.Add(new RunnerMove(r3, 3, 4, false));
                if (r2 is not null) mv?.Add(new RunnerMove(r2, 2, 4, false));
                if (r1 is not null) mv?.Add(new RunnerMove(r1, 1, 4, false));
                mv?.Add(new RunnerMove(batter, 0, 3, false));
                bases.Clear();
                bases.Third = batter;
                return (runs, 0, (0, 0), false, false, Moves(mv));
            }

            case PlateAppearanceResult.HomeRun:
            {
                var runs = bases.RunnerCount + 1;
                if (r3 is not null) mv?.Add(new RunnerMove(r3, 3, 4, false));
                if (r2 is not null) mv?.Add(new RunnerMove(r2, 2, 4, false));
                if (r1 is not null) mv?.Add(new RunnerMove(r1, 1, 4, false));
                mv?.Add(new RunnerMove(batter, 0, 4, false));
                bases.Clear();
                return (runs, 0, (0, 0), false, false, Moves(mv));
            }

            case PlateAppearanceResult.InPlayOut:
            {
                var (runs, outs, homeOuts, batterSafeOnFc) = ApplyInPlayOut(bases, batter, r1, r2, r3, currentOuts, coeff, rng, mv, home, r1Start, r1StealBreak, r2StealBreak, r3StealBreak, flyReads);
                return (runs, outs, (homeOuts, 0), batterSafeOnFc, false, Moves(mv)); // 凡打の塁上憤死は本塁のみ
            }

            default:
                return (0, 0, (0, 0), false, false, Moves(mv));
        }
    }

    /// <summary>走者ごとのフライの読み（issue #1130）。null の塁＝読まない（走者なし・エンドランや盗塁で既に走っていた）。</summary>
    private sealed record FlyReads(FlyRunnerRead? R1, FlyRunnerRead? R2, FlyRunnerRead? R3, FlyReadSituation Situation, double ResumeAtSeconds)
    {
        public FlyRunnerRead? At(int baseNo) => baseNo switch { 1 => R1, 2 => R2, 3 => R3, _ => null };

        /// <summary>その塁の走者が待った（ハーフウェー・塁に付いた）か。</summary>
        public bool Waited(int baseNo) => At(baseNo) is { Start: not FlyRunnerStart.Go };

        /// <summary>安打で待った走者の追加スタート遅延[s]（走らなかった・読んでいない走者は 0）。</summary>
        public double DelayFor(int baseNo, Player runner, BaserunningCoefficients c)
            => At(baseNo) is { Start: not FlyRunnerStart.Go } rd
                ? FlyReadModel.HoldExtraDelaySeconds(runner, rd.HoldLeadM, ResumeAtSeconds, c)
                : 0.0;

        /// <summary>安打で待った走者の move に、絵の材料（待った位置・走り出した時刻）を載せる。</summary>
        public RunnerMove Mark(RunnerMove m)
            => At(m.FromBase) is { Start: not FlyRunnerStart.Go } rd
                ? m with { FlyHoldLeadM = rd.HoldLeadM, FlyResumeAtSeconds = ResumeAtSeconds }
                : m;
    }

    /// <summary>
    /// フライの読み（issue #1130・設計書12 §4.10）。2アウト未満のフライ（安打・凡打）で、塁上の走者ごとに
    /// 打った瞬間に走るか（<see cref="FlyRunnerStart.Go"/>）待つかを決める。待つなら、三塁走者・浅いフライ・
    /// タッチアップで進めそうな深いフライは塁に付き（Hold）、それ以外はハーフウェー。
    /// 乱数は際どい打球の読みだけ（一塁→二塁→三塁の順）。2アウト・フライでない・読みの材料が無いときは null＝従来どおり。
    /// </summary>
    private static FlyReads? ReadFlyRunners(
        PlateAppearanceResult result, Player? r1, Player? r2, Player? r3, int currentOuts, HomePlayContext? home,
        BaserunningCoefficients coeff, IRandomSource rng,
        StartType r1Start, bool r1StealBreak, bool r2StealBreak, bool r3StealBreak)
    {
        if (home is not { FlyRead: { } fr } h) return null;
        if (result is not (PlateAppearanceResult.Single or PlateAppearanceResult.Double or PlateAppearanceResult.InPlayOut)) return null;
        if (currentOuts >= 2) return null; // 2アウトは打った瞬間に走る＝従来どおり（乱数も引かない）
        if (r1 is null && r2 is null && r3 is null) return null;

        var range = Math.Sqrt(fr.LandingPoint.X * fr.LandingPoint.X + fr.LandingPoint.Z * fr.LandingPoint.Z);
        // タッチアップの見込みは落下点（捕球点）から測る（安打の Situation は転がった後の回収点なので使わない）。
        var landingSituation = h.Situation with { BallFieldedPoint = fr.LandingPoint, BallFieldedAtSeconds = fr.LandAtSeconds };

        FlyRunnerRead? Read(Player? runner, int baseNo, bool committed, bool nextBaseFree)
        {
            if (runner is null || committed) return null;
            // 内野の打球（ポップ・内野のライナー）は塁に付いて動かない（「ライナーは止まれ」）＝読みの乱数を引かない。
            if (range < coeff.FlyHalfwayMinRangeM) return new FlyRunnerRead(FlyRunnerStart.Hold, 0.0);
            if (FlyReadModel.ReadGo(fr.CatchMarginSeconds, runner, currentOuts, coeff, rng))
                return new FlyRunnerRead(FlyRunnerStart.Go, 0.0);
            if (baseNo == 3)
                return new FlyRunnerRead(FlyRunnerStart.Hold, 0.0);
            if (nextBaseFree)
            {
                var tagUp = baseNo == 2
                    ? HomePlayResolver.TagUpThirdParams(h.Field, coeff)
                    : HomePlayResolver.TagUpSecondParams(h.Field, coeff);
                var p = HomePlayResolver.TagUpSuccessProbability(runner, baseNo, landingSituation, h.Field, coeff, tagUp);
                if (HomeSendDecision.ShouldSendTagUp(p, h.Aggression, h.Tactics))
                    return new FlyRunnerRead(FlyRunnerStart.Hold, 0.0); // 深いフライ＝タッチアップを狙って塁に付く
            }
            // ハーフウェーの位置＝捕られても確実に戻れる距離（戻し送球の秒−反転−余裕）×脚力。上限 FlyHalfwayLeadM。
            var basePoint = baseNo == 1 ? h.Field.FirstBase : h.Field.SecondBase;
            var throwBack = DoubledOffResolver.DefenseReturnSeconds(
                (basePoint - fr.LandingPoint).Length, h.Situation.OutfielderThrowSpeedMps, coeff, h.Situation.OutfielderFielding);
            var safeLead = (throwBack - coeff.DoubledOffReverseSeconds - coeff.FlyHalfwaySafetySeconds)
                           * runner.ToFielder(coeff.AbilityEffect).SprintSpeedMps;
            // 二次リードより先へ出られない（戻し送球が短い）なら、ハーフウェーは取らず塁に付く。
            if (safeLead <= HomePlayResolver.RunnerContextOf(runner, coeff).LeadDistanceM)
                return new FlyRunnerRead(FlyRunnerStart.Hold, 0.0);
            return new FlyRunnerRead(FlyRunnerStart.Halfway, Math.Min(safeLead, coeff.FlyHalfwayLeadM));
        }

        var read1 = Read(r1, 1, r1Start is StartType.Contact or StartType.Gamble || r1StealBreak, r2 is null);
        var read2 = Read(r2, 2, r2StealBreak, r3 is null);
        var read3 = Read(r3, 3, r3StealBreak, true);
        return new FlyReads(read1, read2, read3, fr, FlyReadModel.ResumeAtSeconds(fr, coeff));
    }

    private static IReadOnlyList<RunnerMove> Moves(List<RunnerMove>? mv)
        => mv ?? (IReadOnlyList<RunnerMove>)System.Array.Empty<RunnerMove>();

    // 進塁判断の成功確率。スタート・回すか自重かの的確さは「走塁判断(Baserunning)」で決まる（設計書02 §4.1）。
    // ※スプリント自体は走力だが、進塁の可否判断はこのパラメータ。
    private static double P(double baseProb, int baserunning, BaserunningCoefficients coeff)
        => MathUtil.Clamp(baseProb + coeff.SpeedSlope * (baserunning - 50), 0.02, 0.98);

    /// <summary>
    /// 一塁を与えられたときの押し出し（四球・死球・振り逃げ生還）。打者を一塁へ置き、詰まっている走者だけを
    /// 押し出す（満塁なら三塁走者が生還）。返り値は (得点, アウト)。乱数は引かない。
    ///
    /// 振り逃げ（issue #525）から呼ぶために公開した。2アウトの振り逃げは**一塁が埋まっていても成立する**ので、
    /// <c>bases.First = batter</c> と直に書くと一塁走者が消える（実測: 走者保存則テストが400試合で4件検出。
    /// 旧 drop_third_strike_reach_prob=0.001 では滅多に踏まなかった経路が、着弾コースゲート導入に伴う
    /// 再校正 0.39 で表に出た）。「一塁を与える」規則は1箇所に集約する。
    /// </summary>
    public static (int Runs, int Outs) AwardFirstBase(BaseState bases, Player batter, List<RunnerMove>? mv = null)
        => ApplyWalk(bases, batter, bases.First, bases.Second, bases.Third, mv);

    /// <summary>
    /// バント決着の塁の付け替え（押し出し込み）。打者が一塁を目指すので、一塁走者は常に・二塁走者は一塁走者あり・
    /// 三塁走者は満塁のとき押し出される（打者アウトの犠打でも、アウトが取られるまでは同じ）。封殺された走者は
    /// 塁から外し、残りの押し出しは進める。押し出されない二塁走者は三塁が空なら1つ進む（送りバント）。
    /// 三塁走者が押し出されないとき（一・三塁など）は動かない。乱数は引かない。
    /// <para>返り値 Scorer＝押し出しで本塁へ向かった三塁走者（塁からは外す。得点にするかは呼び出し側が
    /// アウト数で決める＝3アウト目が封殺・打者アウトなら得点は認められない）。Advanced＝打者以外の誰かが
    /// 進んだか（誰も進まない打者アウトは犠打ではない）。</para>
    /// スクイズは三塁走者を別途裁定済みで <c>bases.Third = null</c> にしてから呼ぶ（押し出しの連鎖は一・二塁だけ見る）。
    /// </summary>
    public static (Player? Scorer, bool Advanced) ApplyBunt(BaseState bases, Player batter, BuntResult outcome)
    {
        if (outcome == BuntResult.PopOut) return (null, false);
        if (outcome == BuntResult.PopOutDoubledOff)
        {
            bases.First = null;
            return (null, false);
        }

        Player? r1 = bases.First, r2 = bases.Second, r3 = bases.Third;
        var forced2 = r1 is not null;
        var forced3 = forced2 && r2 is not null;
        var forcedHome = forced3 && r3 is not null;
        var batterReaches = outcome != BuntResult.SacrificeSuccess;

        Player? scorer = null;
        Player? n1 = null, n2 = null, n3 = null;
        if (r3 is not null && outcome != BuntResult.ForceOutAtHome)
        {
            if (forcedHome) scorer = r3;
            else n3 = r3;
        }
        if (r2 is not null && outcome != BuntResult.ForceOutAtThird)
        {
            if (forced3 || n3 is null) n3 = r2;
            else n2 = r2;
        }
        if (r1 is not null && outcome != BuntResult.ForceOutAtSecond) n2 = r1;
        if (batterReaches) n1 = batter;

        bases.First = n1;
        bases.Second = n2;
        bases.Third = n3;
        var advanced = scorer is not null
            || (r2 is not null && ReferenceEquals(n3, r2))
            || (r1 is not null && ReferenceEquals(n2, r1));
        return (scorer, advanced);
    }

    private static (int, int) ApplyWalk(BaseState bases, Player batter, Player? r1, Player? r2, Player? r3,
        List<RunnerMove>? mv)
    {
        var runs = 0;
        if (r1 is not null)
        {
            if (r2 is not null)
            {
                if (r3 is not null)
                {
                    runs++;         // 満塁押し出し
                    mv?.Add(new RunnerMove(r3, 3, 4, false));
                }
                bases.Third = r2;
                mv?.Add(new RunnerMove(r2, 2, 3, false));
            }
            bases.Second = r1;
            mv?.Add(new RunnerMove(r1, 1, 2, false));
        }
        bases.First = batter;
        mv?.Add(new RunnerMove(batter, 0, 1, false));
        return (runs, 0);
    }

    /// <summary>本塁突入の裁定結果（設計書12 §3, F2）。</summary>
    private enum HomeVerdict { Scored, Held, OutAtHome }

    private static (int Runs, int ExtraOuts, int HomeOuts, int ThirdOuts, bool ForcedAtSecond) ApplyRunnerAdvance(
        BaseState bases, Player batter, Player? r1, Player? r2, Player? r3, int currentOuts,
        BaserunningCoefficients coeff, IRandomSource rng, bool isDouble, List<RunnerMove>? mv, HomePlayContext? home,
        FlyReads? reads = null)
    {
        // フライの読み（issue #1130）: 待った走者は、落ちたのを見てから塁間の途中で走り出す＝追加スタート遅延。
        // 打った瞬間に走った走者・読んでいない走者は 0（従来と同じ）。
        var d1 = r1 is not null && reads is not null ? reads.DelayFor(1, r1, coeff) : 0.0;
        var d2 = r2 is not null && reads is not null ? reads.DelayFor(2, r2, coeff) : 0.0;
        var d3 = r3 is not null && reads is not null ? reads.DelayFor(3, r3, coeff) : 0.0;
        RunnerMove Mark(RunnerMove m) => reads is null ? m : reads.Mark(m);
        // 外野ゴロ（issue #1130）: 待った一塁走者が二塁で封殺された（打者は安打でなく野選）。
        var forcedAtSecond = false;
        bases.Clear();
        var runs = 0;
        var extraOuts = 0;
        var homeOuts = 0;   // 本塁クロスプレー憤死（統計参考値。extraOuts の内数）
        var thirdOuts = 0;  // 単打の一塁→三塁レース憤死（Issue #89。extraOuts の内数）
        // 1プレーで送球先は1箇所＝アウトは最大1（1球は2箇所へ投げられない）。本塁・三塁で共有する（#89）。
        var outThrowUsed = false;
        // 本塁へ投げてセーフだった（issue #1257）: 球は本塁へ行った＝他塁（三塁等）へはもう投げられない。
        // outThrowUsed と違い、後続の本塁突入は引き続き本塁の送球との勝負（無条件生還にはしない）。
        var homeThrowMade = false;
        // バックホーム悪送球（2026-08-30）: 本塁への送球が逸れ、アウトのはずの走者が生還したか。
        // 発生時は末尾で残る走者を1つずつ押し上げる（design-14 P1-6型の連鎖、全走者+1進塁）。
        var homeWildThrowOccurred = false;
        // ダブルタッチアウト（issue #984, OPEN-QUESTIONS Q27選択肢2）: 本塁で1人目を刺した
        // 「タッチ完了時刻」（接触＝0秒基準）。null＝まだ誰も本塁で刺していない
        // （outThrowUsed が三塁等で立った場合はここは null のまま＝後続は従来どおり無条件生還）。
        double? homeOutTagCompleteAtSeconds = null;

        // 打者の到達塁（単打/野選=1塁、二塁打=2塁）。先行走者から順にこの塁を明け渡す義務があるかを決める。
        var batterBase = isDouble ? 2 : 1;
        // フォース進塁（#87）: 打者の到達塁とその先の塁が連続して埋まっている走者は自重できない
        // （＝バックアップの塁が無い）。二塁打の一塁走者は打者が二塁に入るだけで一塁は空くため対象外。
        var r2Forced = batterBase == 2 ? r2 is not null : r1 is not null && r2 is not null;
        var r3Forced = r2Forced && r3 is not null;

        // 走者の行き先を塁ごとに保持（#87: newSecond/newThird の2変数上書きで走者が消える構造を廃止）。
        Player? third = null;
        Player? second = null;
        Player? first = null;

        // 捕球ミスの失策（拾い直せず確定, issue #903/#950）では、走者は打った瞬間には走れない（ゴロを読む）。
        // 2アウトだけは打った瞬間に走る＝遅れ0。三塁を狙う側（ResolveCatchErrorAdvanceToThird）と同じ値を使う。
        // それ以外の打球（安打の返球との勝負）は従来どおり遅れ0＝恒等。
        var catchErrorStartDelay = home is { UnrecoveredCatchError: true }
            ? CatchErrorStartDelaySeconds(currentOuts, coeff) : 0.0;

        // 本塁突入の裁定。home=null は従来の確率テーブル（決定論・乱数消費順を保存）。
        // tableUnconditional=true は「テーブル時は無条件生還」（三塁走者・二塁打の二塁走者）。
        // CloseCall（判定オーバーレイ, Issue #59）は物理レース（home!=null）で決着した場合のみ算出する
        // 純計算（rng非消費）で、乱数消費順・結果には一切影響しない。
        // Delay＝走者が引き受けた出遅れ[s]（issue #801。送球より先に着く絵にしない）。捕球ミスの読みの遅れ
        // （#950）は生還・憤死のどちらでも絵の走り出しに乗る。
        // sendProbCap＝送り判断に使う見込みの上限（#950: 捕球ミスの二塁走者は三塁の見込みを超えない）。
        (HomeVerdict Verdict, bool CloseCall, double Delay, Fielding.ThrowScatterModel.Scatter? Scatter) TryHome(Player runner, int fromBase, double tableProb, bool tableUnconditional, double sendProbCap = 1.0, double readDelay = 0.0)
        {
            if (currentOuts + extraOuts >= 3) return (HomeVerdict.Held, false, 0.0, null); // 既に3アウト＝以降は進めない
            if (home is not { } h)
                return (tableUnconditional || MathUtil.Chance(P(tableProb, coeff.AbilityEffect.Running(runner.Baserunning), coeff), rng)
                    ? HomeVerdict.Scored : HomeVerdict.Held, false, 0.0, null);

            // F2: 送り判定（勝てる時だけ送る）→ 時間の勝負（外野中継 vs 走者）。
            // 走り出しの遅れ＝捕球ミスの読み（#950）＋フライの読み（#1130。待った走者だけ正）。
            var startDelay = catchErrorStartDelay + readDelay;
            var prob = Math.Min(
                HomePlayResolver.SuccessProbability(runner, fromBase, h.Situation, h.Field, coeff, startDelay), sendProbCap);
            if (!HomeSendDecision.ShouldSend(prob, currentOuts + extraOuts, h.Aggression, h.Tactics))
                return (HomeVerdict.Held, false, 0.0, null); // 自重（RNG消費なし）
            if (homeOutTagCompleteAtSeconds is { } tagAt)
            {
                // ダブルタッチアウト（issue #984, Q27選択肢2）: 本塁で1人目を刺した捕手は、ボールを持ったまま
                // 本塁に居る＝2人目は新しい送球との勝負ではなく「1人目のタッチ完了＋切り替え秒」との勝負。
                // 送球は既に捕られている（悪送球・逸れは引かない）＝1回だけ引く。
                var dblMargin = HomePlayResolver.DoubleTagMargin(runner, fromBase, tagAt, h.Field, coeff, startDelay);
                var dblCloseCall = Math.Abs(dblMargin) < coeff.CloseCallMarginSeconds;
                var dbl = HomePlayResolver.ResolveDoubleTagAtHome(runner, fromBase, tagAt, h.Field, coeff, rng, startDelay);
                return dbl.Safe
                    ? (HomeVerdict.Scored, dblCloseCall, startDelay, null)
                    : (HomeVerdict.OutAtHome, dblCloseCall, startDelay + dbl.RunnerDelaySeconds, null);
            }
            var margin = HomePlayResolver.Margin(runner, fromBase, h.Situation, h.Field, coeff, startDelay);
            var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
            var (outcome, delay, scatter) = HomePlayResolver.ResolveWithWildThrowVerdict(
                runner, fromBase, h.Situation, h.Field, coeff, HomePlayResolver.HomeParams(coeff), rng, startDelay);
            // 送球を他の塁（三塁等）で使い切っていれば本塁には球が無い＝従来どおり無条件生還（乱数の消費順も従来どおり）。
            if (outcome == HomePlayResolver.HomeThrowOutcome.Safe || outThrowUsed)
            {
                if (!outThrowUsed) homeThrowMade = true;
                return (HomeVerdict.Scored, closeCall, startDelay, scatter);
            }
            outThrowUsed = true;
            if (outcome == HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow)
            {
                homeWildThrowOccurred = true;
                return (HomeVerdict.Scored, closeCall, startDelay, scatter);
            }
            // 本塁での憤死: 1人目のタッチが済む時刻＝送球到達＋タッチと、走者が実際に着く時刻（出遅れ込み）の遅い方。
            // 走者が送球より後に着くなら、捕手は走者が来るまでタッチできない（絵の判定もそのレッグ終端に立つ）。
            homeOutTagCompleteAtSeconds = Math.Max(
                HomePlayResolver.DefenseTimeSeconds(h.Situation, coeff),
                HomePlayResolver.RunnerTimeSeconds(runner, fromBase, 4, h.Field, coeff, startDelay + delay));
            return (HomeVerdict.OutAtHome, closeCall, startDelay + delay, scatter);
        }

        // 三塁走者（先行）: テーブル時は無条件生還。フォース時（満塁）は自重不可＝RNGを消費せず確定生還
        // （凡打側の r3Forced と同じ流儀。時間の勝負にはしていない＝ルール上の確定進塁）。
        if (r3 is not null)
        {
            if (r3Forced)
            {
                runs++;
                mv?.Add(Mark(new RunnerMove(r3, 3, 4, false) { ExtraStartDelaySeconds = d3 }));
            }
            else
            {
                var (verdict3, closeCall3, delay3, scatter3) = TryHome(r3, 3, 0.0, tableUnconditional: true, readDelay: d3);
                switch (verdict3)
                {
                    case HomeVerdict.Scored: runs++; mv?.Add(Mark(new RunnerMove(r3, 3, 4, false) { CloseCall = closeCall3, ExtraStartDelaySeconds = delay3, ThrowScatter = scatter3 })); break;
                    case HomeVerdict.OutAtHome: extraOuts++; homeOuts++; mv?.Add(Mark(new RunnerMove(r3, 3, 4, Out: true) { CloseCall = closeCall3, ExtraStartDelaySeconds = delay3, ThrowScatter = scatter3 })); break;
                    default: third = r3; break; // 自重＝三塁で止まる（移動なし）
                }
            }
        }

        // 二塁走者: 単打は SecondToHomeOnSingle、二塁打はテーブル時無条件。
        // フォース時（一塁走者あり、または二塁打で打者が二塁を占有）は自重不可＝三塁へ確定進塁。
        // r3Forced により三塁は必ず空いている（上のブロックで確定済み）。
        if (r2 is not null)
        {
            // 捕球ミスの非フォースの二塁走者（#950）: 本塁へ回す見込みは、同じ打席で三塁に間に合う見込みを超えない
            // （内野でこぼれた球は本塁にも近い。外野の返球で校正した本塁の係数をそのまま使うと、拾い直しが遅い
            // 打席で本塁の方が三塁より安全に見えてしまう）。判定・絵の秒は変えず、送り判断の見込みだけに効く。
            var homeCap2 = !r2Forced && home is { UnrecoveredCatchError: true } ceCap
                ? CatchErrorThirdProbability(r2, ceCap, currentOuts, coeff) : 1.0;
            var (verdict2, closeCall2, delay2, scatter2) = TryHome(r2, 2, coeff.SecondToHomeOnSingle, tableUnconditional: isDouble, homeCap2, readDelay: d2);
            switch (verdict2)
            {
                case HomeVerdict.Scored: runs++; mv?.Add(Mark(new RunnerMove(r2, 2, 4, false) { CloseCall = closeCall2, ExtraStartDelaySeconds = delay2, ThrowScatter = scatter2 })); break;
                case HomeVerdict.OutAtHome: extraOuts++; homeOuts++; mv?.Add(Mark(new RunnerMove(r2, 2, 4, Out: true) { CloseCall = closeCall2, ExtraStartDelaySeconds = delay2, ThrowScatter = scatter2 })); break;
                default:
                    if (!r2Forced && third is null && !outThrowUsed && !homeThrowMade && currentOuts + extraOuts < 3
                        && home is { UnrecoveredCatchError: true } ce)
                    {
                        // 捕球ミスの失策（拾い直せず確定, issue #903）: 内野でこぼれた球は近くにある＝三塁は
                        // 単打のように無条件では取れない。拾い直した野手との時間勝負で、狙う／自重するを決める。
                        ResolveCatchErrorAdvanceToThird(r2, ce, currentOuts, coeff, rng, mv,
                            ref extraOuts, ref thirdOuts, ref outThrowUsed, ref third, ref second);
                    }
                    else if (r2Forced || third is null) { third = r2; mv?.Add(Mark(new RunnerMove(r2, 2, 3, false) { ExtraStartDelaySeconds = d2 })); }
                    else second = r2; // 三塁が塞がっていれば（非フォース）二塁に留まる
                    break;
            }
        }

        // 一塁走者: 二塁打なら本塁を狙う(FirstToHomeOnDouble)、単打は三塁狙い（本塁は狙わない・従来テーブル）。
        if (r1 is not null)
        {
            if (isDouble)
            {
                // 二塁打では打者が二塁を占有する＝一塁走者は一塁にも二塁にも戻れない（#88）。
                var (verdict1, closeCall1, delay1, scatter1) = TryHome(r1, 1, coeff.FirstToHomeOnDouble, tableUnconditional: false, readDelay: d1);
                switch (verdict1)
                {
                    case HomeVerdict.Scored: runs++; mv?.Add(Mark(new RunnerMove(r1, 1, 4, false) { CloseCall = closeCall1, ExtraStartDelaySeconds = delay1, ThrowScatter = scatter1 })); break;
                    case HomeVerdict.OutAtHome: extraOuts++; homeOuts++; mv?.Add(Mark(new RunnerMove(r1, 1, 4, Out: true) { CloseCall = closeCall1, ExtraStartDelaySeconds = delay1, ThrowScatter = scatter1 })); break;
                    default:
                        if (third is null) { third = r1; mv?.Add(Mark(new RunnerMove(r1, 1, 3, false) { ExtraStartDelaySeconds = d1 })); }
                        else if (currentOuts + extraOuts < 3)
                        {
                            // 三塁に先行走者が自重中＝一塁走者は追い越せないため、先行走者を本塁へ押し出して
                            // 三塁を明け渡させる（フォースの延長。#88）。
                            var pushed = third;
                            runs++;
                            PushHeldRunnerHome(mv, pushed, fromBase: ReferenceEquals(pushed, r2) ? 2 : 3);
                            third = r1;
                            mv?.Add(Mark(new RunnerMove(r1, 1, 3, false) { ExtraStartDelaySeconds = d1 }));
                        }
                        else first = r1; // 既に3アウト＝以降の塁状況は結果に影響しない
                        break;
                }
            }
            else if (home is { } hf && d1 > 0.0 && !outThrowUsed && !homeThrowMade && currentOuts + extraOuts < 3
                     && ResolveOutfieldForceAtSecond(r1, hf, d1, coeff, rng, mv, Mark, ref extraOuts) is { } forceOut)
            {
                // 外野ゴロ（issue #1130）: 待った一塁走者を、落ちた打球を拾った外野手が二塁で狙った。
                // 送球は二塁へ行った＝三塁は狙わない（1プレー1送球）。封殺なら打者は野選で一塁。
                outThrowUsed = true;
                if (forceOut) forcedAtSecond = true;
                else second = r1;
            }
            else if (home is { } h3 && third is null)
            {
                // 単打の一塁→三塁を物理レース化（Issue #89）。打球の深さ・方向（外野処理点）と外野の肩から
                // 三塁送球所要を組み、走者(走力＋走塁判断)との秒勝負で解く。三塁が塞がっていれば二塁止まり。
                // 送り判定で自重した場合は本塁と同じ流儀で RNG を消費しない（決定論・消費順は home!=null 経路の慣習）。
                var third3 = HomePlayResolver.ThirdParams(h3.Field, coeff);
                // 待った一塁走者（#1130）は落ちてから走る＝三塁の見込みも勝負もその遅れ込み。
                // 拾い直せず確定した捕球ミス（#950）の読みの遅れも同じ1か所（二塁走者の三塁・本塁と同じ値）。
                var d1Third = d1 + catchErrorStartDelay;
                var prob = HomePlayResolver.SuccessProbability(r1, 1, h3.Situation, h3.Field, coeff, third3, d1Third);
                if (!HomeSendDecision.ShouldSendThird(prob, currentOuts + extraOuts, h3.Aggression, h3.Tactics))
                {
                    second = r1; // 自重＝二塁で止まる（RNG非消費）
                    mv?.Add(Mark(new RunnerMove(r1, 1, 2, false) { ExtraStartDelaySeconds = d1 }));
                }
                else if (outThrowUsed || homeThrowMade)
                {
                    third = r1; // 送球は既に他塁・本塁で使用済み＝三塁は無血進塁（RNG非消費）
                    mv?.Add(Mark(new RunnerMove(r1, 1, 3, false) { ExtraStartDelaySeconds = d1 }));
                }
                else
                {
                    var margin = HomePlayResolver.Margin(r1, 1, h3.Situation, h3.Field, coeff, third3, d1Third);
                    var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
                    var race3 = HomePlayResolver.ResolveRace(r1, 1, h3.Situation, h3.Field, coeff, third3, rng, d1Third);
                    if (race3.Safe)
                    {
                        third = r1;
                        mv?.Add(Mark(new RunnerMove(r1, 1, 3, false) { CloseCall = closeCall, ExtraStartDelaySeconds = d1Third, ThrowScatter = race3.Scatter }));
                    }
                    else
                    {
                        extraOuts++; thirdOuts++; outThrowUsed = true; // 三塁で刺殺（走者はアウト＝塁上に残らない）
                        mv?.Add(Mark(new RunnerMove(r1, 1, 3, Out: true)
                            { CloseCall = closeCall, ExtraStartDelaySeconds = d1Third + race3.RunnerDelaySeconds, ThrowScatter = race3.Scatter }));
                    }
                }
            }
            else if (home is not null)
            {
                // home!=null かつ三塁が先行走者で塞がっている＝一塁走者は追い越せず二塁止まり（送球なし＝RNG非消費）。
                second = r1;
                mv?.Add(Mark(new RunnerMove(r1, 1, 2, false) { ExtraStartDelaySeconds = d1 }));
            }
            else
            {
                // 従来テーブル（home==null: 単体テスト・純テーブル経路）。決定論・乱数消費順を保存する。
                // Chance() の評価順は変更前と同一に保つ（third の空き判定を先に短絡させない＝RNG消費順を保存）。
                if (MathUtil.Chance(P(coeff.FirstToThirdOnSingle, coeff.AbilityEffect.Phys(r1.Baserunning), coeff), rng) && third is null)
                {
                    third = r1;
                    mv?.Add(new RunnerMove(r1, 1, 3, false));
                }
                else
                {
                    second = r1;
                    mv?.Add(new RunnerMove(r1, 1, 2, false));
                }
            }
        }

        // バックホーム悪送球の連鎖（design-14 P1-6型）: この時点の third/second/first は「悪送球が
        // 無かった場合の」確定済みの行き先。まだ塁上に残っている走者だけをもう1つ押し上げる
        // （得点済み・アウトになった走者は対象外＝bases変数には現れない）。打者走者自身は対象外（次行で別途配置）。
        if (homeWildThrowOccurred && currentOuts + extraOuts < 3)
        {
            var (extraRuns, movedThird, movedSecond, movedFirst) = PushRunnersOneBase(third, second, first, mv);
            runs += extraRuns;
            third = movedThird; second = movedSecond; first = movedFirst;
        }

        // 各走者は互いに異なる塁へ割り当て済み（フォース済みの先行走者は必ず塁を明け渡す）ため、
        // 打者の占有と衝突しない。
        if (third is not null) bases.Third = third;
        if (second is not null) bases.Second = second;
        if (first is not null) bases.First = first;
        if (isDouble) bases.Second = batter; else bases.First = batter;
        mv?.Add(new RunnerMove(batter, 0, isDouble ? 2 : 1, false));

        return (runs, extraOuts, homeOuts, thirdOuts, forcedAtSecond);
    }

    /// <summary>
    /// 外野ゴロ（issue #1130）: ハーフウェーで待った一塁走者（<paramref name="delay"/>＞0）は落ちてから二塁へ走る。
    /// 打球を拾った外野手は、走者が間に合う見込みが <see cref="BaserunningCoefficients.OutfieldForceSecondMaxRunnerSuccess"/>
    /// 未満のときだけ二塁へ投げる（刺せると見たとき）。返り値: null＝投げない（三塁を狙う判断へ進む・乱数なし）／
    /// true＝二塁で封殺（走者アウト）／false＝二塁セーフ（送球は二塁へ行った）。
    /// </summary>
    private static bool? ResolveOutfieldForceAtSecond(
        Player r1, HomePlayContext h, double delay, BaserunningCoefficients coeff, IRandomSource rng,
        List<RunnerMove>? mv, Func<RunnerMove, RunnerMove> mark, ref int extraOuts)
    {
        var p = HomePlayResolver.OutfieldForceSecondParams(h.Field, coeff);
        var prob = HomePlayResolver.SuccessProbability(r1, 1, h.Situation, h.Field, coeff, p, delay);
        if (prob >= coeff.OutfieldForceSecondMaxRunnerSuccess) return null;
        var margin = HomePlayResolver.Margin(r1, 1, h.Situation, h.Field, coeff, p, delay);
        var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
        var race = HomePlayResolver.ResolveRace(r1, 1, h.Situation, h.Field, coeff, p, rng, delay);
        if (race.Safe)
        {
            mv?.Add(mark(new RunnerMove(r1, 1, 2, false) { CloseCall = closeCall, ExtraStartDelaySeconds = delay, ThrowScatter = race.Scatter }));
            return false;
        }
        extraOuts++;
        mv?.Add(mark(new RunnerMove(r1, 1, 2, Out: true)
            { CloseCall = closeCall, Force = true, ExtraStartDelaySeconds = delay + race.RunnerDelaySeconds, ThrowScatter = race.Scatter }));
        return true;
    }

    /// <summary>
    /// 捕球ミスの失策（拾い直せず確定）で塁上の走者が走り出すまでの遅れ[s]（issue #950・design-14 P1-6）。
    /// 打球が内野にある＝走者はゴロを読んでから走る（<see cref="BaserunningCoefficients.GrounderAdvanceStartDelaySeconds"/>）。
    /// <b>2アウトでは打った瞬間に走る＝0</b>（打者がアウトになれば攻撃が終わるので、読む理由が無い）。
    /// 三塁を狙う側も本塁を狙う側もこの1か所を引く。
    /// </summary>
    /// <param name="outsAtContact">打った時点のアウト数（この打席の前のアウト数）。</param>
    public static double CatchErrorStartDelaySeconds(int outsAtContact, BaserunningCoefficients coeff)
        => outsAtContact >= 2 ? 0.0 : coeff.GrounderAdvanceStartDelaySeconds;

    /// <summary>
    /// 捕球ミスの失策で、二塁走者が三塁に間に合う見込み（issue #903/#950）。三塁へ回す判断と、
    /// 本塁へ回す見込みの上限（本塁の見込み≦三塁の見込み）の両方がこの値を使う。乱数は引かない。
    /// </summary>
    public static double CatchErrorThirdProbability(
        Player r2, HomePlayContext h, int outsAtContact, BaserunningCoefficients coeff)
        => HomePlayResolver.SuccessProbability(r2, 2, h.Situation, h.Field, coeff,
            HomePlayResolver.AdvanceParams(h.Field, coeff), CatchErrorStartDelaySeconds(outsAtContact, coeff));

    /// <summary>
    /// 捕球ミスの失策で、二塁走者を本塁へ回す判断に使う見込み（issue #950）。本塁の見込み（読みの遅れ込み）を、
    /// 同じ打席の三塁の見込み（<see cref="CatchErrorThirdProbability"/>）で頭打ちにした値。乱数は引かない。
    /// </summary>
    public static double CatchErrorHomeSendProbability(
        Player r2, HomePlayContext h, int outsAtContact, BaserunningCoefficients coeff)
        => Math.Min(
            HomePlayResolver.SuccessProbability(r2, 2, h.Situation, h.Field, coeff, CatchErrorStartDelaySeconds(outsAtContact, coeff)),
            CatchErrorThirdProbability(r2, h, outsAtContact, coeff));

    /// <summary>
    /// 捕球ミスの失策（拾い直せず確定, issue #903・design-14 P1-6）で、非フォースの二塁走者が三塁を狙うかを
    /// 拾い直した野手との時間勝負で解く。<b>新しい走者の式は作らない</b>: 走者・守備の秒は
    /// <see cref="HomePlayResolver"/>（ゴロ進塁打と同じ <see cref="HomePlayResolver.AdvanceParams"/>＝送球先は三塁・
    /// 走者はゴロを読む遅れ <see cref="CatchErrorStartDelaySeconds"/> を引き受ける＝2アウトは0, #950）、
    /// 守備の時計は <see cref="HomePlayContext.Situation"/>（GameEngine が拾い直しを終えた時刻
    /// <c>CatchErrorReadyAtSeconds</c> を起点に組んだもの）。送り判断は単打の一塁→三塁と同じ
    /// <see cref="HomeSendDecision.ShouldSendThird"/>（三塁へ回すかの判断。2アウト緩和あり）。
    /// 自重は二塁に留まり乱数を消費しない（本塁・三塁レースの流儀, 不変条件#2）。
    /// 呼び出し前提: 非フォースの二塁走者・三塁は空き・送球は未使用・3アウト未満。
    /// </summary>
    private static void ResolveCatchErrorAdvanceToThird(
        Player r2, HomePlayContext h, int currentOuts, BaserunningCoefficients coeff, IRandomSource rng,
        List<RunnerMove>? mv, ref int extraOuts, ref int thirdOuts, ref bool outThrowUsed,
        ref Player? third, ref Player? second)
    {
        var advance = HomePlayResolver.AdvanceParams(h.Field, coeff);
        var delay = CatchErrorStartDelaySeconds(currentOuts, coeff);
        var prob = CatchErrorThirdProbability(r2, h, currentOuts, coeff);
        if (!HomeSendDecision.ShouldSendThird(prob, currentOuts + extraOuts, h.Aggression, h.Tactics))
        {
            second = r2; // 自重＝二塁に留まる（移動なし・RNG非消費）
            return;
        }
        var margin = HomePlayResolver.Margin(r2, 2, h.Situation, h.Field, coeff, advance, delay);
        var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
        var race = HomePlayResolver.ResolveRace(r2, 2, h.Situation, h.Field, coeff, advance, rng, delay);
        if (race.Safe)
        {
            third = r2;
            mv?.Add(new RunnerMove(r2, 2, 3, false)
                { CloseCall = closeCall, ExtraStartDelaySeconds = delay, ThrowScatter = race.Scatter });
        }
        else
        {
            extraOuts++; thirdOuts++; outThrowUsed = true; // 三塁で刺殺（走者はアウト＝塁上に残らない）
            mv?.Add(new RunnerMove(r2, 2, 3, Out: true)
                {
                    CloseCall = closeCall, ExtraStartDelaySeconds = delay + race.RunnerDelaySeconds,
                    ThrowScatter = race.Scatter,
                });
        }
    }

    /// <summary>三塁で自重していた先行走者を、後続走者に追い越されるため本塁へ押し出す（#88）。
    /// 既にその走者の三塁到達move（FromBase→3）が記録済みなら、それを直接本塁行きへ書き換えて単一の
    /// moveにする（2段階の見た目上の移動を避ける）。move未記録（三塁で静止していた走者）なら新規追加する。</summary>
    private static void PushHeldRunnerHome(List<RunnerMove>? mv, Player runner, int fromBase)
    {
        if (mv is null) return;
        for (var i = mv.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(mv[i].Runner, runner) && mv[i].ToBase == 3)
            {
                mv[i] = mv[i] with { ToBase = 4 };
                return;
            }
        }
        mv.Add(new RunnerMove(runner, fromBase, 4, false));
    }

    /// <summary>塁上の走者を1つずつ押し上げる（悪送球1本で全走者+1進塁、design-14 P1-6型の連鎖）。
    /// 三塁→本塁（得点）、二塁→三塁、一塁→二塁（二塁が空いていれば）の順。ReachedOnError の失策連鎖
    /// （<see cref="ApplyErrorExtraAdvance"/>）とバックホーム悪送球（<see cref="ApplyRunnerAdvance"/> 内
    /// TryHome）の共通ロジック。</summary>
    private static (int Runs, Player? Third, Player? Second, Player? First) PushRunnersOneBase(
        Player? third, Player? second, Player? first, List<RunnerMove>? mv)
    {
        var runs = 0;
        if (third is not null)
        {
            runs++;
            mv?.Add(new RunnerMove(third, 3, 4, false));
            third = null;
        }
        if (second is not null)
        {
            mv?.Add(new RunnerMove(second, 2, 3, false));
            third = second;
            second = null;
        }
        if (first is not null && second is null)
        {
            mv?.Add(new RunnerMove(first, 1, 2, false));
            second = first;
            first = null;
        }
        return (runs, third, second, first);
    }

    /// <summary><see cref="PushRunnersOneBase"/> の <see cref="BaseState"/> 版（バックホーム悪送球専用。
    /// <paramref name="bases"/> を直接書き換える）。ApplyInPlayOut の3塁走者本塁レース（フォース封殺・
    /// 内野前進・タッチアップ）で共通に使う。</summary>
    private static int PushRunnersOnWildThrow(BaseState bases, List<RunnerMove>? mv)
    {
        var (runs, third, second, first) = PushRunnersOneBase(bases.Third, bases.Second, bases.First, mv);
        bases.Third = third;
        bases.Second = second;
        bases.First = first;
        return runs;
    }

    /// <summary>失策の連鎖（design-14 P1-6）: 悪送球1本で塁上の走者全員＋打者走者が1つ多く進む、という単一事象として
    /// 一括モデル化する（個別確率ではない）。<paramref name="coeff"/>.ErrorExtraAdvanceProb &gt; 0 のときのみ呼ばれる。</summary>
    private static (int Runs, bool Occurred) ApplyErrorExtraAdvance(
        BaseState bases, Player batter, int currentOuts, int extraOuts,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv, int throwAccuracy,
        Fielding.FieldingErrorKind? errorKind)
    {
        if (currentOuts + extraOuts >= 3) return (0, false);
        // 送球精度連動（Issue #37）: 送球者の精度が低いほど連鎖しやすい。Ac=50 で base と恒等（傾き項=0）。
        var prob = MathUtil.Clamp(
            coeff.ErrorExtraAdvanceProb - (throwAccuracy - 50) * coeff.ErrorExtraAdvanceAccuracySlope, 0.0, 1.0);
        // 失策種別のゲート（issue #629）: 連鎖は design-14 P1-6 が定義するとおり**悪送球**の連鎖。
        // 捕球エラー（お手玉）とフライ落球では球は野手の足元にあり、全走者が1つ多く進む理由がない
        // （絵も「3m弾いて拾い直す」だけなので、走者だけ進むと因果が合わない）。
        // ⚠ 判定の位置は &&の第2項＝**Chance を引いてから**捨てる（issue #525 と同じ流儀）。
        // 乱数の消費位置と回数を従来と揃え、発生「率」だけを絞る。
        if (!MathUtil.Chance(prob, rng) || errorKind != Fielding.FieldingErrorKind.Throw) return (0, false);

        // first は ReachedOnError では常に打者走者。
        var (runs, third, second, first) = PushRunnersOneBase(bases.Third, bases.Second, bases.First, mv);
        bases.Third = third;
        bases.Second = second;
        bases.First = first;
        return (runs, true);
    }

    /// <param name="r1StealBreak">一塁走者が投球と同時に走っていた（盗塁企図が打球で巻き戻された。issue #626）か。
    /// 打球が空中で捕られると<b>始動種別によらず</b>飛び出しの勝負になる（<see cref="DoubledOffResolver"/>）。</param>
    /// <param name="r2StealBreak">二塁走者が三盗で投球と同時に走っていた（issue #1138）か。一塁走者と同じく、
    /// ゴロなら読む遅れ無しで三塁へ向かい（<see cref="BaserunningCoefficients.StealBreakGrounderStartDelaySeconds"/>）、
    /// 空中で捕られれば二塁へ戻れるかの飛び出しの勝負になる。</param>
    /// <param name="r3StealBreak">三塁走者が重盗で本塁へ走っていた（issue #1138）か。二塁走者と同じ扱い（ゴロの本塁レース・三塁への帰塁）。</param>
    private static (int, int, int, bool) ApplyInPlayOut(
        BaseState bases, Player batter, Player? r1, Player? r2, Player? r3, int currentOuts,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv, HomePlayContext? home = null,
        StartType r1Start = StartType.Normal, bool r1StealBreak = false,
        bool r2StealBreak = false, bool r3StealBreak = false, FlyReads? reads = null)
    {
        if (currentOuts >= 2) return (0, 0, 0, false); // この凡打が3アウト目。進塁なし。

        // フォース進塁（2026-07-20, OPEN-QUESTIONS 2026-07-19項）: ゴロ凡打では打者走者が一塁へ向かうため、
        // 後方の占有で押し出される走者（一塁走者=常時 / 二塁走者=一塁走者あり / 三塁走者=満塁）は塁を
        // 明け渡さざるを得ない。守備が確実なアウト（一塁 or 併殺）を選んだ時点で押し出された走者の次塁は
        // 空く＝確定進塁。ProductiveOutAdvanceProb は非フォースの進塁打（二塁→三塁の右方向ゴロ等）専用。
        // フライ（タッグアップ＝時計が別物）と home 無し（従来互換・単体テスト経路）はフォース対象外。
        // フォース判定は打席開始時の占有で確定させる（併殺/野選で r1 が消えても後続の押し出しは起きていた）。
        var isGrounder = home is { IsFly: false };
        var r1Forced = isGrounder && r1 is not null;
        var r2Forced = isGrounder && r1 is not null && r2 is not null;
        var r3Forced = isGrounder && r1 is not null && r2 is not null && r3 is not null;

        var extraOuts = 0;
        var homeOuts = 0;
        var batterSafeOnFc = false;
        // 守備が本塁へ投げた（セーフでも）＝この打球の送球は使い切った。後続の走者は送球の勝負をしない（design-12 §3.5・1プレー1送球）。
        var homeThrowUsed = false;
        // 併殺・野選（内野ゴロ×一塁走者, Issue #369）: 実試合経路（home!=null・ゴロ・打者の一塁到達時刻あり）は
        // 13%固定テーブルでなく「捕球点→二塁の封殺（P_force2）／二塁→一塁の転送」の二段の物理レースで解く
        // （#318 のゴロ進塁打と同じ流儀）。それ以外（home無し・打者一塁到達時刻なし＝単体テスト/
        // 純テーブル経路）は従来テーブル（DoublePlayProb / FieldersChoiceProb）へフォールバックし乱数消費順も保存する。
        //
        // ⚠ フライ（home is { IsFly: true }）は従来テーブルの対象外（issue fly-out-legacy-double-play-table）:
        // 13% はゴロ併殺の率で、フライで一塁走者にフォースは掛からない（「二塁で封殺」の形自体が嘘）。
        // 空中で捕られた打球で一塁走者が余分にアウトになるのは、下の飛び出しの物理レース（DoubledOffResolver＝
        // 一塁へ戻れず憤死）だけ。実測では 0/1死・一塁走者ありのフライアウトの 20% がここで「捕球を待って
        // 二塁へ走って刺される」絵になっていた。
        var flyOut = home is { IsFly: true };
        // 一塁走者の 1→2 の move を併殺の解決が既に積んだか（一塁自己フォース→二塁タッチの失敗。PR #1076 レビュー）。
        // 塁への配置は下の一塁走者カスケードに任せる（二塁走者が押し出された後に置く）ので、そこで move を重ねない。
        var r1AdvanceRecorded = false;
        // 一塁の自己フォース→二塁タッチで一塁走者を刺した時刻[s]。これが3アウト目のときだけ得点の時間の勝負に使う。
        double? tagOutAtSeconds = null;
        if (r1 is not null && home is { IsFly: false, BatterToFirstSeconds: not null } dp)
        {
            DoublePlayForceTarget dpTarget;
            // 投球と同時に走っていた走者（エンドラン・ギャンブル始動・盗塁の巻き戻し）はゴロを読む遅れが無い（設計書12 §4.7.1・issue #1241）。
            var r1DpDelay = r1Start is StartType.Contact or StartType.Gamble || r1StealBreak
                ? coeff.StealBreakGrounderStartDelaySeconds : coeff.DoublePlayR1StartDelaySeconds;
            var r2DpDelay = r2StealBreak ? coeff.StealBreakGrounderStartDelaySeconds : coeff.DoublePlayR1StartDelaySeconds;
            var r3DpDelay = r3StealBreak ? coeff.StealBreakGrounderStartDelaySeconds : coeff.DoublePlayR1StartDelaySeconds;
            (extraOuts, batterSafeOnFc, dpTarget, r1AdvanceRecorded, tagOutAtSeconds) = ResolveDoublePlay(
                bases, batter, r1, r2, r3, dp, coeff, rng, mv, r1DpDelay, r2DpDelay, r3DpDelay);
            if (extraOuts > 0)
            {
                // どの塁を攻めたかで空ける走者が違う（Q31）。二塁/三塁が空けば、後続の押し出し進塁
                // （この下の二塁走者・一塁走者の進塁ブロック）が既存のまま自動的に残りの走者を正しく進める＝特別扱い不要。
                switch (dpTarget)
                {
                    case DoublePlayForceTarget.Second: r1 = null; break;
                    case DoublePlayForceTarget.Third: r2 = null; break;
                    case DoublePlayForceTarget.Home:
                        r3 = null;
                        homeOuts++; // 本塁クロスプレー憤死の統計参考値（ApplyRunnerAdvance の HomeVerdict.OutAtHome と同じ計上先）
                        break;
                }
            }
        }
        // 併殺（1塁走者あり, 従来テーブル。ゴロ／home無しだけ）。
        else if (r1 is not null && !flyOut && MathUtil.Chance(
                MathUtil.Clamp(coeff.DoublePlayProb - coeff.SpeedSlope * (coeff.AbilityEffect.Running(r1.Speed) - 50), 0.02, 0.5), rng))
        {
            extraOuts = 1;
            mv?.Add(new RunnerMove(r1, 1, 2, Out: true) { Force = true }); // 二塁封殺
            bases.First = null;
            r1 = null;
        }
        // 野選（FC, design-14 P1-1）: DP不成立時のみ判定。既定0では分岐自体に入らず rng を一切消費しない
        // （MathUtil.Chance は確率0でも rng.NextDouble() を1回消費するため、確率式へ0を渡すだけでは
        //  乱数消費順が既存と不一致になる。ガードで分岐そのものをスキップして完全一致を保つ）。
        else if (r1 is not null && !flyOut && coeff.FieldersChoiceProb > 0.0 && MathUtil.Chance(
                MathUtil.Clamp(coeff.FieldersChoiceProb + coeff.SpeedSlope * (coeff.AbilityEffect.Running(batter.Speed) - 50), 0.0, 0.95), rng))
        {
            extraOuts = 1;
            batterSafeOnFc = true;
            mv?.Add(new RunnerMove(r1, 1, 2, Out: true) { Force = true }); // 二塁封殺（野選、打者は生存）
            bases.First = batter;
            mv?.Add(new RunnerMove(batter, 0, 1, false));
            r1 = null;
        }

        // ライナー併殺（設計書12 §4, G2）: コンタクト始動(エンドラン等)の一塁走者は打球が空中で捕られた時点で
        // 既に走り出している＝塁へ戻れるかの時間の勝負になる。フライ/ライナー以外（ゴロ）は対象外。
        // 盗塁で走っていた走者（r1StealBreak・issue #626 の巻き戻し）とギャンブル始動も同じ勝負に乗る
        // （issue fly-out-runner-dp-table-and-doubled-off-depiction）。投球で既に走っていたぶんは
        // StealBreakExtraLeadM だけリードが余計に伸びる（戻りが遠い）。通常始動（Normal）は対象外＝帰塁できる。
        // フライの読み（issue #1130）: 落ちると読んで打った瞬間に走った走者（読み違い）も同じ勝負に乗る。
        // 読みは既に走っていた走者（エンドラン・盗塁）を引かない＝同じ打球で読みと飛び出しを二重に引かない。
        // 捕球後に塁へ戻る勝負（飛び出し・ハーフウェー）は、守備が1球を1か所へ投げる＝最初に勝負になった1人だけ（#1130）。
        // 打者のアウトと合わせて3アウトに達したら、それ以上は勝負しない（4つ目のアウトを作らない）。
        var returnThrowUsed = false;
        bool CanContestReturn() => !returnThrowUsed && currentOuts + 1 + extraOuts < 3;
        // 捕った野手がその塁を守る本人（一塁手が一塁走者の塁・三塁手が三塁走者の塁）なら、送球する相手がいない＝勝負にしない。
        bool CatcherOwnsBase(int baseNo) => home?.FielderRole is { } fr
            && (baseNo == 1 && fr == Field.FieldPosition.FirstBase || baseNo == 3 && fr == Field.FieldPosition.ThirdBase);
        var r1RacedBack = false;
        if (r1 is not null && home is { IsFly: true } && reads?.At(1) is { Start: FlyRunnerStart.Go } && CatcherOwnsBase(1))
        {
            r1RacedBack = true; // 読み違えて走ったが、捕った一塁手は自分の塁へ投げられない＝戻れる（乱数なし）
        }
        else if (r1 is not null && home is { IsFly: true } liner
            && (r1Start is StartType.Contact or StartType.Gamble || r1StealBreak
                || reads?.At(1) is { Start: FlyRunnerStart.Go }))
        {
            returnThrowUsed = true;
            var catchAt = liner.Situation.BallFieldedAtSeconds;
            var catchToFirstM = (liner.Field.FirstBase - liner.Situation.BallFieldedPoint).Length;
            var extraLead = r1StealBreak || r1Start == StartType.Gamble ? coeff.StealBreakExtraLeadM : 0.0;
            var arm = liner.Situation.OutfielderThrowSpeedMps;
            var fielding = liner.Situation.OutfielderFielding;
            // 絵の材料（表示専用）: 守備の戻し送球が一塁に届く時刻・走者が稼いだリード・際どさ。
            var throwArrive = catchAt + DoubledOffResolver.DefenseReturnSeconds(catchToFirstM, arm, coeff, fielding);
            var leadM = DoubledOffResolver.LeadGainedM(catchAt, coeff, extraLead);
            var maxLeadM = DoubledOffResolver.MaxLeadM(catchAt, coeff, extraLead);
            var doMargin = DoubledOffResolver.Margin(r1, catchAt, catchToFirstM, arm, coeff, fielding, extraLead);
            var doCloseCall = Math.Abs(doMargin) < coeff.CloseCallMarginSeconds;
            r1RacedBack = true;
            if (DoubledOffResolver.Resolve(r1, catchAt, catchToFirstM, arm, coeff, rng, fielding, extraLead,
                    liner.Situation.ThrowerThrowAccuracy, out var doScatter)
                == DoubledOffResult.DoubledOff)
            {
                extraOuts++;
                mv?.Add(new RunnerMove(r1, 1, 1, Out: true) // 一塁へ戻れず憤死
                    { CloseCall = doCloseCall, DoubledOffThrowArriveSeconds = throwArrive, DoubledOffLeadM = leadM, DoubledOffMaxLeadM = maxLeadM, ThrowScatter = doScatter });
                bases.First = null;
                r1 = null;
            }
            else
            {
                // 帰塁セーフ。塁上は不変だが「飛び出して戻った」絵を出すため move を積む（表示専用・
                // ToBase==FromBase なので得点・封殺・先頭走者の判定はどれも拾わない）。
                mv?.Add(new RunnerMove(r1, 1, 1, Out: false)
                    { CloseCall = doCloseCall, DoubledOffThrowArriveSeconds = throwArrive, DoubledOffLeadM = leadM, DoubledOffMaxLeadM = maxLeadM, ThrowScatter = doScatter });
            }
        }

        // 三盗（二塁走者）・重盗の本塁突入（三塁走者）で走っていた走者も、空中で捕られれば同じ飛び出しの勝負（issue #1138）。
        // 一塁走者の勝負の後に二塁・三塁の順で引く（盗塁が重なるのは二塁走者単独の三盗か、一・三塁の重盗だけ）。
        // ハーフウェーで待った一塁走者（#1130）: 捕球を見てその位置から一塁へ戻る（タッチアップはできない）。
        if (r1 is not null && !r1RacedBack && reads?.At(1) is { Start: FlyRunnerStart.Halfway } half1
            && home is { IsFly: true } hfly1)
        {
            r1RacedBack = true;
            if (ResolveHalfwayReturn(bases, r1, 1, half1.HoldLeadM, hfly1, coeff, rng, mv, ref extraOuts, CanContestReturn() && !CatcherOwnsBase(1), ref returnThrowUsed)) r1 = null;
        }
        var r2RacedBack = false;
        var r3RacedBack = false;
        if (r2 is not null && home is { IsFly: true } liner2
            && (r2StealBreak || reads?.At(2) is { Start: FlyRunnerStart.Go }))
        {
            r2RacedBack = true;
            // 送球が残っているときだけ勝負（残っていなければ帰塁）。盗塁の走者（#1138）も1球は1か所＝先の勝負で送球を使えば帰塁（#1241）。
            if (CanContestReturn())
            {
                returnThrowUsed = true;
                var extraLead2 = r2StealBreak ? coeff.StealBreakExtraLeadM : 0.0;
                if (ResolveStealBreakReturn(bases, r2, 2, liner2, coeff, rng, mv, ref extraOuts, extraLead2)) r2 = null;
            }
        }
        else if (r2 is not null && reads?.At(2) is { Start: FlyRunnerStart.Halfway } half2 && home is { IsFly: true } hfly2)
        {
            r2RacedBack = true;
            if (ResolveHalfwayReturn(bases, r2, 2, half2.HoldLeadM, hfly2, coeff, rng, mv, ref extraOuts, CanContestReturn(), ref returnThrowUsed)) r2 = null;
        }
        if (r3 is not null && home is { IsFly: true } liner3
            && (r3StealBreak || reads?.At(3) is { Start: FlyRunnerStart.Go }))
        {
            r3RacedBack = true;
            if (CanContestReturn() && (r3StealBreak || !CatcherOwnsBase(3)))
            {
                returnThrowUsed = true;
                var extraLead3 = r3StealBreak ? coeff.StealBreakExtraLeadM : 0.0;
                if (ResolveStealBreakReturn(bases, r3, 3, liner3, coeff, rng, mv, ref extraOuts, extraLead3)) r3 = null;
            }
        }

        // 打者自身がこの打席でアウトになるか。野選（FC）成立時は打者は生存するため0（design-14 P1-1）。
        var battersOwnOut = batterSafeOnFc ? 0 : 1;
        if (currentOuts + battersOwnOut + extraOuts >= 3)
        {
            // イニング終了。ただし3アウト目が二塁のタッチ（一塁の自己フォース→二塁タッチ）なら封殺ではなく時間の勝負＝
            // タッチより先に本塁へ着いた三塁走者の得点は認められる（公認野球規則 5.08(a)）。封殺・打者アウトが3つ目のときは0。
            var timePlayRuns = 0;
            if (tagOutAtSeconds is { } tagAt && r3 is not null && !r3RacedBack && home is { } tpHome)
            {
                var r3Delay = r3StealBreak ? coeff.StealBreakGrounderStartDelaySeconds : coeff.HomeGrounderStartDelaySeconds;
                if (HomePlayResolver.RunnerTimeSeconds(r3, 3, 4, tpHome.Field, coeff, r3Delay) < tagAt)
                {
                    timePlayRuns = 1;
                    mv?.Add(new RunnerMove(r3, 3, 4, false) { ExtraStartDelaySeconds = r3Delay });
                    bases.Third = null;
                }
            }
            return (timePlayRuns, extraOuts, homeOuts, batterSafeOnFc);
        }

        var runs = 0;
        // 3塁走者の生還。内野ゴロ(併殺なし)は守備深さ駆動の本塁レース（G1, 設計書12 §4/§5。通常守備も
        // issue #1788 で物理レース化＝下のC分岐）、フライ(犠飛)は捕球時刻起点のタッチアップ物理レース
        // （Issue #90, 設計書12 §3.5）。home無し（単体テスト等）・併殺成立後だが打者の一塁到達時刻が無い
        // 経路だけが従来の SacFlyScoreProb テーブルに残る（実試合のゴロはもう通らない・issue #1241/#1788）。
        if (r3 is not null && !r3RacedBack)
        {
            // フォースの三塁走者（満塁ゴロ）: 自重できない（後続に押し出される）。
            // 内野前進×併殺なしは本塁封殺の勝負（送り判定なしで必ず走る）、それ以外は守備が
            // 一塁/併殺を選んだ＝本塁は空く＝確定生還。
            if (r3Forced && home is { IsFly: false } hforce && extraOuts == 0
                && hforce.InfieldDepth == Tactics.DefenseDepth.In)
            {
                var delay = coeff.HomeGrounderStartDelaySeconds;
                var forceHomeMargin = HomePlayResolver.Margin(r3, 3, hforce.Situation, hforce.Field, coeff, delay);
                var forceHomeCloseCall = Math.Abs(forceHomeMargin) < coeff.CloseCallMarginSeconds;
                var (forceHomeOutcome, forceHomeLate, forceHomeScatter) = HomePlayResolver.ResolveWithWildThrowVerdict(
                    r3, 3, hforce.Situation, hforce.Field, coeff, HomePlayResolver.HomeParams(coeff), rng, delay);
                if (forceHomeOutcome != HomePlayResolver.HomeThrowOutcome.OutAtHome)
                {
                    runs++;
                    mv?.Add(new RunnerMove(r3, 3, 4, false) { CloseCall = forceHomeCloseCall, ExtraStartDelaySeconds = delay, ThrowScatter = forceHomeScatter });
                    if (forceHomeOutcome == HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow)
                    {
                        bases.Third = null; // r3 は既に得点済み＝以降は二塁・一塁だけを押し上げる。
                        runs += PushRunnersOnWildThrow(bases, mv);
                    }
                }
                else
                {
                    extraOuts++; homeOuts++; // 本塁封殺（フォースアウト）
                    // 本封→一封（issue #741）: 守備が投げたのは処理点→本塁の1本だけ＝打者の一塁アウトには
                    // 捕手の一塁転送が要る。二死はここへ来ない（打者の一塁アウトで既にチェンジ＝上の早期 return）。
                    // 転送レースは併殺の二段目と同じ作法。乱数はこの分岐（満塁×内野前進×本塁封殺成立）でだけ1回引く。
                    var relayed = hforce.BatterToFirstSeconds is not null;
                    // 捕手の一塁転送1本の逸れ（issue #859。捕手の送球精度は未配線＝50）。転送したときだけ引く。
                    var (homeRelayScatter, homeRelayNoise) = relayed
                        ? HomePlayResolver.SampleThrowScatter(50, coeff, rng)
                        : (default(Fielding.ThrowScatterModel.Scatter), 0.0);
                    mv?.Add(new RunnerMove(r3, 3, 4, Out: true)
                        { CloseCall = forceHomeCloseCall, ExtraStartDelaySeconds = delay + forceHomeLate, Force = true, HomeForceRelay = relayed, ThrowScatter = forceHomeScatter,
                          RelayThrowScatter = relayed ? homeRelayScatter : null });
                    if (relayed && MathUtil.Chance(HomeForceRelayBatterSafeProbability(hforce, coeff, homeRelayNoise), rng))
                    {
                        // 野選: 本塁は封殺したが転送が打者に間に合わない＝打者は一塁で生きる。一塁走者・二塁走者は
                        // この先のフォース進塁カスケードが押し上げる（一塁の後始末は r1 自身が居るときだけ＝打者は残る）。
                        batterSafeOnFc = true;
                        battersOwnOut = 0;
                        bases.First = batter;
                        mv?.Add(new RunnerMove(batter, 0, 1, false));
                    }
                }
                // 返球が逸れた分岐は押し上げ済みの三塁（元の二塁走者）を消さない（issue #830 と同じ不具合）
                if (forceHomeOutcome != HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow) bases.Third = null;
                r3 = null;
            }
            else if (r3Forced)
            {
                runs++; // 守備は一塁/併殺を選択＝フォースの三塁走者は悠々生還。
                mv?.Add(new RunnerMove(r3, 3, 4, false));
                bases.Third = null;
                r3 = null;
            }
            // 深さが本塁プレーの有無を決める（G1）: 後退=献上して還す／前進=本塁で勝負／通常=従来テーブル。
            else if (home is { IsFly: false } h && extraOuts == 0 && h.InfieldDepth == Tactics.DefenseDepth.Deep)
            {
                runs++; // 内野後退＝献上。三塁走者は悠々生還。
                mv?.Add(new RunnerMove(r3, 3, 4, false));
                bases.Third = null;
                r3 = null;
            }
            else if (home is { IsFly: false } hi && extraOuts == 0 && hi.InfieldDepth == Tactics.DefenseDepth.In)
            {
                // 内野前進＝本塁で時間の勝負。ゴロ走者は打球を読んでから走る分だけ遅い（delay）。
                // 際どければ自重（三塁残留＝無得点）、行けば生還 or 憤死。
                // 重盗で本塁へ走っていた三塁走者は打球を読む遅れが無い（issue #1138）。
                var delay = r3StealBreak ? coeff.StealBreakGrounderStartDelaySeconds : coeff.HomeGrounderStartDelaySeconds;
                var prob = HomePlayResolver.SuccessProbability(r3, 3, hi.Situation, hi.Field, coeff, delay);
                if (HomeSendDecision.ShouldSend(prob, currentOuts, hi.Aggression, hi.Tactics))
                {
                    var backHomeMargin = HomePlayResolver.Margin(r3, 3, hi.Situation, hi.Field, coeff, delay);
                    var backHomeCloseCall = Math.Abs(backHomeMargin) < coeff.CloseCallMarginSeconds;
                    var (backHomeOutcome, backHomeLate, backHomeScatter) = HomePlayResolver.ResolveWithWildThrowVerdict(
                        r3, 3, hi.Situation, hi.Field, coeff, HomePlayResolver.HomeParams(coeff), rng, delay);
                    // 守備は本塁へ投げた＝この打球の送球はこの1本（打者を一塁で刺す送球は無い）。打者は野選で生きる／
                    // 本塁で刺したときだけ捕手の一塁転送と競う（満塁の本塁封殺と同じ作法）。
                    homeThrowUsed = true;
                    if (backHomeOutcome != HomePlayResolver.HomeThrowOutcome.OutAtHome)
                    {
                        runs++;
                        mv?.Add(new RunnerMove(r3, 3, 4, false) { CloseCall = backHomeCloseCall, ExtraStartDelaySeconds = delay, ThrowScatter = backHomeScatter });
                        if (backHomeOutcome == HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow)
                        {
                            bases.Third = null; // r3 は既に得点済み＝以降は二塁・一塁だけを押し上げる。
                            runs += PushRunnersOnWildThrow(bases, mv);
                        }
                        // 野選: 一塁の打者はそのまま生きる（一塁走者は押し出されて二塁へ。後段の一塁走者カスケードが担う）。
                        batterSafeOnFc = true;
                        battersOwnOut = 0;
                        bases.First = batter;
                        mv?.Add(new RunnerMove(batter, 0, 1, false));
                    }
                    else
                    {
                        extraOuts++; homeOuts++; // バックホーム憤死（本塁で刺殺）
                        var relayed = hi.BatterToFirstSeconds is not null;
                        var (homeRelayScatter, homeRelayNoise) = relayed
                            ? HomePlayResolver.SampleThrowScatter(50, coeff, rng)
                            : (default(Fielding.ThrowScatterModel.Scatter), 0.0);
                        mv?.Add(new RunnerMove(r3, 3, 4, Out: true)
                            { CloseCall = backHomeCloseCall, ExtraStartDelaySeconds = delay + backHomeLate, ThrowScatter = backHomeScatter,
                              HomeForceRelay = relayed, RelayThrowScatter = relayed ? homeRelayScatter : null });
                        if (relayed && MathUtil.Chance(HomeForceRelayBatterSafeProbability(hi, coeff, homeRelayNoise), rng))
                        {
                            batterSafeOnFc = true;
                            battersOwnOut = 0;
                            bases.First = batter;
                            mv?.Add(new RunnerMove(batter, 0, 1, false));
                        }
                    }
                    // 返球が逸れた分岐は押し上げ済みの三塁（元の二塁走者）を消さない（issue #830 と同じ不具合）
                    if (backHomeOutcome != HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow) bases.Third = null;
                    r3 = null;
                }
                // 自重は三塁に残る（bases.Third は r3 のまま）。
            }
            // 併殺成立・二塁封殺だけ（A・B, issue #1788）: 一塁走者の併殺/野選（ResolveDoublePlay、dpTarget=Second。
            // r2/r3が居る満塁のTarget=Third/Homeは必ずr3Forced＝上の2枝で既に処理済み＝ここに来るのはそれ以外）
            // で extraOuts>0 になった打球。守備の送球は二塁(＋一塁への転送)で使い切っていて本塁へは投げていない
            // ＝三塁走者は無条件で生還する（乱数を引かない。後退の献上と同じ扱い）。
            else if (!r3Forced && extraOuts > 0 && home is { IsFly: false, BatterToFirstSeconds: not null })
            {
                runs++;
                mv?.Add(new RunnerMove(r3, 3, 4, false));
                bases.Third = null;
                r3 = null;
            }
            // 通常守備（C, issue #1788）: 前進と同じ本塁の時間の勝負だが、通常守備の送球は前進より長い分
            // 生還しやすい（後退＞通常＞前進を保つ）。前進と違い、見込み（prob=三塁走者の生還見込み）が
            // 高すぎる（NormalDepthHomeThrowMaxRunnerSuccess を超える）ときは守備が本塁を捨て、一塁で
            // 確実に打者を取る（1点は渡すが追加アウトのリスクを負わない）。
            else if (home is { IsFly: false } hn && extraOuts == 0 && hn.InfieldDepth == Tactics.DefenseDepth.Normal)
            {
                var delay = r3StealBreak ? coeff.StealBreakGrounderStartDelaySeconds : coeff.HomeGrounderStartDelaySeconds;
                var prob = HomePlayResolver.SuccessProbability(r3, 3, hn.Situation, hn.Field, coeff, delay);
                if (HomeSendDecision.ShouldSend(prob, currentOuts, hn.Aggression, hn.Tactics))
                {
                    if (prob > coeff.NormalDepthHomeThrowMaxRunnerSuccess)
                    {
                        // 見込みが薄い＝守備は本塁を捨てて一塁で確実に打者を取る（送球は一塁の1本。RNG非消費）。
                        runs++;
                        mv?.Add(new RunnerMove(r3, 3, 4, false));
                        bases.Third = null;
                        r3 = null;
                    }
                    else
                    {
                        var backHomeMargin = HomePlayResolver.Margin(r3, 3, hn.Situation, hn.Field, coeff, delay);
                        var backHomeCloseCall = Math.Abs(backHomeMargin) < coeff.CloseCallMarginSeconds;
                        var (backHomeOutcome, backHomeLate, backHomeScatter) = HomePlayResolver.ResolveWithWildThrowVerdict(
                            r3, 3, hn.Situation, hn.Field, coeff, HomePlayResolver.HomeParams(coeff), rng, delay);
                        // 守備は本塁へ投げた＝この打球の送球はこの1本（打者を一塁で刺す送球は無い）。打者は野選で生きる／
                        // 本塁で刺したときだけ捕手の一塁転送と競う（前進・満塁の本塁封殺と同じ作法）。
                        homeThrowUsed = true;
                        if (backHomeOutcome != HomePlayResolver.HomeThrowOutcome.OutAtHome)
                        {
                            runs++;
                            mv?.Add(new RunnerMove(r3, 3, 4, false) { CloseCall = backHomeCloseCall, ExtraStartDelaySeconds = delay, ThrowScatter = backHomeScatter });
                            if (backHomeOutcome == HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow)
                            {
                                bases.Third = null; // r3 は既に得点済み＝以降は二塁・一塁だけを押し上げる。
                                runs += PushRunnersOnWildThrow(bases, mv);
                            }
                            // 野選: 一塁の打者はそのまま生きる（一塁走者は押し出されて二塁へ。後段の一塁走者カスケードが担う）。
                            batterSafeOnFc = true;
                            battersOwnOut = 0;
                            bases.First = batter;
                            mv?.Add(new RunnerMove(batter, 0, 1, false));
                        }
                        else
                        {
                            extraOuts++; homeOuts++; // バックホーム憤死（本塁で刺殺）
                            var relayed = hn.BatterToFirstSeconds is not null;
                            var (homeRelayScatter, homeRelayNoise) = relayed
                                ? HomePlayResolver.SampleThrowScatter(50, coeff, rng)
                                : (default(Fielding.ThrowScatterModel.Scatter), 0.0);
                            mv?.Add(new RunnerMove(r3, 3, 4, Out: true)
                                { CloseCall = backHomeCloseCall, ExtraStartDelaySeconds = delay + backHomeLate, ThrowScatter = backHomeScatter,
                                  HomeForceRelay = relayed, RelayThrowScatter = relayed ? homeRelayScatter : null });
                            if (relayed && MathUtil.Chance(HomeForceRelayBatterSafeProbability(hn, coeff, homeRelayNoise), rng))
                            {
                                batterSafeOnFc = true;
                                battersOwnOut = 0;
                                bases.First = batter;
                                mv?.Add(new RunnerMove(batter, 0, 1, false));
                            }
                        }
                        // 返球が逸れた分岐は押し上げ済みの三塁（元の二塁走者）を消さない（issue #830 と同じ不具合）
                        if (backHomeOutcome != HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow) bases.Third = null;
                        r3 = null;
                    }
                }
                // 自重は三塁に残る（bases.Third は r3 のまま）。
            }
            // 犠飛のタッチアップ（Issue #90, 設計書12 §3.5）: フライ捕球の実試合経路（home!=null・IsFly）は
            // 定数テーブルでなく物理レース。走者は捕球時刻にスタート（二次リードなし＝全塁間を走る）、深い外野
            // フライは送球が長く生還、浅いフライは自重。送り判定→秒の勝負で生還 or 本塁憤死（本塁と同じ流儀）。
            else if (home is { IsFly: true } hfly && extraOuts == 0)
            {
                var tagUp = HomePlayResolver.TagUpHomeParams(coeff);
                var prob = HomePlayResolver.TagUpSuccessProbability(r3, 3, hfly.Situation, hfly.Field, coeff, tagUp);
                if (returnThrowUsed && HomeSendDecision.ShouldSend(prob, currentOuts, hfly.Aggression, hfly.Tactics))
                {
                    // 送球は塁へ戻る走者へ行った（#1130・1球は1か所）＝本塁は空く。勝負なしで生還（乱数なし）。
                    runs++;
                    mv?.Add(new RunnerMove(r3, 3, 4, false) { TagUpCatchAtSeconds = hfly.Situation.BallFieldedAtSeconds });
                    bases.Third = null;
                    r3 = null;
                }
                else if (HomeSendDecision.ShouldSend(prob, currentOuts, hfly.Aggression, hfly.Tactics))
                {
                    returnThrowUsed = true; // 本塁への送球で1本使う＝後続（二塁・一塁走者）のタッチアップは送球の勝負をしない。
                    var tagMargin = HomePlayResolver.TagUpMargin(r3, 3, hfly.Situation, hfly.Field, coeff, tagUp);
                    var tagCloseCall = Math.Abs(tagMargin) < coeff.CloseCallMarginSeconds;
                    // 絵も判定と同じ「捕球を待ってから走る」時計にする（issue score-display-before-home-touch）。
                    var tagCatchAt = hfly.Situation.BallFieldedAtSeconds;
                    var (tagOutcome, tagLate, tagScatter) = HomePlayResolver.TagUpResolveWithWildThrowVerdict(
                        r3, 3, hfly.Situation, hfly.Field, coeff, tagUp, rng);
                    if (tagOutcome != HomePlayResolver.HomeThrowOutcome.OutAtHome)
                    {
                        runs++;
                        mv?.Add(new RunnerMove(r3, 3, 4, false)
                            { CloseCall = tagCloseCall, TagUpCatchAtSeconds = tagCatchAt, ThrowScatter = tagScatter });
                        if (tagOutcome == HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow)
                        {
                            bases.Third = null; // r3 は既に得点済み＝以降は二塁・一塁だけを押し上げる。
                            runs += PushRunnersOnWildThrow(bases, mv);
                        }
                    }
                    else
                    {
                        extraOuts++; homeOuts++; // タッチアップ失敗＝本塁での走塁死
                        mv?.Add(new RunnerMove(r3, 3, 4, Out: true)
                            { CloseCall = tagCloseCall, TagUpCatchAtSeconds = tagCatchAt, ExtraStartDelaySeconds = tagLate, ThrowScatter = tagScatter });
                    }
                    // 返球が逸れた分岐は、三塁を空けた後で二塁走者を三塁へ押し上げ済み＝ここで空にすると
                    // 押し上げた走者が消える（issue #830 の Heavy で走者保存則違反として表に出た）
                    if (tagOutcome != HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow) bases.Third = null;
                    r3 = null;
                }
                // 自重（浅いフライ）は三塁に残る（RNG非消費＝本塁レースの流儀, 不変条件#2）。
            }
            // 従来テーブル（設計書12 §4.6。issue #1788 で実試合のゴロ経路は上のA/B/Cへ全部移った）。残るのは
            // home無し（単体テスト等）と、home はあるが打者の一塁到達時刻が組めない（BatterToFirstSeconds無し）
            // 併殺成立後の経路だけ＝物理レースが組めない従来互換の経路。フライで先に走者が戻れずアウトになった
            // 打席（extraOuts>0）は対象外＝犠飛は無い（#1130 以前はここへ落ちて固定確率で生還していた）。
            else if (home is not { IsFly: true } && MathUtil.Chance(coeff.SacFlyScoreProb, rng))
            {
                runs++;
                mv?.Add(new RunnerMove(r3, 3, 4, false));
                bases.Third = null;
                r3 = null;
            }
        }

        if (currentOuts + battersOwnOut + extraOuts >= 3) return (runs, extraOuts, homeOuts, batterSafeOnFc); // 本塁憤死で3アウト＝以降の進塁なし。

        // 進塁打で二塁走者が三塁へ。フォースは確定進塁（rng非消費）。非フォース×ゴロ×一塁走者なし×
        // 三塁空き（＝典型的な進塁打の1場面）は 18%固定テーブルでなく「捕球点→三塁の物理レース」で解く
        // （Issue #318, 設計書12 §3.5）。守備は幾何（P_direct）で「一塁を取る/一塁を捨てて三塁へ直接投げる/
        // 走者は自重」を選び、送り判定1段だけが本塁・三塁レースと同じ流儀で入る。それ以外（フォース・フライ・
        // extraOuts>0・home無し）は従来テーブル（ProductiveOutAdvanceProb）へフォールバック（rng消費順も従来どおり）。
        // フライ捕球での非フォース進塁はタッチアップ＝捕球まで塁を離れられない。絵の起点にだけ使う
        // 観測値（表示専用・乱数不使用。issue score-display-before-home-touch）。
        var flyTagUpCatchAt = home is { IsFly: true } flyAdv
            ? (double?)flyAdv.Situation.BallFieldedAtSeconds
            : null;

        if (r2 is not null && !r2RacedBack && bases.Third is null)
        {
            if (!r2Forced && (extraOuts == 0 || homeThrowUsed) && home is { IsFly: false } adv)
            {
                if (homeThrowUsed)
                {
                    // 送球は本塁へ行った＝三塁は無警戒。勝負なしで進む（乱数なし）。
                    mv?.Add(new RunnerMove(r2, 2, 3, false) { ExtraStartDelaySeconds = coeff.GrounderAdvanceStartDelaySeconds });
                    bases.Third = r2;
                    bases.Second = null;
                }
                else
                {
                    ResolveGrounderAdvance(bases, r2, adv, coeff, rng, mv, ref extraOuts, r2StealBreak);
                }
                r2 = null; // 進塁・憤死・自重のいずれでも二塁走者の帰趨はここで確定（自重時は bases.Second のまま）。
            }
            else if (extraOuts == 0 && home is { IsFly: true } fly2)
            {
                // フライ捕球＝タッチアップ（design-14 P3-11）。18%固定でなく犠飛と同じ物理レース。
                // フライではフォースが掛からない（r2Forced は必ず false）ので分岐を分けない。
                ResolveFlyTagUp(bases, r2, 2, fly2, coeff, rng, mv, ref extraOuts, returnThrowUsed);
                r2 = null; // 進塁・憤死・自重のいずれでも帰趨はここで確定（自重時は bases.Second のまま）。
            }
            // フライで先に追加アウトが出た打席（extraOuts>0）は進めない＝固定確率の進塁打はゴロ・home無しだけ（#1130）。
            else if (r2Forced || (home is not { IsFly: true } && MathUtil.Chance(coeff.ProductiveOutAdvanceProb, rng)))
            {
                mv?.Add(new RunnerMove(r2, 2, 3, false)
                    { TagUpCatchAtSeconds = r2Forced ? null : flyTagUpCatchAt });
                bases.Third = r2;
                bases.Second = null;
                r2 = null;
            }
        }
        // 進塁の送球勝負が3アウト目を作ったらここで打ち切る（進塁は起きない）。ここに来る時点で extraOuts が
        // 増えているのは ResolveGrounderAdvance か ResolveFlyTagUp（二塁走者）の走塁死だけ。
        if (currentOuts + battersOwnOut + extraOuts >= 3) return (runs, extraOuts, homeOuts, batterSafeOnFc);
        // 飛び出しの勝負を走って戻った一塁走者は、その捕球でタッチアップはできない（塁に戻るのが捕球の後）＝進塁抽選を引かない。
        if (r1 is not null && !r1RacedBack && bases.Second is null)
        {
            if (extraOuts == 0 && home is { IsFly: true } fly1)
            {
                // 一塁→二塁のタッチアップ（design-14 P3-11）。二塁への送球は最短なので、
                // 浅い・中くらいのフライでは到達見込みが送り閾値に届かず自重する＝実物どおり稀になる。
                ResolveFlyTagUp(bases, r1, 1, fly1, coeff, rng, mv, ref extraOuts, returnThrowUsed);
            }
            else if (r1Forced || (home is not { IsFly: true } && MathUtil.Chance(coeff.ProductiveOutAdvanceProb, rng)))
            {
                if (!r1AdvanceRecorded)
                    mv?.Add(new RunnerMove(r1, 1, 2, false)
                        { TagUpCatchAtSeconds = r1Forced ? null : flyTagUpCatchAt });
                bases.Second = r1;
                // 一塁の後始末は「まだ r1 自身が居るとき」だけ行う（Q31）。併殺の代替ルート
                // （三塁/本塁の自己フォース）で野選（打者一塁生存）が成立すると、r1 は対象走者でない
                // （生存し続けここへ来る）のに一塁には既に打者が立っている＝無条件で null にすると
                // 打者を消してしまう（2026-08-27実測: 走者保存則違反で発覚）。既存の全経路では
                // ここに来る時点で bases.First は必ず r1 自身のまま＝この条件は既存経路には無害。
                if (ReferenceEquals(bases.First, r1)) bases.First = null;
            }
        }

        return (runs, extraOuts, homeOuts, batterSafeOnFc);
    }

    /// <summary>
    /// ゴロ凡打の非フォース進塁打（二塁→三塁, Issue #318／issue productive-out-second-to-third-not-advancing,
    /// 設計書12 §3.5）を物理レースで解く。この呼び出しに至った時点で打者走者は一塁アウトが確定している
    /// （実際の内野は「一塁の確実な1アウト」を捨てて三塁の走者を直接狙う判断はしない＝守備の危険回避を
    /// 物理レースだけでは表現できないため、旧実装にあった「一塁を捨てて三塁へ直接送球」分岐は廃止）。
    /// 二塁走者の帰趨は、実際に発生する送球（一塁への送球到達時刻）を起点にした転送レース（P_relay）だけで決まる:
    /// 0) 走者より前（遊撃・三塁方向＝捕球点が本塁−二塁の線より三塁側）のゴロ → 自重（RNG非消費・issue #1418）。
    ///    例外は三盗で投球と同時に走っていた走者（もう走っている）。
    /// 0') 走者の後ろで一塁手・二塁手が捕ったゴロ → 必ず進む（RNG非消費・issue #1418）。一塁を取った守備は三塁へ
    ///    転送しない（実物の一塁手・二塁手はわざわざ三塁へ投げない）＝勝負にしない。投手ゴロは従来どおり A/B。
    /// A) 転送の脅威が実在する（P_relay &lt; 送り閾値）→ 走者自重（RNG非消費）／
    /// B) 脅威が薄い → 走者は三塁へ進み、転送とレース（P_relay）。
    /// 走者所要は本塁・三塁レースと同じ <see cref="HomePlayResolver"/> 経由（新しい走者の式を書かない）。
    /// 呼び出し前提: 非フォースの二塁走者（⇒一塁は必ず空）・三塁は空き・extraOuts==0・ゴロ。
    /// </summary>
    private static void ResolveGrounderAdvance(
        BaseState bases, Player r2, HomePlayContext home,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv,
        ref int extraOuts, bool stealBreak = false)
    {
        // 0) 打球の方向で自重（issue #1418・オーナー決定 案C）。二塁走者は自分より前（三塁側）へのゴロでは進まない
        //    ＝野手が三塁へ投げやすく、走者の目の前で捕られる。右方向（一・二塁側）のゴロだけ転送との勝負に進む。
        var frontSide = IsGrounderInFrontOfSecondRunner(home);
        if (!stealBreak && frontSide) return;

        var advance = HomePlayResolver.AdvanceParams(home.Field, coeff);
        // 三盗で投球と同時に走っていた二塁走者は、ゴロを読んでから走る遅れが無い（issue #1138）。
        var delay = stealBreak ? coeff.StealBreakGrounderStartDelaySeconds : coeff.GrounderAdvanceStartDelaySeconds;

        // 0') 一塁・二塁方向（走者の後ろ）の一塁手・二塁手のゴロ＝必ず進む・三塁への転送なし（issue #1418・オーナー決定
        //     2026-11-23「1塁方向へのゴロは100%進塁する（投ゴロを除く）。ファーストもわざわざ三塁に送球しない」）。
        if (!frontSide && IsRightSideInfielderGrounder(home))
        {
            mv?.Add(new RunnerMove(r2, 2, 3, false) { ExtraStartDelaySeconds = delay });
            bases.Third = r2;
            bases.Second = null;
            return;
        }

        if (home.ThrowArriveSeconds is not { } arriveAtFirst)
        {
            // 一塁送球が無い経路（転送起点が組めない）＝無血進塁（RNG非消費・フォールバック）。
            mv?.Add(new RunnerMove(r2, 2, 3, false) { ExtraStartDelaySeconds = delay });
            bases.Third = r2;
            bases.Second = null;
            return;
        }
        // 一塁を起点にした擬似 situation: 送球起点=一塁ベース・起点時刻=一塁送球到達時刻・肩=中継速度。
        // 一塁手の握り替えだけ AdvanceRelayTransferSeconds に差し替える（外野値と別。既存経路はビット一致）。
        var relay = new HomePlaySituation(
            home.Field.FirstBase, arriveAtFirst, coeff.RelayThrowSpeedMps, home.Situation.OutfielderFielding);
        var pRelay = HomePlayResolver.SuccessProbability(
            r2, 2, relay, home.Field, coeff, advance, delay, transferSecondsOverride: coeff.AdvanceRelayTransferSeconds);
        // 送り閾値（アウト数を取らない＝2アウト緩和なし。理由は HomeSendDecision.SendAdvanceThreshold のコメント）。
        var t = HomeSendDecision.SendAdvanceThreshold(home.Aggression, home.Tactics);

        // A) 自重。二塁残留（bases.Second は r2 のまま）。RNG非消費（本塁・三塁レースの流儀, 不変条件#2）。
        if (pRelay < t) return;

        // B) 走者は一塁経由の転送とレース。打者は一塁アウト（batterSafeOnFc は変えない＝この呼び出しに入った時点で確定済み）。
        var relayMargin = HomePlayResolver.Margin(
            r2, 2, relay, home.Field, coeff, advance, delay, transferSecondsOverride: coeff.AdvanceRelayTransferSeconds);
        var race = HomePlayResolver.ResolveRace(
            r2, 2, relay, home.Field, coeff, advance, rng, delay, transferSecondsOverride: coeff.AdvanceRelayTransferSeconds);
        ResolveAdvanceRace(bases, r2, race, relayMargin, delay, coeff, mv, ref extraOuts);
    }

    /// <summary>
    /// ゴロが二塁走者より前（遊撃・三塁方向）で捕られたか（issue #1418）。捕球点が本塁−二塁の線（Z 軸）より
    /// 三塁側（X&lt;0）なら前。純関数・乱数不使用。
    /// </summary>
    internal static bool IsGrounderInFrontOfSecondRunner(HomePlayContext home)
        => home.Situation.BallFieldedPoint.X < 0.0;

    /// <summary>
    /// 一塁手・二塁手が捕ったゴロか（issue #1418: 走者の後ろのゴロは二塁走者が必ず進む。投手ゴロは含めない）。
    /// <see cref="HomePlayContext.FielderRole"/> が無い呼び出し（単体テスト等）は false＝従来どおり勝負で決める。
    /// </summary>
    internal static bool IsRightSideInfielderGrounder(HomePlayContext home)
        => home.FielderRole is FieldPosition.FirstBase or FieldPosition.SecondBase;

    /// <summary>
    /// 盗塁で投球と同時に走っていた二塁・三塁走者が、打球を空中で捕られたときの飛び出しの勝負（issue #1138）。
    /// 一塁走者（<c>ApplyInPlayOut</c> 内）と同じ <see cref="DoubledOffResolver"/> を、捕球点→その塁の距離で引く。
    /// 返り値は憤死したか（憤死なら塁から外し extraOuts を足す）。帰塁セーフは「飛び出して戻った」絵のための move だけ積む。
    /// </summary>
    private static bool ResolveStealBreakReturn(
        BaseState bases, Player runner, int baseNo, HomePlayContext liner,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv, ref int extraOuts, double? extraLeadM = null)
    {
        var catchAt = liner.Situation.BallFieldedAtSeconds;
        var basePoint = baseNo == 2 ? liner.Field.SecondBase : liner.Field.ThirdBase;
        var catchToBaseM = (basePoint - liner.Situation.BallFieldedPoint).Length;
        // 盗塁で走っていた走者は投球中のリードが乗る。フライを落ちると読んで走った走者（#1130）は 0。
        var extraLead = extraLeadM ?? coeff.StealBreakExtraLeadM;
        var arm = liner.Situation.OutfielderThrowSpeedMps;
        var fielding = liner.Situation.OutfielderFielding;
        var throwArrive = catchAt + DoubledOffResolver.DefenseReturnSeconds(catchToBaseM, arm, coeff, fielding);
        var leadM = DoubledOffResolver.LeadGainedM(catchAt, coeff, extraLead);
        var maxLeadM = DoubledOffResolver.MaxLeadM(catchAt, coeff, extraLead);
        var margin = DoubledOffResolver.Margin(runner, catchAt, catchToBaseM, arm, coeff, fielding, extraLead);
        var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
        var doubledOff = DoubledOffResolver.Resolve(runner, catchAt, catchToBaseM, arm, coeff, rng, fielding, extraLead,
            liner.Situation.ThrowerThrowAccuracy, out var scatter) == DoubledOffResult.DoubledOff;
        mv?.Add(new RunnerMove(runner, baseNo, baseNo, Out: doubledOff)
            { CloseCall = closeCall, DoubledOffThrowArriveSeconds = throwArrive, DoubledOffLeadM = leadM, DoubledOffMaxLeadM = maxLeadM, ThrowScatter = scatter });
        if (!doubledOff) return false;
        extraOuts++;
        if (baseNo == 2) bases.Second = null; else bases.Third = null;
        return true;
    }

    /// <summary>
    /// ハーフウェーで待った走者が、フライを捕られて元の塁へ戻る勝負（issue #1130）。距離は捕球の時点で走者が
    /// 実際に居る位置（<see cref="FlyReadModel.LeadAtCatchM"/>）。ハーフウェーは「捕られても戻れる距離」なので
    /// 大半は勝負にならない＝戻れる見込みが確実（1−<see cref="BaserunningCoefficients.FlyReadCertainProbability"/> 以上）
    /// なら乱数を引かず帰塁（送球もしない）。際どいときだけ戻し送球との勝負を引く。
    /// 返り値は憤死したか。どちらでも「ハーフウェーから戻った」絵のための move を積む。
    /// </summary>
    private static bool ResolveHalfwayReturn(
        BaseState bases, Player runner, int baseNo, double holdLeadM, HomePlayContext fly,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv, ref int extraOuts,
        bool canContest, ref bool returnThrowUsed)
    {
        var catchAt = fly.Situation.BallFieldedAtSeconds;
        var basePoint = baseNo switch { 1 => fly.Field.FirstBase, 2 => fly.Field.SecondBase, _ => fly.Field.ThirdBase };
        var catchToBaseM = (basePoint - fly.Situation.BallFieldedPoint).Length;
        var arm = fly.Situation.OutfielderThrowSpeedMps;
        var fielding = fly.Situation.OutfielderFielding;
        var leadM = FlyReadModel.LeadAtCatchM(runner, holdLeadM, catchAt, coeff);
        var prob = DoubledOffResolver.SuccessProbabilityFromLead(runner, leadM, catchToBaseM, arm, coeff, fielding);
        // 送球が既に他の走者へ行った・3アウト目が済んだ・確実に戻れる＝勝負にしない（乱数を引かない）。
        if (!canContest || prob >= 1.0 - coeff.FlyReadCertainProbability)
        {
            mv?.Add(new RunnerMove(runner, baseNo, baseNo, Out: false)
                { FlyHoldLeadM = holdLeadM, DoubledOffLeadM = leadM, DoubledOffMaxLeadM = leadM });
            return false;
        }
        returnThrowUsed = true;
        var throwArrive = catchAt + DoubledOffResolver.DefenseReturnSeconds(catchToBaseM, arm, coeff, fielding);
        var margin = DoubledOffResolver.MarginFromLead(runner, leadM, catchToBaseM, arm, coeff, fielding);
        var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
        var doubledOff = DoubledOffResolver.ResolveFromLead(runner, leadM, catchToBaseM, arm, coeff, rng, fielding,
            fly.Situation.ThrowerThrowAccuracy, out var scatter) == DoubledOffResult.DoubledOff;
        mv?.Add(new RunnerMove(runner, baseNo, baseNo, Out: doubledOff)
        {
            CloseCall = closeCall, FlyHoldLeadM = holdLeadM, DoubledOffThrowArriveSeconds = throwArrive,
            DoubledOffLeadM = leadM, DoubledOffMaxLeadM = leadM, ThrowScatter = scatter,
        });
        if (!doubledOff) return false;
        extraOuts++;
        switch (baseNo)
        {
            case 1: bases.First = null; break;
            case 2: bases.Second = null; break;
            default: bases.Third = null; break;
        }
        return true;
    }

    /// <summary>
    /// 犠飛以外のタッチアップ（一塁→二塁 / 二塁→三塁, design-14 P3-11）を物理レースで解く。
    /// 犠飛（三塁→本塁, Issue #90）と<b>同じ機構を目標塁だけ替えて</b>使う＝走者の式は新設しない（不変条件#1）。
    /// 走者は捕球時刻にスタート（二次リードなし＝全塁間を走る）、守備も同じ捕球時刻から送球するので、
    /// 勝負は「塁間を走る時間」対「捕球地点→次塁の送球時間」＝打球の深さ・方向と外野の肩だけが効く。
    /// 浅いフライは送球が短く到達見込みが送り閾値に届かない＝自重（RNG非消費）。
    /// 呼び出し前提: フライ・extraOuts==0・進塁先が空き。
    /// </summary>
    private static void ResolveFlyTagUp(
        BaseState bases, Player runner, int fromBase, HomePlayContext fly,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv,
        ref int extraOuts, bool throwUsed = false)
    {
        var p = fromBase == 2
            ? HomePlayResolver.TagUpThirdParams(fly.Field, coeff)
            : HomePlayResolver.TagUpSecondParams(fly.Field, coeff);
        var prob = HomePlayResolver.TagUpSuccessProbability(runner, fromBase, fly.Situation, fly.Field, coeff, p);
        // 送り閾値はタッチアップ専用（他の送り判定より高い）。静止から出るぶん博打の分が悪く、実物の
        // 三塁コーチも「ほぼ確実な時だけ行かせる」＝低い閾値を流用すると送った試行の3〜4割が走塁死になり、
        // 走者を進めるどころか攻撃を削る（実測で得点が帯の下限割れ）。理由は SendTagUpMinSuccess のコメント。
        if (!HomeSendDecision.ShouldSendTagUp(prob, fly.Aggression, fly.Tactics)) return; // 自重（RNG非消費）
        if (throwUsed)
        {
            // 送球は塁へ戻る走者へ行った（#1130・1球は1か所）＝勝負なしで進む（乱数なし）。
            mv?.Add(new RunnerMove(runner, fromBase, fromBase + 1, false) { TagUpCatchAtSeconds = fly.Situation.BallFieldedAtSeconds });
            if (fromBase == 2) { bases.Third = runner; bases.Second = null; } else { bases.Second = runner; bases.First = null; }
            return;
        }

        var margin = HomePlayResolver.TagUpMargin(runner, fromBase, fly.Situation, fly.Field, coeff, p);
        var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
        // 絵も判定と同じ「捕球を待ってから走る」時計にする（犠飛と同じ扱い）。
        var catchAt = fly.Situation.BallFieldedAtSeconds;
        var toBase = fromBase + 1;
        var race = HomePlayResolver.TagUpResolveRace(runner, fromBase, fly.Situation, fly.Field, coeff, p, rng);
        if (race.Safe)
        {
            mv?.Add(new RunnerMove(runner, fromBase, toBase, false)
                { CloseCall = closeCall, TagUpCatchAtSeconds = catchAt, ThrowScatter = race.Scatter });
            if (toBase == 3) bases.Third = runner; else bases.Second = runner;
        }
        else
        {
            extraOuts++; // タッチアップ失敗＝進塁先での走塁死
            mv?.Add(new RunnerMove(runner, fromBase, toBase, Out: true)
                { CloseCall = closeCall, TagUpCatchAtSeconds = catchAt, ExtraStartDelaySeconds = race.RunnerDelaySeconds, ThrowScatter = race.Scatter });
        }
        if (fromBase == 2) bases.Second = null; else bases.First = null;
    }

    /// <summary>進塁レースの決着（Issue #318）。セーフ=三塁到達／アウト=三塁での走塁死（extraOuts加算）。
    /// CloseCall は本塁・三塁と同じ流儀（|margin| &lt; CloseCallMarginSeconds の純計算・RNG非消費）。</summary>
    private static void ResolveAdvanceRace(
        BaseState bases, Player r2, HomePlayResolver.RaceVerdict race, double margin, double delay,
        BaserunningCoefficients coeff, List<RunnerMove>? mv, ref int extraOuts)
    {
        var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
        if (race.Safe)
        {
            mv?.Add(new RunnerMove(r2, 2, 3, false) { CloseCall = closeCall, ExtraStartDelaySeconds = delay, ThrowScatter = race.Scatter });
            bases.Third = r2;
        }
        else
        {
            extraOuts++; // 三塁での走塁死
            mv?.Add(new RunnerMove(r2, 2, 3, Out: true)
                { CloseCall = closeCall, ExtraStartDelaySeconds = delay + race.RunnerDelaySeconds, ThrowScatter = race.Scatter });
        }
        bases.Second = null;
    }

    /// <summary>どの塁を攻めて併殺を狙ったか（Q31・タイムライン/統計の分岐に使う）。</summary>
    private enum DoublePlayForceTarget { Second, Third, Home }

    /// <summary>
    /// 併殺・野選を解く（Q31でディスパッチャ化）。既定は二塁封殺ルート（<see cref="ResolveDoublePlayAtSecond"/>、
    /// 従来どおり1バイトも変えない）。代替ゲートが成立するときだけ、対象走者の到達見込みを二塁封殺と
    /// 比較し、代替のほうが守備に有利（生存確率が低い）なら代替（<see cref="ResolveDoublePlaySelfForce"/>／
    /// <see cref="ResolveDoublePlayFirstBaseTag"/>）を選ぶ:
    /// ①三塁手が自打球を処理＋二塁走者あり → 二塁走者を三塁で自己フォース（送球なし・中継なしで一塁へ転送）／
    /// ②捕手/投手が自打球を処理＋三塁走者あり（満塁）→ 三塁走者を本塁で自己フォース／
    /// ③一塁手が自打球を処理 → 打者を一塁で自己フォース（送球なし）→ 二塁へ送球して一塁走者にタッチ
    /// （issue 3-6-tag-double-play。二塁はフォースが外れるのでタッチプレー）／
    /// ④遊撃手/二塁手が自打球を処理 → 一塁走者を二塁で自己フォース（issue #1549・6-6-3/4-4-3）。①②③と違い
    /// 対象走者・対象塁は標準ルートと同じ一塁走者・二塁のまま＝「送球されたボールを受けてタッチ」か
    /// 「処理した本人が走り込んで踏む」かという<b>届き方だけ</b>が変わる比較になる（タッチの物理
    /// （<see cref="HomePlayResolver.SecondForceParams"/>）は届き方に関わらず同じなので新しい係数は増やさない）。
    /// ゲート不成立（大多数のケース）では <c>home.FielderRole</c> を1回比較するだけで即座に二塁封殺へ落ちる＝
    /// 一塁走者のみ在塁シナリオの RNG 消費順・挙動は完全不変（帯不変の根拠）。
    /// </summary>
    /// <returns>(追加アウト数, 打者が野選で一塁生存したか, どの塁を攻めたか, 一塁走者の 1→2 の move を積んだか＝
    /// 配置は後段の一塁走者カスケードに任せる, 二塁のタッチアウトの接触基準の時刻[s]＝一塁の自己フォース→二塁タッチで
    /// 一塁走者を刺したときだけ。それが3アウト目なら先に本塁へ着いた走者の得点を数える時間の勝負に使う)。</returns>
    private static (int ExtraOuts, bool BatterSafeOnFc, DoublePlayForceTarget Target, bool R1AdvanceRecorded, double? TagOutAtSeconds) ResolveDoublePlay(
        BaseState bases, Player batter, Player r1, Player? r2, Player? r3, HomePlayContext home,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv,
        double r1Delay, double r2Delay, double r3Delay)
    {
        var thirdCandidate = r2 is not null && home.FielderRole == FieldPosition.ThirdBase;
        // 本塁のフォースは満塁（一・二・三塁とも占有）でなければ成立しない（ApplyRunnerAdvance の r3Forced と同じ定義）。
        // 二塁が空いていれば三塁走者は押し出されない＝フォースでないので自己フォースの対象外。
        var homeCandidate = r2 is not null && r3 is not null && home.FielderRole is FieldPosition.Catcher or FieldPosition.Pitcher;
        // 満塁・内野前進は「本塁で封殺して1点を防ぐ」が本筋（ApplyInPlayOut の r3Forced×In 分岐）。併殺を狙わない
        // ときに二塁・三塁の封殺だけを取ると、その本塁封殺の判定が飛ばされる＝先の走者だけの封殺（A-3・issue #797）は
        // 本塁を踏むとき以外は使わない。
        var homeFirst = r2 is not null && r3 is not null && home.InfieldDepth == Tactics.DefenseDepth.In;
        // 一塁手が自打球を処理＝一塁の自己フォースが検討できる（issue 3-6-tag-double-play）。r2/r3の在塁は問わない
        // （対象は常に一塁走者＝標準の二塁封殺ルートと同じ対象走者）。FielderRole は単一値なので
        // thirdCandidate/homeCandidate とは排他（同時に立たない）。
        // ⚠ 本塁が本筋の場面（homeFirst）では一塁の自己フォース→二塁タッチを選ばない（PR #1076 レビュー）。
        // タッチ失敗（追加アウト0）のあとに本塁封殺の勝負がもう一度走り、「一塁を踏んでアウトにした打者が
        // 本塁封殺の野選で一塁に生きる」ような二重の守備になる。
        // 一・三塁（二塁は空き）でも前進守備なら本塁の勝負が続く＝同じ穴（二塁のタッチ失敗のあとにもう1本投げる）を塞ぐ。
        var firstBaseCandidate = home.FielderRole == FieldPosition.FirstBase && !homeFirst
            && !(r3 is not null && home.InfieldDepth == Tactics.DefenseDepth.In);
        // 二塁の自己フォース候補（issue #1549・6-6-3/4-4-3）: 遊撃手/二塁手が自打球を処理＝自分で二塁を踏んで
        // 一塁へ投げられるかを検討してよい。対象は標準ルートと同じ一塁走者(r1)自身＝r2/r3の在塁は問わない
        // （①②のように対象走者が変わる代替ではない）。FielderRole は単一値なので他の候補とは排他。
        var secondSelfForceCandidate = home.FielderRole is FieldPosition.Shortstop or FieldPosition.SecondBase;

        if (!thirdCandidate && !homeCandidate && !firstBaseCandidate && !secondSelfForceCandidate)
        {
            var (eo, fc) = ResolveDoublePlayAtSecond(bases, batter, r1, home, coeff, rng, mv, r1Delay, allowForceOnly: !homeFirst);
            return (eo, fc, DoublePlayForceTarget.Second, false, null);
        }

        var force2nd = HomePlayResolver.SecondForceParams(home.Field, coeff);
        var pR1Safe2nd = HomePlayResolver.SuccessProbability(r1, 1, home.Situation, home.Field, coeff, force2nd, r1Delay);

        if (secondSelfForceCandidate)
        {
            // 自分で二塁を踏む一段目は送球でなく自分の脚（issue #1273と同じ流儀）。対象塁に到達した時点で
            // 捕球済みとみなした HomePlaySituation に握り替えて、標準ルートと同じ force2nd（SecondForceParams）
            // で比較する＝タッチの物理は届き方に関わらず同じなので係数は共用（不変条件#1）。
            var arriveAtSecondSelf = SelfForceArriveAtBaseSeconds(home, force2nd.TargetPoint, coeff);
            var atSecondSelf = home.Situation with
            {
                BallFieldedPoint = force2nd.TargetPoint, BallFieldedAtSeconds = arriveAtSecondSelf,
            };
            var pR1SafeSelf = HomePlayResolver.SuccessProbability(
                r1, 1, atSecondSelf, home.Field, coeff, force2nd, r1Delay, transferSecondsOverride: 0.0);
            if (pR1SafeSelf < pR1Safe2nd)
            {
                var (eo, fc) = ResolveDoublePlaySelfForce(
                    bases, batter, leadRunner: r1, leadFromBase: 1, home, force2nd, r1Delay, coeff, rng, mv,
                    allowForceOnly: !homeFirst);
                return (eo, fc, DoublePlayForceTarget.Second, false, null);
            }
        }

        if (firstBaseCandidate)
        {
            // 一塁手が一塁を自己フォースしてから二塁へ送球してタッチ（issue 3-6-tag-double-play）。
            // 対象は三塁手/本塁の代替と同じ一塁走者＝一塁走者の二塁到達見込みを二塁封殺ルートと比較する。
            // 一段目（一塁手自身が一塁へ走り込む）は送球でなく自分の脚（issue #1273）＝到達時刻を
            // SelfForceArriveAtBaseSeconds で求め、以降（二塁への送球）はそこから時計を進める。
            var arriveAtFirstSelf = SelfForceArriveAtBaseSeconds(home, home.Field.FirstBase, coeff);
            var selfForceSituation = new HomePlaySituation(
                home.Field.FirstBase, arriveAtFirstSelf, home.Situation.OutfielderThrowSpeedMps,
                home.Situation.OutfielderFielding);
            var secondTag = HomePlayResolver.SecondTagParams(home.Field, coeff);
            var pR1SafeTag = HomePlayResolver.SuccessProbability(
                r1, 1, selfForceSituation, home.Field, coeff, secondTag, r1Delay,
                transferSecondsOverride: coeff.DoublePlaySelfForceRelayTransferSeconds);
            if (pR1SafeTag < pR1Safe2nd)
            {
                var (eo, fc, recorded, tagOutAt) = ResolveDoublePlayFirstBaseTag(bases, batter, r1, home, coeff, rng, mv, r1Delay);
                return (eo, fc, DoublePlayForceTarget.Second, recorded, tagOutAt);
            }
        }

        if (thirdCandidate)
        {
            var force3rd = HomePlayResolver.ThirdForceParams(home.Field, coeff);
            // 三塁手自身が三塁へ走り込む一段目は送球でなく自分の脚（issue #1273）。「対象塁で捕球済み」とみなした
            // HomePlaySituation に握り替えて既存の式へ渡す（新しい走者物理式は書かない＝不変条件#1）。
            var atThird = home.Situation with
            {
                BallFieldedPoint = force3rd.TargetPoint,
                BallFieldedAtSeconds = SelfForceArriveAtBaseSeconds(home, force3rd.TargetPoint, coeff),
            };
            var pR2Safe3rd = HomePlayResolver.SuccessProbability(
                r2!, 2, atThird, home.Field, coeff, force3rd, r2Delay, transferSecondsOverride: 0.0);
            if (pR2Safe3rd < pR1Safe2nd)
            {
                var (eo, fc) = ResolveDoublePlaySelfForce(
                    bases, batter, leadRunner: r2!, leadFromBase: 2, home, force3rd, r2Delay, coeff, rng, mv,
                    allowForceOnly: !homeFirst);
                return (eo, fc, DoublePlayForceTarget.Third, false, null);
            }
        }
        if (homeCandidate)
        {
            var forceHome = HomePlayResolver.HomeForceParams(coeff);
            // 捕手/投手自身が本塁へ走り込む一段目も同じく自分の脚（issue #1273）。
            var atHome = home.Situation with
            {
                BallFieldedPoint = forceHome.TargetPoint,
                BallFieldedAtSeconds = SelfForceArriveAtBaseSeconds(home, forceHome.TargetPoint, coeff),
            };
            var pR3SafeHome = HomePlayResolver.SuccessProbability(
                r3!, 3, atHome, home.Field, coeff, forceHome, r3Delay, transferSecondsOverride: 0.0);
            if (pR3SafeHome < pR1Safe2nd)
            {
                var (eo, fc) = ResolveDoublePlaySelfForce(
                    bases, batter, leadRunner: r3!, leadFromBase: 3, home, forceHome, r3Delay, coeff, rng, mv);
                return (eo, fc, DoublePlayForceTarget.Home, false, null);
            }
        }

        var (eo2, fc2) = ResolveDoublePlayAtSecond(bases, batter, r1, home, coeff, rng, mv, r1Delay, allowForceOnly: !homeFirst);
        return (eo2, fc2, DoublePlayForceTarget.Second, false, null);
    }

    /// <summary>
    /// 本封→一封の転送で打者が一塁に生きる確率（issue #741）。処理点→本塁の送球到達を起点に、捕手が本塁を
    /// 踏んで一塁へ投げる（握り替えは自己フォースの転送と同じ <see cref="BaserunningCoefficients.DoublePlaySelfForceRelayTransferSeconds"/>・
    /// 球速は内野の共通値 <see cref="BaserunningCoefficients.RelayThrowSpeedMps"/>）。打者の一塁到達は守備解決の正典値。
    /// 秒と確率の作りは併殺の二段目（<see cref="ResolveDoublePlaySelfForce"/>）と同じ。絵（TimelineBuilder）も
    /// <see cref="HomeForceRelayArriveSeconds"/> で同じ秒を使う。
    /// </summary>
    public static double HomeForceRelayBatterSafeProbability(HomePlayContext home, BaserunningCoefficients coeff,
        double throwNoiseSeconds = 0.0)
    {
        var margin = HomeForceRelayArriveSeconds(home.Situation, home.Field, coeff)
                     - home.BatterToFirstSeconds!.Value + coeff.DoublePlayRelayBias;
        return RelayBatterSafeProbability(margin + throwNoiseSeconds, coeff);
    }

    /// <summary>一塁への転送と打者の勝負の確率（margin＝守備所要−打者所要＋バイアス。正＝打者生存）。
    /// 併殺の二段目・自己封殺→一塁・本封→一封が共有する式。</summary>
    private static double RelayBatterSafeProbability(double batterMargin, BaserunningCoefficients coeff)
        => MathUtil.Clamp(MathUtil.Logistic(batterMargin / coeff.DoublePlayRelayMarginScale), 0.01, 0.99);

    /// <summary>本封→一封の転送が一塁に着いて打者に触れる時刻[s]（接触基準・issue #741）。判定と絵の共通の秒。</summary>
    public static double HomeForceRelayArriveSeconds(
        HomePlaySituation situation, FieldGeometry field, BaserunningCoefficients coeff)
    {
        var plate = new Vector3D(0, 0, 0);
        var toHome = HomePlayResolver.ResolveThrowPath(situation, plate, coeff);
        var catcher = new HomePlaySituation(plate, toHome.ArriveAtSeconds, coeff.RelayThrowSpeedMps, 50);
        return HomePlayResolver.ResolveThrowPath(catcher, field.FirstBase, coeff,
                   transferSecondsOverride: coeff.DoublePlaySelfForceRelayTransferSeconds).ArriveAtSeconds
               + coeff.SecondForceTagSeconds;
    }

    /// <summary>
    /// 自己フォース一段目（Q31・issue #1273）: 処理野手自身が対象塁へ走り込んで到達する時刻[s]。
    /// <see cref="HomePlayResolver.ResolveThrowPath"/> を肩（<see cref="HomePlaySituation.OutfielderThrowSpeedMps"/>）で
    /// 呼ぶと「自分へ送球する」計算になり塁までの移動がほぼゼロ秒になる（旧実装の不具合）＝ここは送球でなく
    /// 自分の脚（<see cref="HomePlayContext.SelfForceSprintSpeedMps"/>、null なら
    /// <see cref="BaserunningCoefficients.SelfForceFielderSprintSpeedMps"/>）で処理点→対象塁の距離を走る。
    /// <see cref="ResolveDoublePlay"/> のゲート比較と <see cref="ResolveDoublePlaySelfForce"/>／
    /// <see cref="ResolveDoublePlayFirstBaseTag"/> が同じ値を使う単一ソース。
    /// </summary>
    private static double SelfForceArriveAtBaseSeconds(
        HomePlayContext home, Vector3D targetPoint, BaserunningCoefficients coeff)
    {
        var sprintSpeedMps = home.SelfForceSprintSpeedMps ?? coeff.SelfForceFielderSprintSpeedMps;
        var runSeconds = SelfForceRunSeconds((home.Situation.BallFieldedPoint - targetPoint).Length, sprintSpeedMps, coeff);
        return home.Situation.BallFieldedAtSeconds + runSeconds;
    }

    /// <summary>
    /// 自己フォース一段目で、止まった野手が <paramref name="distanceM"/> を走り切る秒（issue #1549）。
    /// 最高速 <paramref name="sprintSpeedMps"/> まで <see cref="BaserunningCoefficients.SelfForceFielderAccelMps2"/> で
    /// 等加速し、あとは最高速＝短い距離は加速の途中で着く。<b>判定と絵（TimelineBuilder）の単一ソース</b>。
    /// </summary>
    internal static double SelfForceRunSeconds(double distanceM, double sprintSpeedMps, BaserunningCoefficients coeff)
    {
        var v = Math.Max(0.1, sprintSpeedMps);
        var a = coeff.SelfForceFielderAccelMps2;
        if (a <= 0 || distanceM <= 0) return Math.Max(0, distanceM) / v;
        var accelDistM = v * v / (2 * a);
        return distanceM < accelDistM ? Math.Sqrt(2 * distanceM / a) : distanceM / v + v / (2 * a);
    }

    /// <summary>
    /// 併殺の代替一段目（Q31／issue #1549）: 三塁手/捕手/投手/遊撃手/二塁手が自打球を自分でベースへ運んで
    /// 封殺（送球なし）→ そのまま自分の肩で一塁へ転送する（中継〈ピボット〉なし＝1回投げのみ）。ゲート
    /// （<c>home.FielderRole</c> が実際にその塁を処理した野手であること）は呼び出し元
    /// （<see cref="ResolveDoublePlay"/>）が既に確認済み。分岐構造は <see cref="ResolveDoublePlayAtSecond"/>
    /// と同型（A-1/A-2は RNG非消費、成立時のみ1回消費）だが、二段目の起点・転送秒・タッチ秒は
    /// 対象塁（二塁/三塁/本塁）固有の係数を使う。<paramref name="leadFromBase"/>=1（二塁の自己フォース）
    /// だけは対象走者(leadRunner)が標準の二塁封殺ルートと同じ r1 自身になる＝①②（leadFromBase 2/3）と違い
    /// r1 はこの関数の中で完結して始末する（下の bases.First クリアを leadFromBase==1 にも広げる必要がある）。
    /// </summary>
    private static (int ExtraOuts, bool BatterSafeOnFc) ResolveDoublePlaySelfForce(
        BaseState bases, Player batter, Player leadRunner, int leadFromBase, HomePlayContext home,
        HomePlayResolver.BasePlayParams force, double delay,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv, bool allowForceOnly = true)
    {
        // 一段目は送球でなく処理野手自身が対象塁へ走り込む動き（issue #1273）。「対象塁に到達した時点で
        // 捕球済み」とみなした HomePlaySituation に握り替えて既存の式へ渡す（新しい走者物理式は書かない＝
        // 不変条件#1）。transferSecondsOverride: 0.0 は従来どおり＝距離0（atBase.BallFieldedPoint==force.TargetPoint）
        // なので握り替え秒を上乗せしない。
        var arriveAtBaseSeconds = SelfForceArriveAtBaseSeconds(home, force.TargetPoint, coeff);
        var atBase = home.Situation with { BallFieldedPoint = force.TargetPoint, BallFieldedAtSeconds = arriveAtBaseSeconds };

        // A-1) 自己フォース自体が間に合わない（現実にはほぼ起きない＝呼び出し元の比較で既に二塁封殺より
        // 有利と分かっているが、絶対的な間に合わなさは独立に見る）＝守備は諦め一塁を取る。RNG非消費。
        var pLeadSafe = HomePlayResolver.SuccessProbability(
            leadRunner, leadFromBase, atBase, home.Field, coeff, force, delay, transferSecondsOverride: 0.0);
        if (pLeadSafe > coeff.DoublePlayForceMaxRunnerSuccess) return (0, false);

        // 二段目: 自己フォースした野手自身がそのまま一塁へ転送する（中継なし＝ピボット無し。issue⑦のような
        // 「二塁カバーに実際に入った野手」探しは不要＝この野手自身が投げ手）。起点は処理点でなく対象塁
        // （force.TargetPoint）、時刻は対象塁へ走り込んだ時刻（arriveAtBaseSeconds。旧実装はフォースした瞬間
        // =BallFieldedAtSeconds を使い、塁への移動時間が抜けていた＝issue #1273）。握り替えは自己フォース専用の
        // DoublePlaySelfForceRelayTransferSeconds＝中継を受けてから投げ返す DoublePlayPivotTransferSeconds とは別）。
        var selfForceSituation = new HomePlaySituation(
            force.TargetPoint, arriveAtBaseSeconds, home.Situation.OutfielderThrowSpeedMps,
            home.Situation.OutfielderFielding);
        var relayToFirst = HomePlayResolver
            .ResolveThrowPath(selfForceSituation, home.Field.FirstBase, coeff,
                transferSecondsOverride: coeff.DoublePlaySelfForceRelayTransferSeconds)
            .ArriveAtSeconds + coeff.SecondForceTagSeconds; // 一塁のタッチは二塁封殺ルートと同軸
        var batterMargin = relayToFirst - home.BatterToFirstSeconds!.Value + coeff.DoublePlayRelayBias;
        var pBatterSafe = MathUtil.Clamp(
            MathUtil.Logistic(batterMargin / coeff.DoublePlayRelayMarginScale), 0.01, 0.99);

        // A-2) この代替の一塁転送が薄い＝併殺は仕掛けない（二塁封殺への再挑戦はしない＝v1の単純化。代替は
        // 二塁より生存見込みが低いと分かった上で選ばれているため、二塁を試しても大差ない見込み）。RNG非消費。
        // ただし先の走者の封殺が十分間に合うなら、封殺だけ取って打者を一塁に残す（issue #797）。
        if (1.0 - pBatterSafe < coeff.DoublePlayAttemptMinRelaySuccess)
        {
            if (!allowForceOnly || pLeadSafe > coeff.DoublePlayForceOnlyMaxRunnerSuccess) return (0, false);
            var onlyMargin = HomePlayResolver.Margin(
                leadRunner, leadFromBase, atBase, home.Field, coeff, force, delay, transferSecondsOverride: 0.0);
            mv?.Add(new RunnerMove(leadRunner, leadFromBase, force.TargetBase, Out: true)
            {
                CloseCall = Math.Abs(onlyMargin) < coeff.CloseCallMarginSeconds, Force = true,
                SelfForceDoublePlayLead = true, ForceOnly = true,
            });
            // leadFromBase==1（issue #1549・二塁の自己フォース）は leadRunner が r1 自身＝退いた一塁を
            // 自分で片付ける（①②の二塁/三塁はそのまま、leadRunner=r2/r3 なので一塁には触れない）。
            if (leadFromBase == 1) bases.First = null;
            else if (leadFromBase == 2) bases.Second = null;
            else bases.Third = null;
            bases.First = batter;
            mv?.Add(new RunnerMove(batter, 0, 1, false));
            return (1, true);
        }

        // 自己フォースは成立（対象走者アウト）。際どさは表示のみ（RNG非消費）。
        var leadMargin = HomePlayResolver.Margin(
            leadRunner, leadFromBase, atBase, home.Field, coeff, force, delay, transferSecondsOverride: 0.0);
        var leadClose = Math.Abs(leadMargin) < coeff.CloseCallMarginSeconds;
        // 一塁への転送1本の逸れ（issue #859）: その本の遅れと期待値の差を転送の秒へ足して勝負を引く（ゼロ平均）。
        // 投げ手は自己封殺した処理野手自身。
        var (relayScatter, relayNoise) = HomePlayResolver.SampleThrowScatter(home.Situation.ThrowerThrowAccuracy, coeff, rng);
        mv?.Add(new RunnerMove(leadRunner, leadFromBase, force.TargetBase, Out: true)
            { CloseCall = leadClose, Force = true, SelfForceDoublePlayLead = true, RelayThrowScatter = relayScatter });
        // leadFromBase==1（issue #1549・二塁の自己フォース）は leadRunner が r1 自身＝退いた一塁をここで
        // クリアする（下の bases.First = batter で打者がすぐ上書きする）。①②（leadFromBase 2/3）は
        // leadRunner が r2/r3 で一塁には触れない＝従来どおり Second/Third だけクリア。
        if (leadFromBase == 1) bases.First = null;
        else if (leadFromBase == 2) bases.Second = null;
        else bases.Third = null;

        if (MathUtil.Chance(RelayBatterSafeProbability(batterMargin + relayNoise, coeff), rng))
        {
            // B) 野選: 代替を狙ったが打者が転送を上回って一塁生存。①②（leadFromBase 2/3）は対象走者(r2/r3)が
            // 本塁封殺と違い r1 自身ではないので r1 は生存し続け、この先の一塁走者カスケード（ApplyInPlayOut）が
            // 「r1 は一塁から出た」として bases.First の後始末を走らせる＝ここで打者を置いても、
            // その後始末が対象を見ずに上書きすると打者が消える（2026-08-27実測: 走者保存則違反で発覚）。
            // 後始末側を「まだ r1 自身が居るときだけクリアする」よう絞ったので、ここでは通常どおり
            // 即座に置いてよい（ApplyWalk の bases.First=batter 直書き注意と同種の罠）。
            // leadFromBase==1（二塁の自己フォース）は r1 自身が対象走者＝カスケードを待たず、上の
            // bases.First=null に続けてここで直接 batter を置けば完結する（二重の後始末は無い）。
            bases.First = batter;
            mv?.Add(new RunnerMove(batter, 0, 1, false));
            return (1, true);
        }
        // C) 併殺: 打者も一塁でアウト（打者アウトは呼び出し側の battersOwnOut=1 が計上）。
        return (1, false);
    }

    /// <summary>
    /// 併殺の代替（issue 3-6-tag-double-play）: 一塁手が自打球を自分で一塁ベースへ運んで打者をフォースアウト
    /// （送球なし）にし、そのまま二塁へ送球して一塁走者にタッチを狙う。一塁を先に踏むと二塁のフォースが外れる
    /// ため、二塁は<b>タッチプレー</b>（<see cref="RunnerMove.Force"/>=false）になる点が標準の二塁封殺ルート
    /// （<see cref="ResolveDoublePlayAtSecond"/>）と違う。構造は <see cref="ResolveDoublePlaySelfForce"/> の
    /// 鏡像（自己フォースの対象が走者でなく打者、転送の宛先が一塁でなく二塁）だが、打者の自己フォース出塁は
    /// 呼び出し元（<see cref="ApplyInPlayOut"/> の既定 battersOwnOut=1）に委ねるため、ここでは一塁走者の
    /// 帰趨（二塁でアウト or セーフ）だけを確定させれば済み、A-2相当の1分岐で足りる（B/Cの野選分岐は無い＝
    /// 打者は常にアウトのまま、セーフになるのは常に一塁走者）。
    /// </summary>
    /// <returns>(追加アウト数, 打者が野選で一塁生存したか＝この代替では常にfalse, 一塁走者の 1→2 の move を積んだか, 二塁のタッチアウトの時刻[s]＝刺したときだけ)。</returns>
    private static (int ExtraOuts, bool BatterSafeOnFc, bool R1AdvanceRecorded, double? TagOutAtSeconds) ResolveDoublePlayFirstBaseTag(
        BaseState bases, Player batter, Player r1, HomePlayContext home,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv, double delay)
    {
        // A-1) 自己フォース自体（一塁手が一塁を踏む）が間に合わない＝この代替は選ばれない（呼び出し元の比較で
        // 既に二塁封殺より有利と分かっているが、絶対的な間に合わなさは独立に見る。既存の代替と同じ流儀）。
        // 打者はまだ走者になっていないため、走者物理（RunnerClock）でなく打者の正典値
        // （home.BatterToFirstSeconds）を使う＝不変条件#1（新しい走者の式を書かない）に合わせ、既存の
        // RunnerTimeSeconds 経路は使わない。RNG非消費。
        // 一塁手自身が一塁へ走り込む一段目は送球でなく自分の脚（issue #1273）＝到達時刻は
        // SelfForceArriveAtBaseSeconds（旧実装は捕球の瞬間をそのまま使い、塁への移動時間が抜けていた）。
        var firstForce = HomePlayResolver.FirstForceParams(home.Field, coeff);
        var arriveAtBaseSeconds = SelfForceArriveAtBaseSeconds(home, firstForce.TargetPoint, coeff);
        var selfForceMargin = arriveAtBaseSeconds + firstForce.TagSeconds
            - home.BatterToFirstSeconds!.Value + firstForce.SuccessBias;
        var pBatterSafeSelfForce = MathUtil.Clamp(
            MathUtil.Logistic(selfForceMargin / firstForce.MarginScale), 0.01, 0.99);
        if (pBatterSafeSelfForce > coeff.DoublePlayForceMaxRunnerSuccess) return (0, false, false, null);

        // 二段目: 一塁を踏んだ一塁手自身がそのまま二塁へ送球してタッチ（中継なし）。起点は一塁ベース、
        // 時刻は一塁へ走り込んだ時刻（arriveAtBaseSeconds。旧実装は捕球の瞬間=BallFieldedAtSeconds を使い、
        // 自己フォースの移動時間を無視していた＝issue #1273）。
        var selfForceSituation = new HomePlaySituation(
            home.Field.FirstBase, arriveAtBaseSeconds, home.Situation.OutfielderThrowSpeedMps,
            home.Situation.OutfielderFielding);
        var secondTag = HomePlayResolver.SecondTagParams(home.Field, coeff);
        var pR1Safe = HomePlayResolver.SuccessProbability(
            r1, 1, selfForceSituation, home.Field, coeff, secondTag, delay,
            transferSecondsOverride: coeff.DoublePlaySelfForceRelayTransferSeconds);

        // A-2相当) 二塁のタッチの見込みが薄い＝守備は投げない。打者は単独アウト（既定の battersOwnOut=1 の
        // まま）・一塁走者は普通に二塁へ進む（この先の汎用カスケードに委ねる）。RNG非消費。
        if (1.0 - pR1Safe < coeff.DoublePlayAttemptMinRelaySuccess) return (0, false, false, null);

        // 送球を実際に投げる＝一塁走者のアウト/セーフはここで確定させる（セーフ時の塁への配置だけは汎用カスケードに任せる）。
        var r1Margin = HomePlayResolver.Margin(
            r1, 1, selfForceSituation, home.Field, coeff, secondTag, delay,
            transferSecondsOverride: coeff.DoublePlaySelfForceRelayTransferSeconds);
        var r1Close = Math.Abs(r1Margin) < coeff.CloseCallMarginSeconds;
        var (relayScatter, relayNoise) = HomePlayResolver.SampleThrowScatter(home.Situation.ThrowerThrowAccuracy, coeff, rng);
        // 勝負の抽選はクランプしない（design-12 §3.9・issue #801）。1%/99% で切ると、大きく先着した走者がアウトになる
        // 場面を秒で説明できない。
        var pR1SafeWithNoise = MathUtil.Logistic((r1Margin + relayNoise) / secondTag.MarginScale);
        bases.First = null; // 打者は一塁手の自己フォースでアウト＝一塁走者は必ず一塁を去る

        if (MathUtil.Chance(pR1SafeWithNoise, rng))
        {
            // 一塁走者セーフ（タッチ失敗）: 打者は単独アウトのまま（併殺不成立）・一塁走者は二塁へ安全進塁。
            // ⚠ ここで bases.Second に置かない（PR #1076 レビュー）。二塁走者がいると、その占有を上書きして消し、
            // 後段の二塁走者カスケード（押し出しで三塁へ・bases.Second=null）が置いた r1 をまた外し、
            // 一塁走者カスケードが 1→2 をもう1本積む（move の二重）。満塁×内野前進で本塁返球が逸れると
            // PushRunnersOnWildThrow が上書きされた r1 を三塁へ押して二塁走者が消える（走者保存則違反）。
            // 配置は一塁走者カスケードに任せる（二塁走者が空けた後に置く）。move（絵の材料）はここで積み、
            // カスケードには「積み済み」を返して重ねさせない。
            mv?.Add(new RunnerMove(r1, 1, 2, Out: false)
                { CloseCall = r1Close, FirstBaseSelfForceTag = true, RelayThrowScatter = relayScatter });
            return (0, false, true, null);
        }
        // 併殺成立: 一塁走者も二塁でタッチアウト（二塁はフォースでないので Force=false）。
        mv?.Add(new RunnerMove(r1, 1, 2, Out: true)
            { CloseCall = r1Close, FirstBaseSelfForceTag = true, RelayThrowScatter = relayScatter });
        // 二塁のタッチの時刻（接触基準）。これが3アウト目なら、先に本塁へ着いた走者の得点が認められる（時間の勝負）。
        var tagOutAtSeconds = HomePlayResolver.DefenseTimeSeconds(
            selfForceSituation, secondTag.TargetPoint, secondTag.TagSeconds, coeff,
            coeff.DoublePlaySelfForceRelayTransferSeconds) + relayNoise;
        return (1, false, false, tagOutAtSeconds);
    }

    /// <summary>
    /// 併殺・野選を「捕球点→二塁の封殺（P_force2）」と「二塁→一塁の転送」の二段レースで解く
    /// （Issue #369, 設計書12 §2）。#318 の <see cref="ResolveGrounderAdvance"/> と同型で、守備は幾何だけで
    /// 3分岐を純関数で選ぶ:
    /// A) 一塁のみ（打者を一塁でアウト・一塁走者はフォースで二塁へ）＝呼び出し側の battersOwnOut に委ねて何もしない。
    ///    2条件のいずれかで選ぶ（どちらも RNG非消費）: A-1 二塁封殺自体が間に合わない（一塁走者の二塁到達見込み
    ///    &gt; DoublePlayForceMaxRunnerSuccess＝俊足走者/深い右方向ゴロ）／ A-2 併殺の見込みが薄い（転送で打者を
    ///    刺せる確率 &lt; DoublePlayAttemptMinRelaySuccess＝快足打者/深いゴロ＝ルーティンなゴロ凡打）／
    /// B) 二塁は封殺できるが転送が打者に僅かに間に合わない → 野選（二塁封殺＋打者一塁生存, RNG1消費）／
    /// C) 二塁も一塁も取れる → 併殺（RNG1消費）。
    /// 走者所要は本塁・三塁レースと同じ <see cref="HomePlayResolver"/> 経由（新しい走者の式を書かない, 不変条件#1/#2）。
    /// 二塁封殺成立時（B/C）は bases.First と mv を本メソッドが確定させる。分岐Aは一切変更せず返す。
    /// 呼び出し前提: 一塁走者あり・ゴロ・home.BatterToFirstSeconds 非null。
    /// </summary>
    /// <returns>(追加アウト数, 打者が野選で一塁生存したか)。</returns>
    private static (int ExtraOuts, bool BatterSafeOnFc) ResolveDoublePlayAtSecond(
        BaseState bases, Player batter, Player r1, HomePlayContext home,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv, double delay, bool allowForceOnly = true)
    {
        var force = HomePlayResolver.SecondForceParams(home.Field, coeff);
        // 一塁走者の二塁到達見込み（P_r1_safe）。捕球点→二塁の直接送球（幾何）と一塁走者の脚（走力＋走塁判断）から創発。
        var pR1Safe = HomePlayResolver.SuccessProbability(r1, 1, home.Situation, home.Field, coeff, force, delay);

        // A-1) 二塁封殺自体が間に合わない（俊足走者/深い右方向ゴロ等）＝守備は封殺を諦め一塁を取る。RNG非消費。
        if (pR1Safe > coeff.DoublePlayForceMaxRunnerSuccess) return (0, false);

        // 二段目の見積り: 二塁ベース経由の転送（ピボット）で打者を一塁で刺せるか（P_relay_out）。二塁への送球到達
        // 時刻を起点に、二塁手/遊撃手の転送握り替え（DoublePlayPivotTransferSeconds）で一塁へ転送する。
        // 打者走者の一塁到達は守備解決の正典値（home.BatterToFirstSeconds）＝出塁/凡打を決めたのと同じ時計。
        var arriveAtSecond = HomePlayResolver
            .ResolveThrowPath(home.Situation, home.Field.SecondBase, coeff).ArriveAtSeconds;
        var pivotTransfer = coeff.DoublePlayPivotTransferSeconds;
        // 中継球速は二塁カバーに実際に入った野手の地肩（issue⑦。渡されなければ従来のカットマン用
        // 共通定数 RelayThrowSpeedMps へフォールバック＝旧挙動と恒等）。
        var pivotArm = home.DoublePlayPivotArmMps ?? coeff.RelayThrowSpeedMps;
        var pivot = new HomePlaySituation(
            home.Field.SecondBase, arriveAtSecond, pivotArm, home.Situation.OutfielderFielding);
        var relayToFirst = HomePlayResolver
            .ResolveThrowPath(pivot, home.Field.FirstBase, coeff, transferSecondsOverride: pivotTransfer)
            .ArriveAtSeconds + coeff.SecondForceTagSeconds;
        // margin = 守備所要 − 打者所要（正＝打者がベースに先着＝生存＝野選）。
        var batterMargin = relayToFirst - home.BatterToFirstSeconds!.Value + coeff.DoublePlayRelayBias;
        var pBatterSafe = MathUtil.Clamp(
            MathUtil.Logistic(batterMargin / coeff.DoublePlayRelayMarginScale), 0.01, 0.99);

        // A-2) 併殺の見込みが薄い（打者を刺せる確率 P_relay_out が閾値未満）＝守備は二塁へ投げず「確実な一塁アウト」を取る。
        //    ＝ルーティンなゴロ凡打（打者一塁アウト＋一塁走者は二塁へ進塁）。快足打者や深いゴロはここへ落ちる。RNG非消費。
        //    これにより野選（下の B）は「併殺を狙った末に転送が僅かに間に合わなかった残差」に限られる（現実の稀な頻度）。
        if (1.0 - pBatterSafe < coeff.DoublePlayAttemptMinRelaySuccess)
        {
            // A-3) ただし二塁封殺が十分間に合う（一塁走者の二塁到達見込み ≦ DoublePlayForceOnlyMaxRunnerSuccess）なら、
            //    一塁でなく先の走者を二塁で封殺して打者を一塁に残す（issue #797。実物の内野は封殺が取れる限りまず二塁）。
            //    転送はしない＝打者は一塁に生きる（野選には数えない）。RNG非消費。
            if (!allowForceOnly || pR1Safe > coeff.DoublePlayForceOnlyMaxRunnerSuccess) return (0, false);
            var onlyMargin = HomePlayResolver.Margin(r1, 1, home.Situation, home.Field, coeff, force, delay);
            // 封殺は幾何で確定＝判定に使う逸れは無い（乱数を引かない）。絵の逸れは ThrowScatterAssigner が別の流れから付ける。
            mv?.Add(new RunnerMove(r1, 1, 2, Out: true)
                { CloseCall = Math.Abs(onlyMargin) < coeff.CloseCallMarginSeconds, Force = true, ForceOnly = true });
            bases.First = batter;
            mv?.Add(new RunnerMove(batter, 0, 1, false));
            return (1, true);
        }

        // 二塁封殺は成立（一塁走者アウト）。#318 と同様に一段目のアウトは幾何で確定させ、際どさは表示のみ（RNG非消費）。
        var forceMargin = HomePlayResolver.Margin(r1, 1, home.Situation, home.Field, coeff, force, delay);
        var forceClose = Math.Abs(forceMargin) < coeff.CloseCallMarginSeconds;
        // 送球2本の逸れ（issue #859）: 二塁への送球（処理野手）と一塁への転送（ピボット。送球精度は未配線＝50）。
        // 封殺は幾何で確定のまま、両方の遅れと期待値の差を転送の秒へ足して打者との勝負を引く（ゼロ平均）。
        // 二塁への送球が逸れればピボットが遅れる＝一本目の逸れも打者の勝負に効く。
        var (feedScatter, feedNoise) = HomePlayResolver.SampleThrowScatter(home.Situation.ThrowerThrowAccuracy, coeff, rng);
        var (pivotScatter, pivotNoise) = HomePlayResolver.SampleThrowScatter(50, coeff, rng);
        mv?.Add(new RunnerMove(r1, 1, 2, Out: true)
            { CloseCall = forceClose, Force = true, ThrowScatter = feedScatter, RelayThrowScatter = pivotScatter }); // 二塁封殺
        bases.First = null;

        if (MathUtil.Chance(RelayBatterSafeProbability(batterMargin + feedNoise + pivotNoise, coeff), rng))
        {
            // B) 野選: 併殺を狙ったが打者が転送を上回って一塁生存（一塁は封殺した一塁走者が空けた＝衝突しない）。
            bases.First = batter;
            mv?.Add(new RunnerMove(batter, 0, 1, false));
            return (1, true);
        }
        // C) 併殺: 打者も一塁でアウト（打者アウトは呼び出し側の battersOwnOut=1 が計上、
        //    2本目送球の絵は GameEngine の AppendDoublePlayThrows が二塁封殺 RunnerMove から差し替える）。
        return (1, false);
    }
}
