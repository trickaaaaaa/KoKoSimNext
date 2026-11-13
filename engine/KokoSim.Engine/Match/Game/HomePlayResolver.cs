using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>本塁クロスプレーの結果（バックホーム憤死, 設計書12 §3）。</summary>
public enum HomePlayResult
{
    Safe,
    OutAtHome,
}

/// <summary>
/// 本塁への送球の起点情報（外野が打球を処理した点・時刻・肩）。
/// FieldingPlay から派生させて渡す（Slice C で配線）。座標は本塁原点・平面。
/// </summary>
public readonly record struct HomePlaySituation(
    Vector3D BallFieldedPoint,
    double BallFieldedAtSeconds,
    double OutfielderThrowSpeedMps,
    // 処理野手の守備(Fielding)。返球・中継の握り替えを能力で伸縮（Issue #36）。既定50＝現行固定値と恒等。
    int OutfielderFielding = 50,
    // 処理野手の送球精度(ThrowAccuracy)。本塁への悪送球判定（IsWildThrow）専用、既定50＝リーグ平均と恒等。
    int ThrowerThrowAccuracy = 50);

/// <summary>
/// 本塁クロスプレーの解決（設計書12 §3, F2）。盗塁 <see cref="StealResolver"/> と同型の「時間の勝負」。
/// 走者: 残り塁間距離 ÷ スプリント速度(走力) ＋ 判断遅延(走塁判断)。
/// 守備: 外野処理時刻 ＋ 捕球 ＋ (中継 or 直接返球) ＋ タッチ。
/// margin = 守備所要 − 走者所要 → logistic で生還確率。乱数は注入（決定論）。
/// すべて接触（打った瞬間）＝0秒基準。
/// </summary>
public static class HomePlayResolver
{
    /// <summary>
    /// 目標塁ごとのレース係数（本塁 or 三塁, Issue #89）。塁固有はこの3つ（送球先座標・タッチ・
    /// バイアス・幅）だけで、走者反応・二次リード・外野処理/中継の係数は本塁と共有する。
    /// </summary>
    /// <param name="TargetBase">到達を狙う塁（3=三塁, 4=本塁）。走者所要距離の算出に使う。</param>
    /// <param name="TargetPoint">送球先ベースの平面座標（本塁=原点, 三塁=<see cref="FieldGeometry.ThirdBase"/>）。</param>
    /// <param name="TagSeconds">その塁でのタッチ[s]。</param>
    /// <param name="SuccessBias">到達確率バイアス[s]（走者物理の保守性を吸収する校正項）。</param>
    /// <param name="MarginScale">margin→確率の logistic 幅[s]。</param>
    public readonly record struct BasePlayParams(
        int TargetBase, Vector3D TargetPoint, double TagSeconds, double SuccessBias, double MarginScale);

    /// <summary>本塁レースの係数束（現状値。挙動は据え置き）。</summary>
    public static BasePlayParams HomeParams(BaserunningCoefficients c)
        => new(4, new Vector3D(0, 0, 0), c.HomeTagSeconds, c.HomeSuccessBias, c.HomeMarginScale);

    /// <summary>三塁レースの係数束（Issue #89）。送球先は三塁ベース、タッチ/バイアス/幅は三塁固有。</summary>
    public static BasePlayParams ThirdParams(FieldGeometry field, BaserunningCoefficients c)
        => new(3, field.ThirdBase, c.ThirdTagSeconds, c.ThirdSuccessBias, c.ThirdMarginScale);

    /// <summary>ゴロ凡打の進塁打レース係数束（二塁→三塁, Issue #318）。送球先は三塁ベース、
    /// タッチ/バイアス/幅は進塁固有（<see cref="ThirdParams"/> と同型・係数だけ分離）。</summary>
    public static BasePlayParams AdvanceParams(FieldGeometry field, BaserunningCoefficients c)
        => new(3, field.ThirdBase, c.AdvanceTagSeconds, c.AdvanceSuccessBias, c.AdvanceMarginScale);

    /// <summary>二塁封殺レースの係数束（併殺の一段目＝一塁走者を二塁で封殺, Issue #369）。送球先は二塁ベース、
    /// タッチ/バイアス/幅は封殺固有（<see cref="ThirdParams"/> と同型・係数だけ分離）。TargetBase=2。</summary>
    public static BasePlayParams SecondForceParams(FieldGeometry field, BaserunningCoefficients c)
        => new(2, field.SecondBase, c.SecondForceTagSeconds, c.SecondForceSuccessBias, c.SecondForceMarginScale);

    /// <summary>三塁封殺レースの係数束（併殺の代替一段目＝二塁走者を三塁で自己フォース, Q31）。送球先は
    /// 三塁ベース、タッチ/バイアス/幅は自己フォース固有（<see cref="ThirdParams"/> とは別＝あちらは開けた
    /// 進塁レース用の校正で意味が違う）。TargetBase=3。</summary>
    public static BasePlayParams ThirdForceParams(FieldGeometry field, BaserunningCoefficients c)
        => new(3, field.ThirdBase, c.ThirdForceTagSeconds, c.ThirdForceSuccessBias, c.ThirdForceMarginScale);

