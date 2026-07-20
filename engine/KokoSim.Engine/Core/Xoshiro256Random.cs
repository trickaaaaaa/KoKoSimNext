namespace KokoSim.Engine.Core;

/// <summary>
/// xoshiro256** に基づく決定論乱数源。シードは splitmix64 で 256bit 状態へ展開する。
/// プラットフォーム非依存（純粋な整数演算のみ）で、同一シード同一結果を保証する。
/// </summary>
public sealed class Xoshiro256Random : IRandomSource
{
    private ulong _s0, _s1, _s2, _s3;

    // Box-Muller の予備値キャッシュ。
    private double _spareGaussian;
    private bool _hasSpareGaussian;

    public Xoshiro256Random(ulong seed)
    {
        // splitmix64 でシードを 4 ワードへ展開（xoshiro 作者推奨の初期化）。
        var sm = seed;
        _s0 = SplitMix64(ref sm);
        _s1 = SplitMix64(ref sm);
        _s2 = SplitMix64(ref sm);
        _s3 = SplitMix64(ref sm);
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5UL, 7) * 9UL;
        var t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);

        return result;
    }

    public double NextDouble()
    {
        // 上位 53bit を [0,1) の double に変換（IEEE754 の仮数幅に合わせ一様）。
        return (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minInclusive), "minInclusive は maxExclusive より小さい必要があります。");
        }

        var range = (ulong)((long)maxExclusive - minInclusive);
        // Lemire の偏りなし範囲縮約。
        var m = NextUInt64() % range;
        return minInclusive + (int)m;
    }

    public double NextGaussian(double mean = 0.0, double stdDev = 1.0)
    {
        if (_hasSpareGaussian)
        {
            _hasSpareGaussian = false;
            return mean + stdDev * _spareGaussian;
        }

        // Box-Muller 法。u1 は 0 を避ける。
        double u1, u2;
        do
        {
            u1 = NextDouble();
        } while (u1 <= double.Epsilon);
        u2 = NextDouble();

        var mag = Math.Sqrt(-2.0 * Math.Log(u1));
        var z0 = mag * Math.Cos(2.0 * Math.PI * u2);
        var z1 = mag * Math.Sin(2.0 * Math.PI * u2);

        _spareGaussian = z1;
        _hasSpareGaussian = true;
        return mean + stdDev * z0;
    }

    public IRandomSource Fork(ulong streamId)
    {
        // 現在状態と streamId を混合した新シードで独立ストリームを作る。
        var mixed = _s0 ^ RotateLeft(streamId + 0x9E3779B97F4A7C15UL, 32);
        return new Xoshiro256Random(mixed);
    }
}
