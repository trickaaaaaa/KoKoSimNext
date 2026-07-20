using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 牽制アウト／離塁刺殺の解決（design-14 P1-5）。盗塁企図の裏で、投手の牽制の鋭さ（Mental を代理指標とする）
/// と走者のリード幅（Steal を代理指標とする）から低確率の刺殺を判定する。StealResolver の「時間の勝負」とは
/// 異なり単発のベルヌーイ試行（<see cref="Match.Game.BaserunningModel"/> の FieldersChoiceProb と同型）。
/// </summary>
public static class PickoffResolver
{
    public static double Probability(Player runner, Player pitcher, BaserunningCoefficients c)
        => MathUtil.Clamp(
            c.PickoffBaseProb
            + (runner.Steal - 50) * c.PickoffRunnerLeadSlope
            - (pitcher.Mental - 50) * c.PickoffPitcherSenseSlope,
            0.0, c.PickoffMaxProb);

    /// <summary>既定0（<see cref="BaserunningCoefficients.PickoffBaseProb"/>）ではガードにより
    /// rng を一切消費せず常に false（＝呼び出し元の盗塁判定と乱数消費順・結果とも完全一致）。</summary>
    public static bool Resolve(Player runner, Player pitcher, BaserunningCoefficients c, IRandomSource rng)
        => c.PickoffBaseProb > 0.0 && MathUtil.Chance(Probability(runner, pitcher, c), rng);
}