    /// <summary>本塁封殺レースの係数束（併殺の代替一段目＝三塁走者を本塁で自己フォース, Q31）。送球先は
    /// 本塁、タッチ/バイアス/幅は自己フォース固有（<see cref="HomeParams"/> とは別＝あちらは開けた本塁生還
    /// レース用の校正で意味が違う）。TargetBase=4。</summary>
    public static BasePlayParams HomeForceParams(BaserunningCoefficients c)
        => new(4, new Vector3D(0, 0, 0), c.HomeForceTagSeconds, c.HomeForceSuccessBias, c.HomeForceMarginScale);

    /// <summary>
    /// 塁上の走者の時計の文脈（Issue #316）。二次リードで前に出ており、走塁判断ぶんの反応遅延がある。
    /// 判定・表示ともこの文脈で <see cref="RunnerClock"/> を呼べば同じ時刻になる。
    /// </summary>
    public static RunnerContext RunnerContextOf(Player runner, BaserunningCoefficients c)
        => new(
            StartDelaySeconds:
                Math.Max(0.10, c.HomeRunnerReactionIntercept - c.AbilityEffect.Running(runner.Baserunning) * c.HomeRunnerReactionSlope),
            LeadDistanceM: c.HomeLeadDistanceM,
            TopSpeedFactor: c.RunnerTopSpeedFactor,
            TurnSecondsPerBase: c.RunnerBaseTurnSeconds,
            RoundsStartingBase: false);

    /// <summary>
    /// タッチアップの走者の時計の文脈（issue score-display-before-home-touch）。
    /// 捕球でベースを蹴るので<b>二次リードは取れず</b>（塁間は全距離）、<b>静止から出る</b>ので
    /// 走り出しの速度倍率も乗らない。起点（捕球時刻）は呼び出し側が足す
    /// （<see cref="RunnerMove.TagUpCatchAtSeconds"/>）。
    ///
    /// <para>これで <see cref="RunnerClock"/> が <see cref="TagUpRunnerTimeSeconds"/> と同じ秒を返す＝
    /// 判定と表示が同じ時計になる（Issue #316 の原則の穴埋め）。両者が一致することは
    /// <c>TagUpRunnerLegTests</c> が固定する（判定側の式は帯・決定論のため1文字も動かさない）。</para>
    /// </summary>
    public static RunnerContext TagUpContextOf(Player runner, BaserunningCoefficients c)
        => RunnerContextOf(runner, c) with { LeadDistanceM = 0.0, TopSpeedFactor = 1.0 };

    /// <summary>
    /// 走者が目標塁へ到達する所要時間[s]（接触基準）。fromBase: 0〜3、toBase: 3=三塁/4=本塁。
    /// extraStartDelaySeconds=打球を読んでから走り出す等の追加遅延（G1ゴロ本塁レースで正値。ヒットは0）。
    /// </summary>
    public static double RunnerTimeSeconds(
        Player runner, int fromBase, int toBase, FieldGeometry field, BaserunningCoefficients c,
        double extraStartDelaySeconds = 0.0)
        => RunnerClock.SegmentSeconds(
            runner, c.AbilityEffect, fromBase, toBase, RunnerContextOf(runner, c), field, extraStartDelaySeconds);

    /// <summary>本塁への走者所要時間[s]（後方互換: toBase=4 固定）。</summary>
    public static double RunnerTimeSeconds(
        Player runner, int fromBase, FieldGeometry field, BaserunningCoefficients c, double extraStartDelaySeconds = 0.0)
        => RunnerTimeSeconds(runner, fromBase, 4, field, c, extraStartDelaySeconds);

    /// <summary>
    /// 送球経路の内訳（判定と表示の**単一ソース**, Issue #301③/#298-2）。
    /// <see cref="DefenseTimeSeconds"/> はここから導出するので、タイムライン側がこの構造体を使って
    /// 絵を描けば「送球の到達時刻が判定と矛盾する」ことが構造的に起きない。時刻は接触＝0秒基準。
    /// </summary>
    /// <param name="HasRelay">中継（カットマン経由）か。false なら直接返球。</param>
    /// <param name="CutoffPoint">カットマンの理論位置（送球先ベースから <c>CutoffFractionFromHome</c> の位置）。直接返球では送球先と同値。</param>
    /// <param name="ReleaseAtSeconds">処理野手が投げ始める時刻（＝回収＋外野の持ち替え）。</param>
    /// <param name="ArriveAtSeconds">送球先ベースにボールが到達する時刻（タッチは含まない）。</param>
    public readonly record struct ThrowPath(
        bool HasRelay, Vector3D CutoffPoint, double ReleaseAtSeconds, double ArriveAtSeconds);

    /// <summary>
    /// この送球で中継（カットマン）を挟むか（issue #526）。**判定式はこの1箇所にしか置かない**——
    /// 本塁・三塁・併殺の転送（<see cref="ResolveThrowPath"/> 経由）と、外野返球の再生データ
    /// （<c>TimelineBuilder.AppendOutfieldReturnThrow</c>）が同じ関数を通ることで、
    /// 判定と絵で中継の出方が食い違わない。
    ///
    /// 距離だけでなく<b>投げる野手の肩</b>で決まる＝強肩は遠くから直接返球する。乱数は引かない純関数。
    /// </summary>
    /// <param name="distanceM">処理点から送球先までの水平距離[m]。</param>
    /// <param name="throwSpeedMps">投げる野手の送球速度[m/s]（<c>FielderAttributes.ThrowSpeedMps</c>）。</param>
    public static bool NeedsRelay(double distanceM, double throwSpeedMps, BaserunningCoefficients c)
        => distanceM > throwSpeedMps * c.CutoffThrowSeconds;

