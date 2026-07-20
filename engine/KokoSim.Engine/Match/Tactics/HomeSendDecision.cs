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
    /// 送りの閾値（生還推定確率がこれ以上なら還す）。
    /// aggression: 0=超慎重, 0.5=中立, 1=超積極（機動力校・攻めの監督）。
    /// </summary>
    public static double SendThreshold(int outs, double aggression, TacticsCoefficients c)
    {
        var t = c.SendHomeMinSuccess;
        if (outs >= 2) t -= c.SendHomeTwoOutRelax;      // 2アウトは失うものが少なく積極的に還す
        t -= (aggression - 0.5) * c.SendHomeAggressionSpan;
        return MathUtil.Clamp(t, 0.05, 0.95);
    }

    /// <summary>生還推定確率と状況から、還す(true)か自重(false)かを決める。</summary>
    public static bool ShouldSend(double scoreProbability, int outs, double aggression, TacticsCoefficients c)
        => scoreProbability >= SendThreshold(outs, aggression, c);
}
