using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>1球とその直後のカウント（B/S）。</summary>
public readonly record struct PitchToken(PitchKind Kind, int BallsAfter, int StrikesAfter);

/// <summary>1打席の投球列（中継風カウントの合成データ）。</summary>
public sealed record PitchSequence(IReadOnlyList<PitchToken> Pitches)
{
    public static readonly PitchSequence Empty = new(Array.Empty<PitchToken>());
}

/// <summary>
/// 投球列の合成器（設計 Group G, #4）。
///
/// エンジンは打席を「1回で」解決し、投球ごとのボール/ストライクをモデル化していない。決定論ゲートと
/// バランス帯を壊さず中継風の B/S カウントを見せるため、**打席結果が確定した後**に、結果と整合する
/// 妥当な投球列を合成する。合成は専用の <see cref="Xoshiro256Random"/>（打席固有のハッシュ由来シード）だけを
/// 使い、メインの試合 RNG ストリームには一切触れない。総投球数と最終カウント（K=3ストライク／BB=4ボール／
/// インプレー=打った瞬間）は実データと整合し、途中の内訳は妥当だが架空。判定・帯には無関係。
/// </summary>
public static class PitchSequenceSynthesizer
{
    /// <summary>
    /// 結果 result・総投球数 pitchCount・独立シード seed から投球列を合成する。
    /// pitchCount は結果ごとの最小球数（K=3/BB=4/インプレー=1）を下限に丸める
    /// （申告敬遠 pitchCount=0 → 4球の敬遠ボールとして見せる）。途中で 3ストライク/4ボールに達しない。
    /// </summary>
    public static PitchSequence Synthesize(PlateAppearanceResult result, int pitchCount, ulong seed)
    {
        var min = result switch
        {
            PlateAppearanceResult.Strikeout => 3,
            PlateAppearanceResult.Walk => 4,
            _ => 1,
        };
        var n = Math.Max(pitchCount, min);

        var rng = new Xoshiro256Random(Mix(seed));

        // 中間（最後の1球を除く n-1 球）の内訳。ball≤3, strike≤2, foul は2ストライク時のみ（カウント不変）。
        int ballsInter, strikesInter, foulsInter;
        switch (result)
        {
            case PlateAppearanceResult.Strikeout:
                strikesInter = Math.Min(2, n - 1);
                var restK = (n - 1) - strikesInter;
                ballsInter = Math.Min(3, restK);
                foulsInter = restK - ballsInter;
                break;
            case PlateAppearanceResult.Walk:
                ballsInter = Math.Min(3, n - 1);
                var restW = (n - 1) - ballsInter;
                strikesInter = Math.Min(2, restW);
                foulsInter = restW - strikesInter;
                break;
            default: // インプレー（最後の球で打つ）
                var baseN = n - 1;
                ballsInter = Math.Min(3, baseN);
                var restP = baseN - ballsInter;
                strikesInter = Math.Min(2, restP);
                foulsInter = restP - strikesInter;
                break;
        }

        var tokens = new List<PitchToken>(n);
        int b = 0, s = 0;

        // ball と strike を交互っぽく並べる（fouls は 2ストライク到達後にまとめる）。
        int nb = ballsInter, ns = strikesInter;
        while (nb > 0 || ns > 0)
        {
            var pickBall = ns == 0 || (nb > 0 && (rng.NextUInt64() & 1UL) == 0);
            if (pickBall)
            {
                b++; nb--;
                tokens.Add(new PitchToken(PitchKind.Ball, b, s));
            }
            else
            {
                s++; ns--;
                var kind = (rng.NextUInt64() & 1UL) == 0 ? PitchKind.CalledStrike : PitchKind.SwingingStrike;
                tokens.Add(new PitchToken(kind, b, s));
            }
        }
        for (var i = 0; i < foulsInter; i++)
            tokens.Add(new PitchToken(PitchKind.Foul, b, s)); // s==2 のはず＝ファウルはカウント不変

        // 最後の1球（結果に整合する終端）。
        switch (result)
        {
            case PlateAppearanceResult.Strikeout:
                s = 3;
                tokens.Add(new PitchToken(
                    (rng.NextUInt64() & 1UL) == 0 ? PitchKind.CalledStrike : PitchKind.SwingingStrike, b, s));
                break;
            case PlateAppearanceResult.Walk:
                b = 4;
                tokens.Add(new PitchToken(PitchKind.Ball, b, s));
                break;
            default:
                tokens.Add(new PitchToken(PitchKind.InPlay, b, s));
                break;
        }

        return new PitchSequence(tokens);
    }

    /// <summary>打席固有の材料を1つの ulong シードへ混ぜる（メインRNGに触れない独立ストリーム用）。</summary>
    public static ulong SeedFrom(int paIndex, int inning, bool isTop, int pitchCount, PlateAppearanceResult result)
    {
        var x = (ulong)(uint)paIndex * 0x9E3779B1UL;
        x ^= (ulong)(uint)inning << 20;
        x ^= isTop ? 0x1UL : 0x2UL;
        x ^= (ulong)(uint)pitchCount << 32;
        x ^= (ulong)(uint)(int)result << 48;
        return x;
    }

    // splitmix64 finalizer（シード撹拌。メインRNGストリームとは独立）。
    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