    /// <summary>
    /// 処理点→送球先ベースの送球経路と時刻を解く（中継しきい値・カットマン位置・肩・握り替えを一括）。
    /// <see cref="DefenseTimeSeconds"/> の算式そのもの（切り出しただけで数値は不変）。
    /// <paramref name="transferSecondsOverride"/>＝処理野手の捕球→送球（握り替え）秒の差し替え（Issue #318）。
    /// **null なら現行 <see cref="BaserunningCoefficients.OutfieldTransferSeconds"/> と完全恒等**（既存呼び出しは
    /// 引数を足さずビット一致を保つ）。一塁を取ってから三塁へ転送する経路だけ AdvanceRelayTransferSeconds を渡す。
    /// </summary>
    public static ThrowPath ResolveThrowPath(HomePlaySituation s, Vector3D targetPoint, BaserunningCoefficients c,
        double? transferSecondsOverride = null)
    {
        // 処理点から送球先ベースまでの水平距離。
        var dx = s.BallFieldedPoint.X - targetPoint.X;
        var dz = s.BallFieldedPoint.Z - targetPoint.Z;
        var dist = Math.Sqrt(dx * dx + dz * dz);

        // 握り替え倍率（Issue #36）: 処理野手の守備で外野返球・中継の持ち替えを伸縮。守備50で×1.0＝恒等。
        var transferFactor = new Players.FielderAttributes { Fielding = s.OutfielderFielding }
            .TransferFactor(c.TransferFieldingSlope, c.TransferFactorMin);

        var hasRelay = NeedsRelay(dist, s.OutfielderThrowSpeedMps, c);
        double throwTime;
        if (hasRelay)
        {
            // 中継: カットマンは送球先ベースから CutoffFractionFromHome の位置。
            var legToCutoff = dist * (1.0 - c.CutoffFractionFromHome) / s.OutfielderThrowSpeedMps;
            var legToTarget = dist * c.CutoffFractionFromHome / c.RelayThrowSpeedMps;
            throwTime = legToCutoff + c.RelayTransferSeconds * transferFactor + legToTarget;
        }
        else
        {
            throwTime = dist / s.OutfielderThrowSpeedMps; // 直接返球
        }
        // 送球の逸れ（issue #858）: 送球者(s.ThrowerThrowAccuracy)のリーグ平均（50）からの差分だけの
        // 期待遅れを ThrowScatterModel で加える。この経路は本塁・三塁クロスプレー、犠飛/進塁のタッチアップ、
        // 併殺（BaserunningModel.ResolveDoublePlay の二塁/三塁/本塁フォース）、バントの封殺が共有する単一経路
        // ＝ここ一箇所の変更で全部に広がる。受け手は league average 固定（Catching=50＝恒等。この経路には
        // 塁をカバーする個別の受け手が無い場面も混在するため、全文脈で同じ既定にして帯への影響をゼロに保つ）。
        // 精度50では厳密に0.0（既存呼び出し＝既定 ThrowerThrowAccuracy=50 は浮動小数まで完全不変）。
        throwTime += ThrowScatterModel.CenteredExpectedRecoverySeconds(
            ThrowScatterModel.SigmaCmForAccuracy(s.ThrowerThrowAccuracy), receiverCatching: 50,
            c.ThrowScatterLateralSecPerCm, c.ThrowScatterHighSecPerCm, c.ThrowScatterShortHopSecPerCm,
            c.ThrowScatterCatchingReliefSlope, c.ThrowScatterCatchingReliefMinFactor);

        // 握り替え秒: null なら OutfieldTransferSeconds（既存経路とビット一致）。
        var transferSeconds = transferSecondsOverride ?? c.OutfieldTransferSeconds;
        // 加算の順序は元の式のまま（左結合）＝浮動小数の結果までビット一致で保つ（帯不変）。
        var release = s.BallFieldedAtSeconds + transferSeconds * transferFactor;
        var cutoff = hasRelay
            ? new Vector3D(
                targetPoint.X + (s.BallFieldedPoint.X - targetPoint.X) * c.CutoffFractionFromHome,
                0,
                targetPoint.Z + (s.BallFieldedPoint.Z - targetPoint.Z) * c.CutoffFractionFromHome)
            : targetPoint;
        return new ThrowPath(hasRelay, cutoff, release, release + throwTime);
    }

    /// <summary>目標塁へ送球が到達する所要時間[s]（外野処理＋中継/直接＋タッチ, 接触基準）。
    /// <paramref name="transferSecondsOverride"/> は握り替え秒の差し替え（Issue #318, null で恒等）。</summary>
    public static double DefenseTimeSeconds(HomePlaySituation s, Vector3D targetPoint, double tagSeconds, BaserunningCoefficients c,
        double? transferSecondsOverride = null)
        => ResolveThrowPath(s, targetPoint, c, transferSecondsOverride).ArriveAtSeconds + tagSeconds;

