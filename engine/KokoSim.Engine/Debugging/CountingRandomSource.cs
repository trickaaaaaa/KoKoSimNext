using KokoSim.Engine.Core;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 乱数消費のトレース用デコレータ（設計書17 §4.5, F1・穴#7の解消）。
/// 値そのものは内側の <see cref="IRandomSource"/> をそのまま素通しするので、
/// <b>包んでも乱数列は1ビットも変わらない</b>（＝digest も帯も不変）。
///
/// <para><see cref="Fork"/> は子も包んで返し、消費本数は親子で共有カウンタに集計する。
/// 「決定論が壊れたときに、どの球で乱数消費数が食い違ったか」を <c>trace-diff</c> が指せるようにするための土台。
/// <c>CaptureTrace</c> が真のときだけ包む＝既定パスにはこの型が存在しない。</para>
/// </summary>
public sealed class CountingRandomSource : IRandomSource
{
    /// <summary>親子で共有する集計器（Fork した先の消費も1本の数字で追える）。</summary>
    public sealed class Counter
    {
        /// <summary>これまでに消費した乱数の本数（Fork先を含む）。</summary>
        public long Draws { get; internal set; }
        /// <summary>直近に Fork した派生ストリームのid（未Forkなら0）。</summary>
        public ulong LastForkStreamId { get; internal set; }
        /// <summary>Fork した回数。</summary>
        public int Forks { get; internal set; }
    }

    private readonly IRandomSource _inner;

    public CountingRandomSource(IRandomSource inner) : this(inner, new Counter(), 0UL) { }

    private CountingRandomSource(IRandomSource inner, Counter counter, ulong streamId)
    {
        _inner = inner;
        Stats = counter;
        StreamId = streamId;
    }

    /// <summary>共有カウンタ。Fork した子も同じ実体を指す。</summary>
    public Counter Stats { get; }

    /// <summary>この乱数源の派生元 streamId（ルートは0）。</summary>
    public ulong StreamId { get; }

    public ulong NextUInt64()
    {
        Stats.Draws++;
        return _inner.NextUInt64();
    }

    public double NextDouble()
    {
        Stats.Draws++;
        return _inner.NextDouble();
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        Stats.Draws++;
        return _inner.NextInt(minInclusive, maxExclusive);
    }

    public double NextGaussian(double mean = 0.0, double stdDev = 1.0)
    {
        Stats.Draws++;
        return _inner.NextGaussian(mean, stdDev);
    }

    public IRandomSource Fork(ulong streamId)
    {
        Stats.Forks++;
        Stats.LastForkStreamId = streamId;
        return new CountingRandomSource(_inner.Fork(streamId), Stats, streamId);
    }

    /// <summary>包まれている素の乱数源（RNG状態の捕捉など、実装固有の操作へ抜けるため）。</summary>
    public IRandomSource Unwrap() => _inner;

    /// <summary>rng が包まれていれば剥がす（多重に包まれていても剥がし切る）。</summary>
    public static IRandomSource UnwrapAll(IRandomSource rng)
    {
        while (rng is CountingRandomSource c) rng = c.Unwrap();
        return rng;
    }
}
