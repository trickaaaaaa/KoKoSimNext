namespace KokoSim.Engine.Core;

/// <summary>
/// 決定論乱数源（不変条件#2）。
/// エンジン内の乱数は必ずこのインターフェース経由で取得し、<see cref="System.Random"/> の
/// 直接生成や <see cref="System.Guid"/> 由来の乱数は使用しない。
/// 同一シードは常に同一結果を返す。
/// </summary>
public interface IRandomSource
{
    /// <summary>64bit の一様乱数。すべての派生メソッドの土台。</summary>
    ulong NextUInt64();

    /// <summary>[0, 1) の一様乱数。</summary>
    double NextDouble();

    /// <summary>[minInclusive, maxExclusive) の整数一様乱数。</summary>
    int NextInt(int minInclusive, int maxExclusive);

    /// <summary>平均 mean・標準偏差 stdDev の正規乱数（コントロールσ散布などに使用）。</summary>
    double NextGaussian(double mean = 0.0, double stdDev = 1.0);

    /// <summary>
    /// streamId で決定される独立な派生ストリームを生成する。
    /// 打席・守備・イベントなど用途ごとに乱数列を分離しつつ決定論を保つために使う。
    /// </summary>
    IRandomSource Fork(ulong streamId);
}