    /// <summary>本塁への送球所要時間[s]（後方互換: 送球先=原点・タッチ=HomeTagSeconds）。</summary>
    public static double DefenseTimeSeconds(HomePlaySituation s, BaserunningCoefficients c)
        => DefenseTimeSeconds(s, new Vector3D(0, 0, 0), c.HomeTagSeconds, c);

    /// <summary>
    /// 到達判定のmargin[s]（守備所要−走者所要＋バイアス。判定オーバーレイ, Issue #59）。
    /// 0=判定境界、正=セーフ（到達）寄り、負=アウト（憤死）寄り。塁は <paramref name="p"/> で指定。
    /// <paramref name="transferSecondsOverride"/> は守備の握り替え秒の差し替え（Issue #318, null で恒等）。
    /// </summary>
    public static double Margin(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, double extraStartDelaySeconds = 0.0, double? transferSecondsOverride = null)
        => DefenseTimeSeconds(s, p.TargetPoint, p.TagSeconds, c, transferSecondsOverride)
           - RunnerTimeSeconds(runner, fromBase, p.TargetBase, field, c, extraStartDelaySeconds) + p.SuccessBias;

    /// <summary>本塁 margin[s]（後方互換: HomeParams 固定）。</summary>
    public static double Margin(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        double extraStartDelaySeconds = 0.0)
        => Margin(runner, fromBase, s, field, c, HomeParams(c), extraStartDelaySeconds);

    /// <summary>到達成功確率（走塁と同式の logistic）。塁は <paramref name="p"/> で指定。
    /// <paramref name="transferSecondsOverride"/> は守備の握り替え秒の差し替え（Issue #318, null で恒等）。</summary>
    public static double SuccessProbability(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, double extraStartDelaySeconds = 0.0, double? transferSecondsOverride = null)
    {
        var margin = Margin(runner, fromBase, s, field, c, p, extraStartDelaySeconds, transferSecondsOverride);
        return MathUtil.Clamp(MathUtil.Logistic(margin / p.MarginScale), 0.01, 0.99);
    }

    /// <summary>本塁生還成功確率（後方互換: HomeParams 固定）。</summary>
    public static double SuccessProbability(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        double extraStartDelaySeconds = 0.0)
        => SuccessProbability(runner, fromBase, s, field, c, HomeParams(c), extraStartDelaySeconds);

    /// <summary>
    /// 勝負1回の判定と、その判定に合う走者の秒（issue #801）。
    /// </summary>
    /// <param name="Safe">走者が勝ったか（到達）。</param>
    /// <param name="RunnerDelaySeconds">
    /// アウトのとき、走者が引き受けた出遅れ[s]（スタートの遅れ・三塁を回るときのもたつき）。
    /// 走者の時計どおりだと送球より先に着いてしまう場面でだけ正になる。運はまず守備の持ち替え（中継・握り替え）を
    /// 素早く済ませた秒（<see cref="QuickHandlingSeconds"/>）で引き受け、それを超える分だけを走者の出遅れにする
    /// （盗塁の <c>StealResolver</c> と同じ振り分け）。絵は <see cref="RunnerMove.ExtraStartDelaySeconds"/> に足して描く
    /// ＝「送球が届く前にアウト」が構造的に起きない。セーフ・送球が先に着く場面では0。
    /// </param>
    /// <param name="Scatter">この勝負の送球が実際に逸れた量（issue #859）。判定の遅れ・悪送球の判定に使った値そのもので、
    /// 絵は受け手をこの値へ寄せて描く。勝負を抽選しない経路では null。</param>
    public readonly record struct RaceVerdict(bool Safe, double RunnerDelaySeconds, ThrowScatterModel.Scatter? Scatter = null);

    /// <summary>
    /// 送球1本の逸れを引き、判定へ足す秒（ゼロ平均のゆらぎ）を返す（issue #859）。送球精度ごとの期待遅れは
    /// <see cref="ResolveThrowPath"/> が既に足しているので、ここではその本の実際の遅れと期待値との差だけを返す
    /// ＝送り判断（期待値で読む）と勝負の平均が一致する（盗塁送球 <c>StealResolver.ResolveVerdict</c> と同じ作法）。
    /// 受け手は <see cref="ResolveThrowPath"/> と同じく Catching=50 固定。乱数は正規2つ（横・縦）。
    /// </summary>
    public static (ThrowScatterModel.Scatter Scatter, double NoiseSeconds) SampleThrowScatter(
        int throwAccuracy, BaserunningCoefficients c, IRandomSource rng)
    {
        var sigmaCm = ThrowScatterModel.SigmaCmForAccuracy(throwAccuracy);
        var scatter = ThrowScatterModel.Sample(sigmaCm, rng);
        var noise = ThrowScatterModel.RecoverySeconds(scatter, receiverCatching: 50,
                        c.ThrowScatterLateralSecPerCm, c.ThrowScatterHighSecPerCm, c.ThrowScatterShortHopSecPerCm,
                        c.ThrowScatterCatchingReliefSlope, c.ThrowScatterCatchingReliefMinFactor)
                    - ThrowScatterModel.ExpectedRecoverySeconds(sigmaCm, receiverCatching: 50,
                        c.ThrowScatterLateralSecPerCm, c.ThrowScatterHighSecPerCm, c.ThrowScatterShortHopSecPerCm,
                        c.ThrowScatterCatchingReliefSlope, c.ThrowScatterCatchingReliefMinFactor);
        return (scatter, noise);
    }

