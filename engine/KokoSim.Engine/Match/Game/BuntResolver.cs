using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>バントの結果分布（設計書02 §4.3）。</summary>
public enum BuntResult
{
    SacrificeSuccess, // 犠打成功（走者進塁・打者アウト）
    InfieldHit,       // 内野安打（セーフティ成功）
    PopOut,           // 小フライ（危険なアウト）
    Foul,             // ファウル（カウントのみ進む）
    MissedBunt,       // 空振り
}

/// <summary>
/// バント／セーフティバントの解決（設計書02 §4.3）。バントパラメータが主要因（ミートは僅か）。
/// 基礎成功率 = BuntBase + Bunt×Slope − 球速補正。結果は分布で返す。
/// スクイズは <see cref="SqueezeResolver"/> がこれを内部利用する。
/// </summary>
public static class BuntResolver
{
    /// <summary>バント成功率（犠打が転がる確率。分岐後の成功判定に使う）。</summary>
    public static double SuccessRate(Player batter, PitcherAttributes pitcher, BaserunningCoefficients c)
    {
        var veloPenalty = System.Math.Max(0.0, pitcher.MaxVelocityKmh - c.BuntVelocityRefKmh)
                          * c.BuntVelocityPenaltyPerKmh;
        // 性格④（設計書01 §1.1）: 自己犠牲型は+、目立ちたがりは−。BuntSuccessBonus は投影時に性格表から解決済み（既定0）。
        return MathUtil.Clamp(
            c.BuntBase + batter.Bunt * c.BuntSkillSlope - veloPenalty + batter.BuntSuccessBonus, 0.05, 0.98);
    }

    /// <summary>
    /// バント内野安打確率（設計書02 §4.1b, Step4-②）。犠打・セーフティ共通で
    /// 「打者走者→一塁」対「バント処理→一塁送球」の秒勝負を logistic に通す。
    /// 犠打は構え遅延が大きく、走力が低いとほぼ0%（＝従来挙動）だが、快足×好バントは送りでも数%生きる。
    /// </summary>
    public static double InfieldHitProbability(Player batter, bool safety, BaserunningCoefficients c)
    {
        var handednessBonus = batter.Bats is Handedness.Left or Handedness.Switch
            ? c.BuntLeftFirstStepBonusSeconds : 0.0;
        var squareDelay = safety ? c.SafetyBuntSquareDelaySeconds : c.SacrificeBuntSquareDelaySeconds;
        var batterTime = c.BuntBaseDistanceM / batter.ToBatter().SpeedToFirstMps()
                         + c.BuntRunnerReactionSeconds + squareDelay - handednessBonus;
        var defenseTime = c.BuntFieldThrowBaseSeconds + (batter.Bunt - 50) * c.BuntPlacementSlope;
        var margin = defenseTime - batterTime;
        return MathUtil.Clamp(MathUtil.Logistic(margin / c.BuntInfieldHitTimeScale), 0.01, 0.95);
    }

    /// <summary>
    /// バント1回を解決。safety=true はセーフティ（内野安打を積極的に狙う）。
    /// 分岐: 空振り / ファウル(技術で減少) / 小フライ / （成功判定）内野安打(時間勝負) or 犠打成功 / 失敗=小フライ。
    /// </summary>
    public static BuntResult Resolve(Player batter, PitcherAttributes pitcher, bool safety,
        BaserunningCoefficients c, IRandomSource rng)
    {
        if (MathUtil.Chance(c.BuntMissShare, rng)) return BuntResult.MissedBunt;

        // ファウル: 上手い打者ほど減る（設計書02 §4.3, Step4-③）。
        var foulShare = System.Math.Max(c.BuntFoulFloor,
            c.BuntFoulShare - (batter.Bunt - 50) * c.BuntFoulSkillSlope);
        if (MathUtil.Chance(foulShare, rng)) return BuntResult.Foul;
        if (MathUtil.Chance(c.BuntPopShare, rng)) return BuntResult.PopOut;

        if (!MathUtil.Chance(SuccessRate(batter, pitcher, c), rng))
            return BuntResult.PopOut; // 転がしそこねの凡打

        // 犠打・セーフティ共通の内野安打判定（時間軸）。セーフティは構え遅延が小さく生きやすい。
        if (MathUtil.Chance(InfieldHitProbability(batter, safety, c), rng)) return BuntResult.InfieldHit;
        return BuntResult.SacrificeSuccess;
    }
}
