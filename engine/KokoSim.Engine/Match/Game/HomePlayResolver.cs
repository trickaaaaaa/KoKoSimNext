using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
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
    int OutfielderFielding = 50);

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

    /// <summary>到達レースを解決（Safe=到達 or false=憤死）。塁は <paramref name="p"/> で指定。</summary>
    public static bool ResolveSafe(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng, double extraStartDelaySeconds = 0.0)
        => MathUtil.Chance(SuccessProbability(runner, fromBase, s, field, c, p, extraStartDelaySeconds), rng);

    /// <summary>本塁クロスプレーを解決（生還 or 憤死）。extraStartDelaySeconds=走者の追加スタート遅延（G1）。</summary>
    public static HomePlayResult Resolve(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        IRandomSource rng, double extraStartDelaySeconds = 0.0)
        => ResolveSafe(runner, fromBase, s, field, c, HomeParams(c), rng, extraStartDelaySeconds)
            ? HomePlayResult.Safe
            : HomePlayResult.OutAtHome;

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

    /// <summary>タッチアップの到達レースを解決（true=生還 / false=本塁憤死）。塁は <paramref name="p"/> で指定。</summary>
    public static bool TagUpResolveSafe(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, IRandomSource rng)
        => MathUtil.Chance(TagUpSuccessProbability(runner, fromBase, s, field, c, p), rng);
}