    /// <summary>
    /// 勝負の抽選（乱数1つ）。確率は margin の logistic そのもの＝<b>1%/99% でクランプしない</b>（issue #801）。
    ///
    /// <para><see cref="SuccessProbability"/> のクランプは送り判断・采配AIの<b>読み</b>のためのもので、
    /// 勝負の抽選にまで掛けると「走者が数秒早く着く場面でも1%でアウト」になり、そのアウトを表す秒がどこにも
    /// 無かった（安打での本塁憤死の約6%が「送球が届く4秒前にアウト」の絵になっていた）。外せば、アウトが出るのは
    /// 運の秒で説明できる場面だけになる: アウト ⇔ margin＋ゆらぎ＜0 で、margin＝守備−走者＋バイアス。
    /// 走者が守備より先に着く（守備−走者＞0）のにアウトなら、ゆらぎはその差＋バイアス以上に走者へ不利に出ている
    /// ＝<paramref name="defenseMinusRunner"/> ぶんを守備の素早さ（<paramref name="quickSeconds"/>）と走者の出遅れで
    /// 引き受けても、引いたゆらぎの内側に収まる（バイアスが負の勝負は無い）。
    /// 乱数の消費は従来の <see cref="MathUtil.Chance"/> と同じ1回。</para>
    /// </summary>
    private static RaceVerdict DrawRace(
        double margin, double marginScale, double defenseMinusRunner, double quickSeconds, IRandomSource rng,
        ThrowScatterModel.Scatter? scatter = null)
    {
        var safe = MathUtil.Chance(MathUtil.Logistic(margin / marginScale), rng);
        return new RaceVerdict(safe, safe ? 0.0 : Math.Max(0.0, defenseMinusRunner - quickSeconds), scatter);
    }

    /// <summary>
    /// 守備が運の良い側に振れたとき縮められる持ち替えの秒[s]（issue #801）。中継の持ち替え（中継ありのとき）と、
    /// 握り替えの差し替え（<paramref name="transferSecondsOverride"/>＝一塁からの転送）の合計。絵の側も同じ秒を
    /// 「間ゼロで投げ切った最速」まで詰められる（<c>TimelineBuilder.GuardAgainstVerdict</c>）ので、
    /// 走者の出遅れはこれを超えた分だけでよい。乱数不使用の純計算。
    /// </summary>
    public static double QuickHandlingSeconds(
        HomePlaySituation s, Vector3D targetPoint, BaserunningCoefficients c, double? transferSecondsOverride = null)
    {
        var path = ResolveThrowPath(s, targetPoint, c, transferSecondsOverride);
        var transferFactor = new Players.FielderAttributes { Fielding = s.OutfielderFielding }
            .TransferFactor(c.TransferFieldingSlope, c.TransferFactorMin);
        var relayHold = path.HasRelay ? c.RelayTransferSeconds * transferFactor : 0.0;
        var transfer = transferSecondsOverride is { } t ? t * transferFactor : 0.0;
        return relayHold + transfer;
    }

    /// <summary>到達レースを1回解く（判定＋アウト時の走者の出遅れ秒, issue #801）。塁は <paramref name="p"/> で指定。
    /// <paramref name="transferSecondsOverride"/> は守備の握り替え秒の差し替え（Issue #318, null で恒等）。</summary>
    public static RaceVerdict ResolveRace(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng, double extraStartDelaySeconds = 0.0, double? transferSecondsOverride = null)
    {
        // 送球1本の逸れ（issue #859）: その本の実際の遅れと期待値の差を守備側へ足す（ゼロ平均）。
        var (scatter, noise) = SampleThrowScatter(s.ThrowerThrowAccuracy, c, rng);
        // Margin と同じ式・同じ加算順（守備−走者＋バイアス）に、逸れのゆらぎを足す。
        var defenseMinusRunner = DefenseTimeSeconds(s, p.TargetPoint, p.TagSeconds, c, transferSecondsOverride)
            - RunnerTimeSeconds(runner, fromBase, p.TargetBase, field, c, extraStartDelaySeconds);
        // 走者の出遅れ（絵）は、絵の送球の秒（期待値）より速く刺したことにならないよう、ゆらぎの遅い側だけで測る。
        return DrawRace(defenseMinusRunner + noise + p.SuccessBias, p.MarginScale, defenseMinusRunner + Math.Max(0.0, noise),
            QuickHandlingSeconds(s, p.TargetPoint, c, transferSecondsOverride), rng, scatter);
    }

    /// <summary>到達レースを解決（Safe=到達 or false=憤死）。塁は <paramref name="p"/> で指定。</summary>
    public static bool ResolveSafe(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng, double extraStartDelaySeconds = 0.0)
        => ResolveRace(runner, fromBase, s, field, c, p, rng, extraStartDelaySeconds).Safe;

    /// <summary>本塁クロスプレーを解決（生還 or 憤死）。extraStartDelaySeconds=走者の追加スタート遅延（G1）。</summary>
    public static HomePlayResult Resolve(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        IRandomSource rng, double extraStartDelaySeconds = 0.0)
        => ResolveSafe(runner, fromBase, s, field, c, HomeParams(c), rng, extraStartDelaySeconds)
            ? HomePlayResult.Safe
            : HomePlayResult.OutAtHome;

