using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 投手疲労の係数（設計書02 §1.1e）。目安投球数（PitcherAttributes.StaminaPitches）を超えた
/// 球数に応じて球速天井・制球が低下する。目安投球数そのものは選手側が持つ。
/// </summary>
public sealed record FatigueCoefficients
{
    public double VelocityDropPerOverPitch { get; init; } = 0.05;   // km/h
    public double ControlDropPerOverPitch { get; init; } = 0.12;    // 能力ポイント
    public double RelievePitchMargin { get; init; } = 25.0;          // 目安を何球超えたら継投
    public double HardCapPitches { get; init; } = 130.0;
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
    {
        var basep = pitcher.Pitching ?? PitcherAttributes.LeagueAverage;
        var over = fatiguePitches - basep.StaminaPitches;
        if (over <= 0) return basep;

        var vel = basep.MaxVelocityKmh - over * c.VelocityDropPerOverPitch;
        var ctrl = (int)MathUtil.Clamp(basep.Control - over * c.ControlDropPerOverPitch, 5, 100);
        return basep with
        {
            MaxVelocityKmh = System.Math.Max(105, vel),
            Control = ctrl,
        };
    }

    /// <summary>継投すべきか（実効消費球数ベース）。</summary>
    public static bool ShouldRelieve(Player pitcher, double fatiguePitches, FatigueCoefficients c)
    {
        var basep = pitcher.Pitching ?? PitcherAttributes.LeagueAverage;
        return fatiguePitches >= basep.StaminaPitches + c.RelievePitchMargin
               || fatiguePitches >= c.HardCapPitches;
    }
}
