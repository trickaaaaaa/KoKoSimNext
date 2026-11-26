using System;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>集計モデル（<see cref="AggregateMatch"/>）の勝敗から表示用スコアを作る。勝ち抜き戦とリーグ戦で共通。</summary>
internal static class AggregateScore
{
    /// <summary>得点差(margin)から表示用スコアを合成する（敗者0〜3点＋差）。決定論（注入乱数・1回消費）。</summary>
    public static (int WinnerScore, int LoserScore) Synthesize(int margin, IRandomSource rng)
    {
        var loser = rng.NextInt(0, 4);
        return (loser + Math.Max(1, margin), loser);
    }
}
