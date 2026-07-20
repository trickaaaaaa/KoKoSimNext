using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 盗塁に対する守備の読み／ピッチアウト（設計書09 §1, 設計書12 §5, G3）。
/// 「捕手リード＋投手センス vs 状況の意外性」を確率化する。読まれた盗塁はピッチアウトで捕手が
/// 優位に立ち（送球が速くなる）刺されやすい。意表（低い企図度・ギャンブル始動）は読まれにくい。
/// これで「意図的な意表が警戒薄を出し抜く／セオリー盗塁が読まれて憤死」の対称が完成する（Q10）。
/// 純関数＋注入乱数（決定論）。実際の刺殺/生還は <see cref="StealResolver"/> の時間の勝負で解決する。
/// </summary>
public static class StealReadModel
{
    /// <summary>
    /// この盗塁企図の「予想されやすさ」E∈[0,1]。快足の盗塁屋がセオリー通りに走るほど高く（読まれやすい）、
    /// 鈍足の意表・ギャンブル始動ほど低い（読まれにくい）。守備が読める上限を状況側から与える指標。
    /// </summary>
    public static double Expectedness(Player runner, StartType startType, BaserunningCoefficients c)
    {
        var e = c.StealExpectednessIntercept + (runner.Steal - 50) * c.StealExpectednessStealSlope;
        if (startType == StartType.Gamble) e -= c.GambleUnexpectednessReduction;
        return MathUtil.Clamp(e, 0.0, 1.0);
    }

    /// <summary>守備がピッチアウトで読み切る確率。読み力（捕手リード＋投手センス）×予想度で、上限クランプ。
    /// 予想度が低い意表は、読み力が高くても読み切れない（掛け算で0へ寄る）。</summary>
    public static double PitchoutProbability(Player catcher, Player pitcher, double expectedness, BaserunningCoefficients c)
    {
        var readSkill = MathUtil.Clamp(
            c.StealReadIntercept
            + (catcher.Lead - 50) * c.StealReadCatcherLeadSlope
            + (pitcher.Mental - 50) * c.StealReadPitcherSenseSlope,
            0.0, 1.0);
        return MathUtil.Clamp(readSkill * expectedness, 0.0, c.MaxPitchoutProb);
    }

    /// <summary>ピッチアウトが出たか（＝守備が読み切ったか）。決定論・乱数注入。</summary>
    public static bool RollPitchout(
        Player runner, Player catcher, Player pitcher, StartType startType,
        BaserunningCoefficients c, IRandomSource rng)
    {
        var expectedness = Expectedness(runner, startType, c);
        return MathUtil.Chance(PitchoutProbability(catcher, pitcher, expectedness, c), rng);
    }
}
