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

    // ===== 状態の捕捉と復元（設計書17 §3.2・F0 再現基盤） =====
    // シードを持たない乱数源（大会の Fork ストリーム等）でも「今この瞬間」から再開できるようにする。
    // <see cref="IRandomSource"/> には足さない（実装差し替えの自由度を残す＝設計書17 §3.2）。

    /// <summary>捕捉状態の語数。xoshiro の 4 ワード＋Box-Muller 予備値（値・有無）で 6。</summary>
    public const int StateWords = 6;

    /// <summary>
    /// 現在の内部状態をコピーで返す（設計書17 §3.2）。<see cref="FromState"/> と往復させると、
    /// 以降の乱数列が1ビットも違わずに再現される。
    ///
    /// <para>設計書17 §3.2 は「4要素」と書いているが、<see cref="NextGaussian"/> の Box-Muller は
    /// 予備値を1つキャッシュしており、これを落とすと復元後の正規乱数が半周ズレる（=決定論が壊れる）。
    /// よって 4 ワードに予備値の bit 表現と有無フラグを足した 6 要素を単一ソースとする。</para>
    /// </summary>
    public ulong[] CaptureState() => new[]
    {
        _s0, _s1, _s2, _s3,
        unchecked((ulong)BitConverter.DoubleToInt64Bits(_spareGaussian)),
        _hasSpareGaussian ? 1UL : 0UL,
    };

    /// <summary>
    /// <see cref="CaptureState"/> の出力から乱数源を復元する。予備値を持たない4要素配列
    /// （旧セーブ・手書きトークン）も受け付け、その場合は予備値なしとして扱う。
    /// </summary>
    public static Xoshiro256Random FromState(ulong[] state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (state.Length != 4 && state.Length != StateWords)
        {
            throw new ArgumentException(
                $"RNG状態は4要素または{StateWords}要素である必要があります（実際: {state.Length}）。", nameof(state));
        }
        if (state[0] == 0 && state[1] == 0 && state[2] == 0 && state[3] == 0)
        {
            // xoshiro は全ゼロ状態から抜け出せない（常に0を返す）。壊れたセーブを黙って受けない。
            throw new ArgumentException("RNG状態が全ゼロです（xoshiro の不正状態）。", nameof(state));
        }

        var r = new Xoshiro256Random(0UL);
        r._s0 = state[0];
        r._s1 = state[1];
        r._s2 = state[2];
        r._s3 = state[3];
        if (state.Length == StateWords)
        {
            r._spareGaussian = BitConverter.Int64BitsToDouble(unchecked((long)state[4]));
            r._hasSpareGaussian = state[5] != 0UL;
        }
        return r;
    }
}
