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
/// <param name="OutfielderFieldingAbility">
/// 処理野手の守備(Fielding)[1〜100]（トランスファー秒数の起点, Issue #36）。既定50＝従来の固定秒数と恒等。
/// </param>
public readonly record struct HomePlaySituation(
    Vector3D BallFieldedPoint,
    double BallFieldedAtSeconds,
    double OutfielderThrowSpeedMps,
    int OutfielderFieldingAbility = 50);

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

    /// <summary>
    /// 走者が目標塁へ到達する所要時間[s]（接触基準）。fromBase: 0〜3、toBase: 3=三塁/4=本塁。
    /// extraStartDelaySeconds=打球を読んでから走り出す等の追加遅延（G1ゴロ本塁レースで正値。ヒットは0）。
    /// </summary>
    public static double RunnerTimeSeconds(
        Player runner, int fromBase, int toBase, FieldGeometry field, BaserunningCoefficients c,
        double extraStartDelaySeconds = 0.0)
    {
        var startDelay = Math.Max(0.10, c.HomeRunnerReactionIntercept - runner.Baserunning * c.HomeRunnerReactionSlope);
        var basesToGo = toBase - fromBase; // 本塁: 3塁=1,2塁=2,1塁=3 / 三塁: 1塁=2
        var distance = Math.Max(0.0, basesToGo * field.BaseDistanceM - c.HomeLeadDistanceM);
        return distance / runner.ToFielder().SprintSpeedMps + startDelay + extraStartDelaySeconds;
    }

    /// <summary>本塁への走者所要時間[s]（後方互換: toBase=4 固定）。</summary>
    public static double RunnerTimeSeconds(
        Player runner, int fromBase, FieldGeometry field, BaserunningCoefficients c, double extraStartDelaySeconds = 0.0)
        => RunnerTimeSeconds(runner, fromBase, 4, field, c, extraStartDelaySeconds);

    /// <summary>目標塁へ送球が到達する所要時間[s]（外野処理＋中継/直接＋タッチ, 接触基準）。</summary>
    public static double DefenseTimeSeconds(HomePlaySituation s, Vector3D targetPoint, double tagSeconds, BaserunningCoefficients c)
    {
        // 処理点から送球先ベースまでの水平距離。
        var dx = s.BallFieldedPoint.X - targetPoint.X;
        var dz = s.BallFieldedPoint.Z - targetPoint.Z;
        var dist = Math.Sqrt(dx * dx + dz * dz);

        double throwTime;
        if (dist > c.CutoffDistanceThresholdM)
        {
            // 中継: カットマンは送球先ベースから CutoffFractionFromHome の位置。
            var legToCutoff = dist * (1.0 - c.CutoffFractionFromHome) / s.OutfielderThrowSpeedMps;
            var legToTarget = dist * c.CutoffFractionFromHome / c.RelayThrowSpeedMps;
            throwTime = legToCutoff + c.RelayTransferSeconds + legToTarget;
        }
        else
        {
            throwTime = dist / s.OutfielderThrowSpeedMps; // 直接返球
        }

        var outfieldTransfer = new FielderAttributes { Fielding = s.OutfielderFieldingAbility }.TransferSeconds(
            c.OutfieldTransferSeconds, c.TransferFieldingSlope, c.TransferSecondsFloor);
        return s.BallFieldedAtSeconds + outfieldTransfer + throwTime + tagSeconds;
    }

    /// <summary>本塁への送球所要時間[s]（後方互換: 送球先=原点・タッチ=HomeTagSeconds）。</summary>
    public static double DefenseTimeSeconds(HomePlaySituation s, BaserunningCoefficients c)
        => DefenseTimeSeconds(s, new Vector3D(0, 0, 0), c.HomeTagSeconds, c);

    /// <summary>
    /// 到達判定のmargin[s]（守備所要−走者所要＋バイアス。判定オーバーレイ, Issue #59）。
    /// 0=判定境界、正=セーフ（到達）寄り、負=アウト（憤死）寄り。塁は <paramref name="p"/> で指定。
    /// </summary>
    public static double Margin(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, double extraStartDelaySeconds = 0.0)
        => DefenseTimeSeconds(s, p.TargetPoint, p.TagSeconds, c)
           - RunnerTimeSeconds(runner, fromBase, p.TargetBase, field, c, extraStartDelaySeconds) + p.SuccessBias;

    /// <summary>本塁 margin[s]（後方互換: HomeParams 固定）。</summary>
    public static double Margin(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        double extraStartDelaySeconds = 0.0)
        => Margin(runner, fromBase, s, field, c, HomeParams(c), extraStartDelaySeconds);

    /// <summary>到達成功確率（走塁と同式の logistic）。塁は <paramref name="p"/> で指定。</summary>
    public static double SuccessProbability(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        in BasePlayParams p, double extraStartDelaySeconds = 0.0)
    {
        var margin = Margin(runner, fromBase, s, field, c, p, extraStartDelaySeconds);
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

    /// <summary>
    /// タッチアップ走者が目標塁へ到達する所要時間[s]（接触基準）。離塁は捕球後なので起点は捕球時刻
    /// （<paramref name="s"/>.BallFieldedAtSeconds）。二次リードは取れない（捕球時にベースへ触れている）ため
    /// 塁間は全距離を走る。
    /// </summary>
    public static double TagUpRunnerTimeSeconds(
        Player runner, int fromBase, int toBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c)
    {
        var startDelay = Math.Max(0.10, c.HomeRunnerReactionIntercept - runner.Baserunning * c.HomeRunnerReactionSlope);
        var distance = (toBase - fromBase) * field.BaseDistanceM; // 二次リードなし＝全塁間
        return s.BallFieldedAtSeconds + distance / runner.ToFielder().SprintSpeedMps + startDelay;
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