    // === 本塁への悪送球（2026-08-30, design-14 P1-6b型の二値ロールをバックホームへ拡張）===
    // 「外野からのバックホームが毎回ドンピシャで届く」違和感の是正。内野ゴロ→一塁の送球エラー
    // （FieldingResolver.MaybeInfieldError, ThrowErrorBaseProb）と同じ形＝守備が間に合う（憤死になる）
    // 場面でだけ追加の1ロールを引き、送球者の ThrowAccuracy が低いほど悪送球になりやすい。

    /// <summary>本塁クロスプレーの結果（悪送球を区別する拡張）。<see cref="SafeOnWildThrow"/> は
    /// 「守備が間に合ったはずが悪送球で生還した」＝呼び出し側で悪送球の連鎖（全走者+1進塁）を
    /// トリガーする合図。<see cref="Safe"/>（走者が普通に勝った）とは区別する。</summary>
    public enum HomeThrowOutcome { Safe, OutAtHome, SafeOnWildThrow }

    /// <summary>本塁への送球が逸れるか（送球者の ThrowAccuracy から確率を出し、2次元の逸れをサンプルして
    /// 閾値と比べる＝issue #858で逸れの閾値へ統合。確率係数は不変（下の式）なので、確率カーブ自体は従来の
    /// 二値ロールと完全に同じ＝rng消費が1→2系統に増える以外は帯への影響なし。決定論baseline再生成が必要）。
    /// Ac=50 で <see cref="BaserunningCoefficients.RelayThrowErrorBaseProb"/> と恒等。
    /// <see cref="BaserunningCoefficients.RelayThrowErrorBaseProb"/>≤0 なら常にfalse（rng非消費＝旧経路と完全一致）。</summary>
    public static bool IsWildThrow(int throwAccuracy, BaserunningCoefficients c, IRandomSource rng)
        => c.RelayThrowErrorBaseProb > 0.0
           && IsWildThrow(throwAccuracy, c, ThrowScatterModel.Sample(ThrowScatterModel.SigmaCmForAccuracy(throwAccuracy), rng));

    /// <summary>引き済みの逸れ（勝負に使った同じ1本, issue #859）が悪送球か。乱数を消費しない。</summary>
    public static bool IsWildThrow(int throwAccuracy, BaserunningCoefficients c, ThrowScatterModel.Scatter scatter)
    {
        if (c.RelayThrowErrorBaseProb <= 0.0) return false;
        var prob = MathUtil.Clamp(
            c.RelayThrowErrorBaseProb - (throwAccuracy - 50) * c.RelayThrowErrorAccuracySlope, 0.0, 1.0);
        var sigmaCm = ThrowScatterModel.SigmaCmForAccuracy(throwAccuracy);
        var thresholdCm = ThrowScatterModel.WildThresholdCmForProbability(sigmaCm, prob);
        return ThrowScatterModel.IsWild(scatter, thresholdCm);
    }

    /// <summary>送球逸れの回収レース（design-12 §3.8）: 逸れ幅をサンプルし、拾い直しロス[s]を返す。
    /// 回収役は「受け手自身」（起点=塁、逸れ幅ぶんだけ動く。返球不要）と「バックアップ」
    /// （<see cref="ThrowRecovery.BackupRole"/>の定位置から着弾点まで走り、塁へ返球する）の近い方。
    /// バックアップの走力・肩は個別Player情報が届いていないため<see cref="BaserunningCoefficients.RecoverySprintSpeedMps"/>
    /// と<see cref="BaserunningCoefficients.RelayThrowSpeedMps"/>（リーグ平均内野肩）の定数で近似する。</summary>
    private static double SampleRecoveryPenaltySeconds(
        HomePlaySituation s, Vector3D targetPoint, int targetBase, BaserunningCoefficients c, IRandomSource rng)
    {
        var scatterM = Math.Abs(rng.NextGaussian(0.0, c.WildThrowScatterSigmaM));
        var landing = ThrowRecovery.LandingPoint(s.BallFieldedPoint, targetPoint, scatterM);
        var (backupX, backupZ) = FieldGeometry.StandardPosition(ThrowRecovery.BackupRole(targetBase));
        return ThrowRecovery.RecoveryPenaltySeconds(
            scatterM, landing, new Vector3D(backupX, 0, backupZ),
            c.RecoverySprintSpeedMps, c.RecoverySprintSpeedMps, c.RelayThrowSpeedMps, c.RecoveryReactionSeconds);
    }

    /// <summary>本塁クロスプレーを解決し、悪送球による生還を区別する。塁は <paramref name="p"/> で指定。
    /// 走者が勝てば <see cref="ResolveSafe"/> と同じ1ロールで確定（悪送球ロールは引かない＝乱数消費順不変）。
    /// 守備が勝つ場面だけ追加で <see cref="IsWildThrow"/> を引く。悪送球が確定すると、逸れ幅→回収ロス
    /// （design-12 §3.8）を守備所要時間へ加算してもう一度同じMargin/logisticを評価する＝拾い直しが
    /// 間に合えば従来どおりアウトのまま（「毎回逸れたら即セーフ」にはしない）。</summary>
    public static HomeThrowOutcome ResolveWithWildThrow(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng, double extraStartDelaySeconds = 0.0)
        => ResolveWithWildThrowVerdict(runner, fromBase, s, field, c, p, rng, extraStartDelaySeconds).Outcome;

