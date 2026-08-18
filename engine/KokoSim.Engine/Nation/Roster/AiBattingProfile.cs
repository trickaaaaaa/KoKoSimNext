using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校の選手の打撃・技術5能力（ミート/パワー/選球眼/守備/捕球）の凸凹配分の重み（issue ai-roster-power-top-tier-all-sluggers, 2026-08-18）。
///
/// <para>自校の <see cref="ProspectGenerator"/> は総合力を重み exp(N(0,σ)) で9技術へ配分する（設計書04 §2.1）。AI校は
/// 全能力を同じ中心で独立に引いていたので、B/A校の先発は全員が中心どおりの万能型だった。ここは同じ作法の重み
/// （平均1に正規化＝総和不変）を専用 Fork から引く。σ は <see cref="RosterCoefficients.AiConcentration"/>。</para>
///
/// <para>⚠ 重みは平均を保つので、パワーだけを希少にするのは <see cref="RosterCoefficients.AiPowerSkew"/>（中心の振り替え）
/// の役目。本塁打率はパワーに凸なので、散らすだけでは本塁打は増える。</para>
/// </summary>
public readonly record struct AiBattingProfile(double Contact, double Power, double Discipline, double Fielding, double Catching)
{
    /// <summary>プロフィール用の Fork ソルト（性格 salt／潜在投手 salt／精神 salt と衝突しない上位ビット）。</summary>
    internal const ulong ProfileSalt = 0x5A2C_0000_0000_0000UL;

    public static AiBattingProfile Flat => new(1, 1, 1, 1, 1);

    /// <summary>重み exp(N(0,σ)) を5本引き平均1に正規化する（σ≤0 なら恒等）。fork で5回だけ引く＝消費数固定。</summary>
    public static AiBattingProfile Draw(IRandomSource fork, RosterCoefficients rc)
    {
        var sigma = rc.AiConcentration;
        if (sigma <= 0.0) return Flat;
        var w = new double[5];
        var sum = 0.0;
        for (var i = 0; i < 5; i++)
        {
            w[i] = Math.Exp(fork.NextGaussian(0, sigma));
            sum += w[i];
        }
        var mean = sum / 5.0;
        return new AiBattingProfile(w[0] / mean, w[1] / mean, w[2] / mean, w[3] / mean, w[4] / mean);
    }
}
