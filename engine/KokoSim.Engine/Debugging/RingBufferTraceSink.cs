using System.Collections.Generic;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 直近N球・N打席をメモリに保持するシンク（設計書17 §4.2, F3）。デバッグHUDのデータ源。
/// HUD は<b>表示専用</b>で、ここから読むだけ＝engine を1回も追加で呼ばない（結果は変わらない）。
///
/// <para>設計書17 §4.2 では Unity 側の部品として書かれているが、IO も Unity 参照も持たない純データ構造なので
/// engine 側に置いてテストで固定する（不変条件#3を守ったまま、リングの巻き戻り境界を回帰にできる）。</para>
/// </summary>
public sealed class RingBufferTraceSink : IDebugTraceSink
{
    private readonly PitchTrace?[] _pitches;
    private readonly PaTrace?[] _pas;
    private int _pitchHead;
    private int _paHead;

    public RingBufferTraceSink(int pitchCapacity = 64, int paCapacity = 16)
    {
        if (pitchCapacity <= 0) throw new System.ArgumentOutOfRangeException(nameof(pitchCapacity));
        if (paCapacity <= 0) throw new System.ArgumentOutOfRangeException(nameof(paCapacity));
        _pitches = new PitchTrace?[pitchCapacity];
        _pas = new PaTrace?[paCapacity];
    }

    public int PitchCapacity => _pitches.Length;
    public int PaCapacity => _pas.Length;

    /// <summary>この試合のヘッダ（再現トークンの土台）。</summary>
    public GameTraceHeader? Header { get; private set; }

    /// <summary>試合が終わっていれば最終結果。</summary>
    public GameResult? Result { get; private set; }

    /// <summary>これまでに観測した総球数（リングの容量とは無関係）。</summary>
    public long TotalPitches { get; private set; }
    public long TotalPlateAppearances { get; private set; }

    /// <summary>直近の1球（未観測なら null）。HUD の「今の球」ペイン。</summary>
    public PitchTrace? Latest => TotalPitches == 0 ? null : _pitches[(_pitchHead - 1 + _pitches.Length) % _pitches.Length];

    /// <summary>直近 <paramref name="n"/> 球を<b>古い順</b>で返す（HUD の球ログ表）。</summary>
    public IReadOnlyList<PitchTrace> RecentPitches(int n)
    {
        var count = (int)System.Math.Min(System.Math.Min(n, _pitches.Length), TotalPitches);
        var list = new List<PitchTrace>(count);
        for (var i = count; i >= 1; i--)
        {
            var t = _pitches[(_pitchHead - i + _pitches.Length * 2) % _pitches.Length];
            if (t is not null) list.Add(t);
        }
        return list;
    }

    /// <summary>直近 <paramref name="n"/> 打席を<b>古い順</b>で返す。</summary>
    public IReadOnlyList<PaTrace> RecentPlateAppearances(int n)
    {
        var count = (int)System.Math.Min(System.Math.Min(n, _pas.Length), TotalPlateAppearances);
        var list = new List<PaTrace>(count);
        for (var i = count; i >= 1; i--)
        {
            var t = _pas[(_paHead - i + _pas.Length * 2) % _pas.Length];
            if (t is not null) list.Add(t);
        }
        return list;
    }

    public void OnGameStart(GameTraceHeader header)
    {
        // 試合が変わったらリングを空にする（前の試合の球が HUD に残らないように）。
        Header = header;
        Result = null;
        System.Array.Clear(_pitches, 0, _pitches.Length);
        System.Array.Clear(_pas, 0, _pas.Length);
        _pitchHead = 0;
        _paHead = 0;
        TotalPitches = 0;
        TotalPlateAppearances = 0;
    }

    public void OnPitch(PitchTrace t)
    {
        _pitches[_pitchHead] = t;
        _pitchHead = (_pitchHead + 1) % _pitches.Length;
        TotalPitches++;
    }

    public void OnPlateAppearance(PaTrace t)
    {
        _pas[_paHead] = t;
        _paHead = (_paHead + 1) % _pas.Length;
        TotalPlateAppearances++;
    }

    public void OnGameEnd(GameResult result) => Result = result;
}
