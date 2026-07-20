using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>プレッシャー指数の係数（設計書02 §3.1-3.2, YAML駆動）。</summary>
public sealed record PressureCoefficients
{
    /// <summary>終盤加点の開始イニング（8回以降 +1）。</summary>
    public int LateInningFrom { get; init; } = 8;
    /// <summary>接戦加点の点差（2点差以内 +1）。</summary>
    public int CloseScoreDiff { get; init; } = 2;
    /// <summary>得点圏走者 +1（満塁なら +2）。</summary>
    public int RispPoint { get; init; } = 1;
    public int BasesLoadedPoint { get; init; } = 2;
    /// <summary>負けたら引退（3年夏）+1。</summary>
    public int RetirementPoint { get; init; } = 1;
    /// <summary>指数の上限（0〜8）。</summary>
    public int MaxIndex { get; init; } = 8;

    /// <summary>補正式の傾き: 倍率 = 1 + (精神力−50)/50 × Slope × P（精神力100・P8で+16%）。</summary>
    public double MultiplierSlope { get; init; } = 0.02;
    /// <summary>疲労が閾値超過時、負側のみ増幅する倍率（終盤の失点劇）。</summary>
    public double FatigueNegativeAmplify { get; init; } = 1.5;
}

/// <summary>プレッシャー算出に必要な試合状況（試合エンジンが打席ごとに構成）。</summary>
public readonly record struct PressureSituation(
    int StageBonus,          // 大会段階（練習試合0 / 予選+1 / 甲子園+2 / 決勝+3）
    int Inning,
    int ScoreDiffAbs,        // 得失点差の絶対値
    bool RunnerOnSecond,
    bool RunnerOnThird,
    bool BasesLoaded,
    bool RetirementOnLine);  // 負けたら引退（3年夏）

/// <summary>
/// 精神力の発動（設計書02 §3）。状況からプレッシャー指数P（0〜8）を自動算出し、
/// 能力補正倍率 = 1 + (精神力−50)/50 × 0.02 × P を実効能力に掛ける（補正係数方式＝パイプライン不変）。
/// 精神力50なら常に恒等 → 既存の統計帯を変えない。伝令・主将は mitigation（負側の緩和）で接続する。
/// </summary>
public static class PressureModel
{
    /// <summary>プレッシャー指数P（0〜8）。</summary>
    public static int Compute(in PressureSituation s, PressureCoefficients c)
    {
        var p = s.StageBonus;
        if (s.Inning >= c.LateInningFrom) p += 1;
        if (s.ScoreDiffAbs <= c.CloseScoreDiff) p += 1;
        if (s.BasesLoaded) p += c.BasesLoadedPoint;
        else if (s.RunnerOnSecond || s.RunnerOnThird) p += c.RispPoint;
        if (s.RetirementOnLine) p += c.RetirementPoint;
        return Math.Clamp(p, 0, c.MaxIndex);
    }

    /// <summary>
    /// 能力補正倍率。mitigation（0〜1, 伝令・主将による負側の緩和）は倍率が1未満のときだけ効く。
    /// fatigueOver=true（疲労閾値超過）と negativeAmplify（動揺, 設計書09 §3）は負側のみ増幅。
    /// 精神力50なら常に1.0。
    /// </summary>
    public static double Multiplier(int mental, int pressureIndex, PressureCoefficients c,
        double mitigation = 0.0, bool fatigueOver = false, double negativeAmplify = 1.0)
    {
        var raw = (mental - 50) / 50.0 * c.MultiplierSlope * pressureIndex;
        if (raw < 0)
        {
            raw *= 1.0 - MathUtil.Clamp(mitigation, 0.0, 1.0);
            if (fatigueOver) raw *= c.FatigueNegativeAmplify;
            raw *= negativeAmplify;
        }
        return 1.0 + raw;
    }

    /// <summary>打者へ適用（コンタクト率の元となるミートに補正）。倍率1.0なら恒等。</summary>
    public static BatterAttributes ApplyBatter(BatterAttributes b, double multiplier)
    {
        if (multiplier == 1.0) return b;
        return b with { Contact = ClampAbility(b.Contact * multiplier) };
    }

    /// <summary>投手へ適用（コントロールσに補正）。倍率1.0なら恒等。</summary>
    public static PitcherAttributes ApplyPitcher(PitcherAttributes p, double multiplier)
    {
        if (multiplier == 1.0) return p;
        return p with { Control = ClampAbility(p.Control * multiplier) };
    }

    private static int ClampAbility(double v) => Math.Clamp((int)Math.Round(v), 1, 100);
}
