namespace KokoSim.Engine.Core;

/// <summary>確率・幾何計算の共通ヘルパ。</summary>
public static class MathUtil
{
    /// <summary>ロジスティック関数 1/(1+e^-x)。対数オッズ→確率。</summary>
    public static double Logistic(double x) => 1.0 / (1.0 + Math.Exp(-x));

    public static double Clamp(double v, double min, double max)
        => v < min ? min : (v > max ? max : v);

    /// <summary>確率 p のベルヌーイ試行（注入乱数）。</summary>
    public static bool Chance(double p, IRandomSource rng) => rng.NextDouble() < p;
}
