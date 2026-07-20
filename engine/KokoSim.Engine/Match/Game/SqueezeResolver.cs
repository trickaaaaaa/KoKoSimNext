using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>スクイズの結果。</summary>
public readonly record struct SqueezeOutcome(int Runs, bool BatterOut, bool RunnerOut, BuntResult Bunt);

/// <summary>
/// スクイズの解決（設計書02 §4.4）。バント成功率 × 三塁走者スタート(走塁判断) × ウエスト判定。
/// ウエスト（外し）: 相手バッテリーが察知するとボールを外し、三本間挟殺の大惨事になり得る。
/// 現フェーズは配球AI未接続のため wasteProbability を外から与える（既定0＝外さない）。
/// </summary>
public static class SqueezeResolver
{
    public static SqueezeOutcome Resolve(Player batter, Player thirdRunner, PitcherAttributes pitcher,
        double wasteProbability, BaserunningCoefficients c, IRandomSource rng)
    {
        // ウエストを読まれた場合: 打者はバントできず、飛び出した三塁走者が挟殺される大惨事。
        if (MathUtil.Chance(MathUtil.Clamp(wasteProbability, 0.0, 1.0), rng))
        {
            return new SqueezeOutcome(0, BatterOut: false, RunnerOut: true, BuntResult.MissedBunt);
        }

        var bunt = BuntResolver.Resolve(batter, pitcher, safety: false, c, rng);
        switch (bunt)
        {
            case BuntResult.SacrificeSuccess:
            case BuntResult.InfieldHit:
                // 三塁走者は走塁判断でスタートを切れており生還。打者は犠打なら1アウト、内野安打なら生きる。
                return new SqueezeOutcome(1, BatterOut: bunt == BuntResult.SacrificeSuccess, RunnerOut: false, bunt);
            case BuntResult.PopOut:
                // 小フライ→本塁封殺 or 併殺の危険。三塁走者アウト・打者もアウト気味だが簡易に走者アウト。
                return new SqueezeOutcome(0, BatterOut: true, RunnerOut: false, bunt);
            default: // Foul / MissedBunt: 三塁走者が飛び出していれば挟殺。簡易に走者アウト。
                return new SqueezeOutcome(0, BatterOut: false, RunnerOut: true, bunt);
        }
    }
}
