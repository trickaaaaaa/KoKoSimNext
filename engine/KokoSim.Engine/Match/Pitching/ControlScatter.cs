using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// コントロール由来の投球散布（設計書01 §2②）。狙ったコースからの誤差を2次元正規分布でサンプリングする。
/// 乱数は必ず注入された IRandomSource を使う（不変条件#2）。
/// </summary>
public static class ControlScatter
{
    /// <summary>本塁面での実着弾位置(X=左右, Y=高さ)[m]。</summary>
    public readonly struct Location
    {
        public readonly double X;
        public readonly double Y;
        public Location(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// 狙い位置(aimX, aimY)[m]・コントロール値・係数から、実際の着弾位置をサンプリングする。
    /// 横・縦それぞれ独立に N(0, σ) の誤差を加える。
    /// </summary>
    public static Location Sample(
        double aimX,
        double aimY,
        int control,
        PitchingCoefficients coefficients,
        IRandomSource rng)
    {
        var sigma = coefficients.ControlSigmaMeters(control);
        var dx = rng.NextGaussian(0.0, sigma);
        var dy = rng.NextGaussian(0.0, sigma);
        return new Location(aimX + dx, aimY + dy);
    }
}