    /// <summary><see cref="ResolveWithWildThrow(Player,int,HomePlaySituation,FieldGeometry,BaserunningCoefficients,in BasePlayParams,IRandomSource,double)"/>
    /// に、アウトのときの走者の出遅れ秒（<see cref="RaceVerdict.RunnerDelaySeconds"/>, issue #801）を添えて返す。</summary>
    public static (HomeThrowOutcome Outcome, double RunnerDelaySeconds, ThrowScatterModel.Scatter? Scatter) ResolveWithWildThrowVerdict(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng, double extraStartDelaySeconds = 0.0)
    {
        var race = ResolveRace(runner, fromBase, s, field, c, p, rng, extraStartDelaySeconds);
        if (race.Safe) return (HomeThrowOutcome.Safe, 0.0, race.Scatter);
        return ResolveWildThrowAfter(race, s, p, c, rng, Margin(runner, fromBase, s, field, c, p, extraStartDelaySeconds));
    }

    /// <summary>
    /// 1回目の勝負で守備が勝った後（<paramref name="race"/>.Safe=false）の悪送球ロール（本塁・タッチアップ共通）。
    /// <paramref name="margin"/> は1回目と同じ margin（乱数を引かない純計算）。悪送球なら回収ロスを守備へ足して
    /// もう一度同じ margin/logistic を評価する。2回目の抽選もクランプしない（<see cref="DrawRace"/> と同じ理由）。
    /// 悪送球でもアウトのままなら、絵は普通の送球アウト＝走者の出遅れは1回目の秒をそのまま使う。
    /// </summary>
    private static (HomeThrowOutcome Outcome, double RunnerDelaySeconds, ThrowScatterModel.Scatter? Scatter) ResolveWildThrowAfter(
        RaceVerdict race, HomePlaySituation s, in BasePlayParams p, BaserunningCoefficients c, IRandomSource rng,
        double margin)
    {
        // 悪送球かどうかは、勝負に使った同じ1本の逸れで決める（issue #859: 送球は1本＝逸れも1つ）。
        var wild = race.Scatter is { } sc ? IsWildThrow(s.ThrowerThrowAccuracy, c, sc) : IsWildThrow(s.ThrowerThrowAccuracy, c, rng);
        if (!wild) return (HomeThrowOutcome.OutAtHome, race.RunnerDelaySeconds, race.Scatter);

        var recovery = SampleRecoveryPenaltySeconds(s, p.TargetPoint, p.TargetBase, c, rng);
        var penalizedMargin = margin + recovery;
        var stillSafe = MathUtil.Chance(MathUtil.Logistic(penalizedMargin / p.MarginScale), rng);
        return stillSafe
            ? (HomeThrowOutcome.SafeOnWildThrow, 0.0, race.Scatter)
            : (HomeThrowOutcome.OutAtHome, race.RunnerDelaySeconds, race.Scatter);
    }

    /// <summary>本塁クロスプレーを解決（後方互換: HomeParams 固定）。悪送球を区別する<see cref="ResolveWithWildThrow(Player,int,HomePlaySituation,FieldGeometry,BaserunningCoefficients,in BasePlayParams,IRandomSource,double)"/>の便宜オーバーロード。</summary>
    public static HomeThrowOutcome ResolveWithWildThrow(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        IRandomSource rng, double extraStartDelaySeconds = 0.0)
        => ResolveWithWildThrow(runner, fromBase, s, field, c, HomeParams(c), rng, extraStartDelaySeconds);

    // === 犠飛のタッチアップ（Issue #90, 設計書12 §3.5）===
    // タッグアップは本塁クロスプレー（安打・ゴロの本塁レース）と1点だけ違う: 走者の時計の起点が
    // 「打球の到達」ではなく「捕球の瞬間」＝離塁は捕球後。守備も同じ捕球時刻から送球する（DefenseTimeSeconds
    // は BallFieldedAtSeconds を含む）ため、共通起点は捕球時刻になり互いに相殺する。残る勝負は
    // 「塁間を全部走る時間（二次リードなし）」対「捕球地点→塁の送球時間」だけ＝打球の深さ・方向と外野の肩が効く。

    /// <summary>犠飛のタッチアップ用レース係数束（本塁へ還す想定）。送球先=本塁・タッチ=HomeTagSeconds、
    /// バイアス/幅はタッチアップ固有（<see cref="BaserunningCoefficients.TagUpSuccessBias"/> /
    /// <see cref="BaserunningCoefficients.TagUpMarginScale"/>）。</summary>
    public static BasePlayParams TagUpHomeParams(BaserunningCoefficients c)
        => new(4, new Vector3D(0, 0, 0), c.HomeTagSeconds, c.TagUpSuccessBias, c.TagUpMarginScale);

