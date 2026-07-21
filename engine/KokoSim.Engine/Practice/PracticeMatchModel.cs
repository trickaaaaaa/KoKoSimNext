using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;

namespace KokoSim.Engine.Practice;

/// <summary>
/// 練習試合の申込・受諾モデル（設計書04 §名声の効果＝名声は「練習試合の申込」に効く）。
/// 純関数＋シード付き乱数のみ（不変条件#2）。ティア差と名声から受諾確率を決める。
/// </summary>
public static class PracticeMatchModel
{
    /// <summary>
    /// 受諾確率。相手が格上（ティアが上）なほど下がり、監督の名声が高いほど上がる。
    /// 格下相手はティア差の減点を受けない（断る理由がない）。
    /// </summary>
    public static double AcceptChance(Tier managerTier, Tier opponentTier, double fame, PracticeMatchCoefficients c)
    {
        var gap = Math.Max(0, (int)opponentTier - (int)managerTier);
        var p = c.BaseAccept - c.TierGapPenalty * gap + c.FameWeight * (Math.Clamp(fame, 0, 100) / 100.0);
        return Math.Clamp(p, c.MinAccept, c.MaxAccept);
    }

    /// <summary>受諾判定を1回引く。同シード同結果（不変条件#2）。</summary>
    public static bool Accepts(Tier managerTier, Tier opponentTier, double fame,
        PracticeMatchCoefficients c, IRandomSource rng)
        => rng.NextDouble() < AcceptChance(managerTier, opponentTier, fame, c);
}
