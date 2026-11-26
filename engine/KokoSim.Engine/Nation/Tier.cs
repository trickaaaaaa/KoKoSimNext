using System;

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

    /// <summary>画面に出す総合点（四捨五入した整数）。点数とランクを並べて出す画面はこれを通す。</summary>
    public static int DisplayPoints(double strength) => (int)Math.Round(strength);

    /// <summary>
    /// 画面に出す点数（<see cref="DisplayPoints"/>）から決めたティア。49.6 は点数「50」＝D になる。
    /// 点数とランクを並べて出す表示専用で、AI のティア判定などは区切りを動かさない <see cref="FromStrength"/> のまま。
    /// </summary>
    public static Tier FromDisplayed(double strength) => FromStrength(DisplayPoints(strength));
}