    /// <summary>タッチアップ（二塁→三塁）のレース係数束（design-14 P3-11）。送球先=三塁・タッチは三塁レースと
    /// 同軸（<see cref="BaserunningCoefficients.ThirdTagSeconds"/>）、幅はタッチアップ共通、バイアスだけ塁固有。</summary>
    public static BasePlayParams TagUpThirdParams(FieldGeometry field, BaserunningCoefficients c)
        => new(3, field.ThirdBase, c.ThirdTagSeconds, c.TagUpThirdSuccessBias, c.TagUpMarginScale);

    /// <summary>タッチアップ（一塁→二塁）のレース係数束（design-14 P3-11）。送球先=二塁・タッチは二塁と
    /// 同軸（<see cref="BaserunningCoefficients.SecondForceTagSeconds"/>＝ベースでのタッチ/捕球0.10）。</summary>
    public static BasePlayParams TagUpSecondParams(FieldGeometry field, BaserunningCoefficients c)
        => new(2, field.SecondBase, c.SecondForceTagSeconds, c.TagUpSecondSuccessBias, c.TagUpMarginScale);

    /// <summary>
    /// タッチアップ走者が目標塁へ到達する所要時間[s]（接触基準）。離塁は捕球後なので起点は捕球時刻
    /// （<paramref name="s"/>.BallFieldedAtSeconds）。二次リードは取れない（捕球時にベースへ触れている）ため
    /// 塁間は全距離を走る。
    /// </summary>
    public static double TagUpRunnerTimeSeconds(
        Player runner, int fromBase, int toBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c)
    {
        var startDelay = Math.Max(0.10, c.HomeRunnerReactionIntercept - c.AbilityEffect.Running(runner.Baserunning) * c.HomeRunnerReactionSlope);
        var distance = (toBase - fromBase) * field.BaseDistanceM; // 二次リードなし＝全塁間
        return s.BallFieldedAtSeconds + distance / runner.ToFielder(c.AbilityEffect).SprintSpeedMps + startDelay;
    }

    /// <summary>タッチアップ到達の margin[s]（守備所要−走者所要＋バイアス）。塁は <paramref name="p"/> で指定。</summary>
    public static double TagUpMargin(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c, in BasePlayParams p)
        => DefenseTimeSeconds(s, p.TargetPoint, p.TagSeconds, c)
           - TagUpRunnerTimeSeconds(runner, fromBase, p.TargetBase, s, field, c) + p.SuccessBias;

    /// <summary>タッチアップ生還成功確率（走塁と同式の logistic）。塁は <paramref name="p"/> で指定。</summary>
    public static double TagUpSuccessProbability(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c, in BasePlayParams p)
        => MathUtil.Clamp(MathUtil.Logistic(TagUpMargin(runner, fromBase, s, field, c, p) / p.MarginScale), 0.01, 0.99);

    /// <summary>タッチアップの到達レースを1回解く（判定＋アウト時の走者の出遅れ秒, issue #801）。
    /// 抽選はクランプしない（<see cref="DrawRace"/>）。塁は <paramref name="p"/> で指定。</summary>
    public static RaceVerdict TagUpResolveRace(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng)
    {
        // 送球1本の逸れ（issue #859。ResolveRace と同じ）。
        var (scatter, noise) = SampleThrowScatter(s.ThrowerThrowAccuracy, c, rng);
        // TagUpMargin と同じ式・同じ加算順に、逸れのゆらぎを足す（出遅れの扱いは ResolveRace と同じ）。
        var defenseMinusRunner = DefenseTimeSeconds(s, p.TargetPoint, p.TagSeconds, c)
            - TagUpRunnerTimeSeconds(runner, fromBase, p.TargetBase, s, field, c);
        return DrawRace(defenseMinusRunner + noise + p.SuccessBias, p.MarginScale, defenseMinusRunner + Math.Max(0.0, noise),
            QuickHandlingSeconds(s, p.TargetPoint, c), rng, scatter);
    }

    /// <summary>タッチアップの到達レースを解決（true=生還 / false=本塁憤死）。塁は <paramref name="p"/> で指定。</summary>
    public static bool TagUpResolveSafe(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng)
        => TagUpResolveRace(runner, fromBase, s, field, c, p, rng).Safe;

    /// <summary>タッチアップの到達レースを解決し、悪送球による生還を区別する（<see cref="ResolveWithWildThrow(Player,int,HomePlaySituation,FieldGeometry,BaserunningCoefficients,in BasePlayParams,IRandomSource,double)"/>のタッチアップ版）。</summary>
    public static HomeThrowOutcome TagUpResolveWithWildThrow(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng)
        => TagUpResolveWithWildThrowVerdict(runner, fromBase, s, field, c, p, rng).Outcome;

    /// <summary><see cref="TagUpResolveWithWildThrow"/> に、アウトのときの走者の出遅れ秒（issue #801）を添えて返す。</summary>
    public static (HomeThrowOutcome Outcome, double RunnerDelaySeconds, ThrowScatterModel.Scatter? Scatter) TagUpResolveWithWildThrowVerdict(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng)
    {
        var race = TagUpResolveRace(runner, fromBase, s, field, c, p, rng);
        if (race.Safe) return (HomeThrowOutcome.Safe, 0.0, race.Scatter);
        return ResolveWildThrowAfter(race, s, p, c, rng, TagUpMargin(runner, fromBase, s, field, c, p));
    }
}
