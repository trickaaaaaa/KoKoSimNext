using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 投手疲労の係数（設計書02 §1.1e）。目安投球数（PitcherAttributes.StaminaPitches）を超えた
/// 球数に応じて球速天井・制球が低下する。目安投球数そのものは選手側が持つ。
/// </summary>
public sealed record FatigueCoefficients
{
    public double VelocityDropPerOverPitch { get; init; } = 0.05;   // km/h（目安超過1球あたり）
    public double ControlDropPerOverPitch { get; init; } = 0.12;    // 能力ポイント（目安超過1球あたり）
    public double RelievePitchMargin { get; init; } = 25.0;          // 目安を何球超えたら継投

    // --- 目安の手前から徐々に効く消耗カーブ（issue fatigue-gradual-before-threshold・設計書02 §1.1e） ---
    // 旧実装は目安投球数まで完全ゼロの階段型（スタミナLv80は112球目も1球目と同じ球威）。
    // 目安の OnsetRatio から二次カーブで低下を始め、目安到達時に VelocityDropAtGuidelineKmh / ControlDropAtGuideline、
    // 超過後は従来勾配（PerOverPitch）で**段差なく**続く。目安の意味は「球威が明確に落ち始める球数」。
    /// <summary>低下が始まる球数の比（目安投球数×これ）。1.0 以上なら従来の階段型に一致。</summary>
    public double OnsetRatio { get; init; } = 0.6;
    /// <summary>目安到達時の球速天井の低下[km/h]。</summary>
    public double VelocityDropAtGuidelineKmh { get; init; } = 2.0;
    /// <summary>目安到達時の制球の低下[能力ポイント]。</summary>
    public double ControlDropAtGuideline { get; init; } = 5.0;
    // ハードキャップ（旧 HardCapPitches=130）は撤廃（issue #356・2026-07-30 監督判定）。降板は目安投球数からの
    // 相対マージン（RelievePitchMargin）と疲労による能力低下（Effective の球速天井・制球低下）だけで決まる。
}

/// <summary>
/// スタミナ＝目安投球数の消耗カーブ（設計書02 §1.1e）。
/// 1球ごとに消耗し（ギア「飛ばす/流す」は消耗の重みで反映, §1.1f）、
/// 目安投球数を超えると球速天井（§1.1dのサンプリング上限）と制球が低下する。
/// </summary>
public static class PitchingFatigue
{
    /// <summary>本来の力を保てる球数（＝目安投球数）。</summary>
    public static double FreshPitches(PitcherAttributes p) => p.StaminaPitches;

    /// <summary>消耗を反映した実効投手能力（球速天井・制球を減衰）。fatiguePitches はギア重み込みの実効消費球数。</summary>
    public static PitcherAttributes Effective(Player pitcher, double fatiguePitches, FatigueCoefficients c)
        => Effective(pitcher.Pitching, fatiguePitches, c);

    /// <summary>
    /// 消耗の適用（表示→物理の効きを通した土台に対して。設計書21）。効きは制球のLvを寝かせるが、消耗の落ち幅は
    /// 「目安球数を超えた分」で決まる実測単位なので寝かせない＝効き→消耗の順で掛ける。
    /// </summary>
    public static PitcherAttributes Effective(PitcherAttributes basep, double fatiguePitches, FatigueCoefficients c)
    {
        var (velDrop, ctrlDrop) = Drops(fatiguePitches, basep.StaminaPitches, c);
        if (velDrop <= 0.0 && ctrlDrop <= 0.0) return basep;

        var vel = basep.MaxVelocityKmh - velDrop;
        var ctrl = (int)MathUtil.Clamp(basep.Control - ctrlDrop, 5, 100);
        return basep with
        {
            MaxVelocityKmh = System.Math.Max(105, vel),
            Control = ctrl,
        };
    }

    /// <summary>
    /// 消耗カーブ（issue fatigue-gradual-before-threshold）: 消費球数 → (球速天井の低下[km/h], 制球の低下[pt])。
    /// 目安×OnsetRatio まで 0、そこから目安まで二次カーブで目安時の低下量へ、超過後は従来勾配で連続。
    /// テストから直接呼べるよう Effective と分離。
    /// </summary>
    public static (double VelocityKmh, double Control) Drops(double fatiguePitches, double staminaPitches, FatigueCoefficients c)
    {
        var over = fatiguePitches - staminaPitches;
        if (over >= 0.0)
            return (c.VelocityDropAtGuidelineKmh + over * c.VelocityDropPerOverPitch,
                    c.ControlDropAtGuideline + over * c.ControlDropPerOverPitch);

        var onset = staminaPitches * MathUtil.Clamp(c.OnsetRatio, 0.0, 1.0);
        if (fatiguePitches <= onset || staminaPitches <= onset) return (0.0, 0.0);
        var t = (fatiguePitches - onset) / (staminaPitches - onset);   // 0..1
        var ramp = t * t;
        return (c.VelocityDropAtGuidelineKmh * ramp, c.ControlDropAtGuideline * ramp);
    }

    /// <summary>継投すべきか（実効消費球数ベース）。目安投球数＋マージンだけで判断（ハードキャップ撤廃, issue #356）。</summary>
    public static bool ShouldRelieve(Player pitcher, double fatiguePitches, FatigueCoefficients c)
    {
        var basep = pitcher.Pitching;
        return fatiguePitches >= basep.StaminaPitches + c.RelievePitchMargin;
    }
}
