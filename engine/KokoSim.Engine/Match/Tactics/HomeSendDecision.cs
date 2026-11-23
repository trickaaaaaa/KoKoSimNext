using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 三塁コーチの「本塁へ還すか自重か」の判断（設計書12 §3/§4, F2）。
/// 盗塁の StealMinSuccess と同型＝生還推定確率（<see cref="Game.HomePlayResolver.SuccessProbability"/>）が
/// 閾値以上なら還す。閾値は 2アウト緩和＋aggression（監督采配・校風）で動く。純関数・乱数不使用。
/// 「勝てる時だけ送る」＝出塁/生還率を確率テーブルでなくレース＋采配から創発させる（Q9(c)）。
/// </summary>
public static class HomeSendDecision
{
    /// <summary>
    /// 本塁送りの閾値（生還推定確率がこれ以上なら還す）。
    /// aggression: 0=超慎重, 0.5=中立, 1=超積極（機動力校・攻めの監督）。
    /// </summary>
    public static double SendThreshold(int outs, double aggression, TacticsCoefficients c)
        => Threshold(outs, aggression, c.SendHomeMinSuccess, c.SendHomeTwoOutRelax, c.SendHomeAggressionSpan);

    /// <summary>生還推定確率と状況から、還す(true)か自重(false)かを決める。</summary>
    public static bool ShouldSend(double scoreProbability, int outs, double aggression, TacticsCoefficients c)
        => scoreProbability >= SendThreshold(outs, aggression, c);

    /// <summary>三塁送りの閾値（単打の一塁→三塁, Issue #89）。本塁と同型・係数のみ三塁固有。</summary>
    public static double SendThirdThreshold(int outs, double aggression, TacticsCoefficients c)
        => Threshold(outs, aggression, c.SendThirdMinSuccess, c.SendThirdTwoOutRelax, c.SendThirdAggressionSpan);

    /// <summary>三塁到達推定確率と状況から、回す(true)か自重(false)かを決める（Issue #89）。</summary>
    public static bool ShouldSendThird(double reachProbability, int outs, double aggression, TacticsCoefficients c)
        => reachProbability >= SendThirdThreshold(outs, aggression, c);

    /// <summary>
    /// ゴロ凡打の進塁打（二塁→三塁, Issue #318）の送り閾値（進塁見込みがこれ以上なら進塁を試みる）。
    /// **アウト数を取らない**: ゴロ凡打は currentOuts&gt;=2 で <see cref="Game.BaserunningModel"/> の ApplyInPlayOut が
    /// 即 return する（この進塁枝に到達するのは 0/1 アウトのみ）ため、2アウト緩和の枝は死にコードになる。
    /// aggression: 0=超慎重, 0.5=中立, 1=超積極。純関数・乱数不使用。
    /// </summary>
    public static double SendAdvanceThreshold(double aggression, TacticsCoefficients c)
    {
        var t = c.SendAdvanceMinSuccess;
        t -= (aggression - 0.5) * c.SendAdvanceAggressionSpan;
        return MathUtil.Clamp(t, 0.05, 0.95);
    }

    /// <summary>
    /// 犠飛以外のタッチアップ（一塁→二塁 / 二塁→三塁, design-14 P3-11）の送り閾値。
    /// 進塁打と同じく<b>アウト数を取らない</b>（<see cref="Game.BaserunningModel"/> の ApplyInPlayOut が
    /// currentOuts&gt;=2 で即 return するため2アウト緩和は死にコード）。閾値が他より高い理由は
    /// <see cref="TacticsCoefficients.SendTagUpMinSuccess"/> のコメント参照。純関数・乱数不使用。
    /// </summary>
    public static double SendTagUpThreshold(double aggression, TacticsCoefficients c)
    {
        var t = c.SendTagUpMinSuccess;
        t -= (aggression - 0.5) * c.SendTagUpAggressionSpan;
        return MathUtil.Clamp(t, 0.05, 0.95);
    }

    /// <summary>タッチアップの到達見込みと状況から、走る(true)か自重(false)かを決める。</summary>
    public static bool ShouldSendTagUp(double reachProbability, double aggression, TacticsCoefficients c)
        => reachProbability >= SendTagUpThreshold(aggression, c);

    /// <summary>送り閾値の共通式（本塁・三塁で係数だけ差し替え）。</summary>
    private static double Threshold(int outs, double aggression, double minSuccess, double twoOutRelax, double aggressionSpan)
    {
        var t = minSuccess;
        if (outs >= 2) t -= twoOutRelax;                // 2アウトは失うものが少なく積極的に送る
        t -= (aggression - 0.5) * aggressionSpan;
        return MathUtil.Clamp(t, 0.05, 0.95);
    }
}
