namespace KokoSim.Engine.Nation;

/// <summary>学校の強さティア（設計書05 §2.2, 能力等級と同体系 G〜S）。</summary>
public enum Tier
{
    G, F, E, D, C, B, A, S,
}

public static class Tiers
{
    /// <summary>チーム力(0〜100)からティアを求める（能力等級と同じ帯, 設計書02 §1.1）。</summary>
    public static Tier FromStrength(double strength) => strength switch
    {
        >= 90 => Tier.S,
        >= 80 => Tier.A,
        >= 70 => Tier.B,
        >= 60 => Tier.C,
        >= 50 => Tier.D,
        >= 40 => Tier.E,
        >= 30 => Tier.F,
        _ => Tier.G,
    };
}
