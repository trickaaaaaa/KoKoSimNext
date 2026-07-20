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
    double OutfielderThrowSpeedMps);

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
    /// 走者が本塁へ到達する所要時間[s]（接触基準）。fromBase: 1〜3。
    /// extraStartDelaySeconds=打球を読んでから走り出す等の追加遅延（G1ゴロ本塁レースで正値。ヒットは0）。
    /// </summary>
    public static double RunnerTimeSeconds(
        Player runner, int fromBase, FieldGeometry field, BaserunningCoefficients c, double extraStartDelaySeconds = 0.0)
    {
        var startDelay = Math.Max(0.10, c.HomeRunnerReactionIntercept - runner.Baserunning * c.HomeRunnerReactionSlope);
        var basesToGo = 4 - fromBase; // 3塁=1, 2塁=2, 1塁=3
        var distance = Math.Max(0.0, basesToGo * field.BaseDistanceM - c.HomeLeadDistanceM);
        return distance / runner.ToFielder().SprintSpeedMps + startDelay + extraStartDelaySeconds;
    }

    /// <summary>本塁へ送球が到達する所要時間[s]（外野処理＋中継/直接＋タッチ, 接触基準）。</summary>
    public static double DefenseTimeSeconds(HomePlaySituation s, BaserunningCoefficients c)
    {
        // 本塁は原点。処理点から本塁までの水平距離。
        var p = s.BallFieldedPoint;
        var dist = Math.Sqrt(p.X * p.X + p.Z * p.Z);

        double throwTime;
        if (dist > c.CutoffDistanceThresholdM)
        {
            // 中継: カットマンは本塁から CutoffFractionFromHome の位置。
            var legToCutoff = dist * (1.0 - c.CutoffFractionFromHome) / s.OutfielderThrowSpeedMps;
            var legToHome = dist * c.CutoffFractionFromHome / c.RelayThrowSpeedMps;
            throwTime = legToCutoff + c.RelayTransferSeconds + legToHome;
        }
        else
        {
            throwTime = dist / s.OutfielderThrowSpeedMps; // 直接返球
        }

        return s.BallFieldedAtSeconds + c.OutfieldTransferSeconds + throwTime + c.HomeTagSeconds;
    }

    /// <summary>生還成功確率（走塁と同式の logistic）。extraStartDelaySeconds=走者の追加スタート遅延（G1）。</summary>
    public static double SuccessProbability(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        double extraStartDelaySeconds = 0.0)
    {
        var margin = DefenseTimeSeconds(s, c)
                     - RunnerTimeSeconds(runner, fromBase, field, c, extraStartDelaySeconds) + c.HomeSuccessBias;
        return MathUtil.Clamp(MathUtil.Logistic(margin / c.HomeMarginScale), 0.01, 0.99);
    }

    /// <summary>本塁クロスプレーを解決（生還 or 憤死）。extraStartDelaySeconds=走者の追加スタート遅延（G1）。</summary>
    public static HomePlayResult Resolve(
        Player runner, int fromBase, HomePlaySituation s, FieldGeometry field, BaserunningCoefficients c,
        IRandomSource rng, double extraStartDelaySeconds = 0.0)
        => MathUtil.Chance(SuccessProbability(runner, fromBase, s, field, c, extraStartDelaySeconds), rng)
            ? HomePlayResult.Safe
            : HomePlayResult.OutAtHome;
}
