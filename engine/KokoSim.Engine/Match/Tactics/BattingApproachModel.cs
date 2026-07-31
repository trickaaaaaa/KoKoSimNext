using System;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 打撃姿勢（<see cref="BattingApproach"/>）の効果（2026-07-31・采配カードの拡充）。
/// 打席頭に一度だけ効く純関数で、効果は次の2つにしか落ちない:
/// ① 実効能力（ミート/パワー/弾道）への倍率 ② スイング頻度の倍率。
///
/// <para>確率へ直接触らず、表示能力→物理層の変換の前段で能力を動かすだけ（不変条件#1）。
/// エンドラン（<see cref="TacticsCoefficients.HitAndRunContactBoost"/> 等）とまったく同じ流儀で、
/// 既定 <see cref="BattingApproach.Normal"/> は恒等＝AI駆動のシムは1ビットも変わらない。</para>
/// </summary>
public static class BattingApproachModel
{
    /// <summary>打撃姿勢を実効打者能力へ載せる。<see cref="BattingApproach.Normal"/>／待球策は恒等。</summary>
    public static BatterAttributes Apply(BatterAttributes batter, BattingApproach approach, TacticsCoefficients c)
        => approach switch
        {
            BattingApproach.GroundBall => batter with
            {
                Contact = Clamp(batter.Contact * (1.0 + c.GroundBallContactBoost)),
                Power = Clamp(batter.Power * (1.0 - c.GroundBallPowerPenalty)),
                LaunchTendency = Clamp(batter.LaunchTendency * (1.0 - c.GroundBallLaunchPenalty)),
            },
            BattingApproach.Slug => batter with
            {
                Contact = Clamp(batter.Contact * (1.0 - c.SlugContactPenalty)),
                Power = Clamp(batter.Power * (1.0 + c.SlugPowerBoost)),
                LaunchTendency = Clamp(batter.LaunchTendency * (1.0 + c.SlugLaunchBoost)),
            },
            BattingApproach.PutInPlay => batter with
            {
                Contact = Clamp(batter.Contact * (1.0 + c.PutInPlayContactBoost)),
                Power = Clamp(batter.Power * (1.0 - c.PutInPlayPowerPenalty)),
            },
            _ => batter,   // Normal / WorkCount は能力を動かさない
        };

    /// <summary>スイング頻度の倍率（待球策だけ 1 未満・他は 1.0＝恒等）。</summary>
    public static double SwingFactor(BattingApproach approach, TacticsCoefficients c)
        => approach == BattingApproach.WorkCount ? c.WorkCountSwingFactor : 1.0;

    private static int Clamp(double v) => Math.Clamp((int)Math.Round(v), 1, 100);
}
