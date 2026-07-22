using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 試合間の回復モデル係数（設計書02 §1.1e・issue #41）。
/// 前登板からの休養日数と直近の球数から、目安投球数（<see cref="Players.PitcherAttributes.StaminaPitches"/>）の
/// 実効値がどれだけ落ちるかを決める。🟡 値は実測校正前のプレースホルダ（式のハードコード禁止＝不変条件#4）。
/// </summary>
public sealed record PitchRecoveryCoefficients
{
    /// <summary>これ以上の休養日数（前登板からの経過日）で完全回復＝実効＝目安投球数（中6日相当）。</summary>
    public double FullRecoveryDays { get; init; } = 7.0;

    /// <summary>「重い登板」の基準球数。直近球数がこれ以上で負荷は最大（1.0）に張り付く。</summary>
    public double ReferencePitches { get; init; } = 100.0;

    /// <summary>連投かつ重い登板が重なったときの最大減衰率（目安投球数に対する割合）。</summary>
    public double MaxReductionFraction { get; init; } = 0.5;
}

/// <summary>
/// 試合間の投手回復モデル（設計書02 §1.1e・issue #41）。純関数・決定論。
/// 「連戦（中0〜2日）でエースの実効スタミナが落ち、休養十分（中6日〜）ならフル回復」を表現する。
/// 本流（GameEngine）への配線は別issue（帯再校正とセット）。ここでは台帳→実効目安投球数の変換だけを提供する。
/// </summary>
public static class PitchRecoveryModel
{
    /// <summary>
    /// 休養日数×直近球数から実効の目安投球数を返す。
    /// restDays=前登板からの経過日（同日=0）、recentPitches=回復ウィンドウ内の累計球数。
    /// </summary>
    public static double EffectiveStaminaPitches(
        double baseStaminaPitches, int restDays, int recentPitches, PitchRecoveryCoefficients c)
    {
        // 完全回復（十分な休養）＝目安投球数そのまま。直近登板がゼロなら休養に関わらず減衰しない。
        if (restDays >= c.FullRecoveryDays || recentPitches <= 0 || baseStaminaPitches <= 0)
            return baseStaminaPitches;

        // 未回復率: 休養が短いほど1へ近づく（restDays=0 で1、FullRecoveryDays で0）。
        var unrecovered = MathUtil.Clamp(1.0 - restDays / c.FullRecoveryDays, 0.0, 1.0);
        // 直近負荷: 重い登板ほど1へ近づく（基準球数で頭打ち）。
        var load = c.ReferencePitches > 0
            ? MathUtil.Clamp(recentPitches / c.ReferencePitches, 0.0, 1.0)
            : 0.0;

        var reduction = baseStaminaPitches * c.MaxReductionFraction * unrecovered * load;
        return baseStaminaPitches - reduction;
    }
}
