using System;
using System.Linq;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 投球列合成器（#4）の契約テスト。総投球数と最終カウントが結果と整合し、途中で3ストライク/4ボールに
/// 達しないこと、同シードで再現することを保証する。合成は演出専用（判定・帯に無関係）。
/// </summary>
public class PitchSequenceSynthesizerTests
{
    private static int MinPitches(PlateAppearanceResult r) => r switch
    {
        PlateAppearanceResult.Strikeout => 3,
        PlateAppearanceResult.Walk => 4,
        _ => 1,
    };

    [Fact]
    public void Synthesize_TerminatesConsistently_AndNeverExceedsCountMidSequence()
    {
        int[] counts = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12 };
        foreach (var result in Enum.GetValues<PlateAppearanceResult>())
        foreach (var pc in counts)
        {
            var seed = PitchSequenceSynthesizer.SeedFrom(pc, 3, true, pc, result);
            var seq = PitchSequenceSynthesizer.Synthesize(result, pc, seed);
            var expectedN = Math.Max(pc, MinPitches(result));
            Assert.Equal(expectedN, seq.Pitches.Count);

            var last = seq.Pitches[^1];
            int prevB = 0, prevS = 0;
            for (var i = 0; i < seq.Pitches.Count; i++)
            {
                var t = seq.Pitches[i];
                var isLast = i == seq.Pitches.Count - 1;
                // カウントは範囲内・非減少。
                Assert.InRange(t.BallsAfter, 0, 4);
                Assert.InRange(t.StrikesAfter, 0, 3);
                Assert.True(t.BallsAfter >= prevB && t.StrikesAfter >= prevS, "カウントが減っている");
                // 途中（最後の1球より前）で決着カウントに達してはいけない。
                if (!isLast)
                {
                    Assert.True(t.BallsAfter < 4, $"{result} 途中で4ボール");
                    Assert.True(t.StrikesAfter < 3, $"{result} 途中で3ストライク");
                }
                prevB = t.BallsAfter; prevS = t.StrikesAfter;
            }

            switch (result)
            {
                case PlateAppearanceResult.Strikeout:
                    Assert.Equal(3, last.StrikesAfter);
                    Assert.True(last.Kind is PitchKind.CalledStrike or PitchKind.SwingingStrike);
                    break;
                case PlateAppearanceResult.Walk:
                    Assert.Equal(4, last.BallsAfter);
                    Assert.Equal(PitchKind.Ball, last.Kind);
                    break;
                case PlateAppearanceResult.HitByPitch:
                    Assert.Equal(PitchKind.HitByPitch, last.Kind); // 死球はカウント不変で終了
                    Assert.True(last.BallsAfter < 4 && last.StrikesAfter < 3);
                    break;
                default:
                    Assert.Equal(PitchKind.InPlay, last.Kind);
                    Assert.True(last.BallsAfter < 4 && last.StrikesAfter < 3);
                    break;
            }
        }
    }

    [Fact]
    public void Synthesize_IsReproducible_ForSameSeed()
    {
        foreach (var result in Enum.GetValues<PlateAppearanceResult>())
        {
            var seed = PitchSequenceSynthesizer.SeedFrom(7, 5, false, 6, result);
            var a = PitchSequenceSynthesizer.Synthesize(result, 6, seed);
            var b = PitchSequenceSynthesizer.Synthesize(result, 6, seed);
            Assert.Equal(a.Pitches, b.Pitches);
        }
    }

    /// <summary>
    /// 合成列は実際に投げられた球ではないため、球種・球速を持たない（null）。表示側はこれを
    /// 「球種・球速は出さない」として扱う＝架空の球速をでっち上げない契約。
    /// </summary>
    [Fact]
    public void Synthesize_HasNoPitchTypeOrVelocity()
    {
        foreach (var result in Enum.GetValues<PlateAppearanceResult>())
        {
            var seed = PitchSequenceSynthesizer.SeedFrom(3, 2, true, 5, result);
            var seq = PitchSequenceSynthesizer.Synthesize(result, 5, seed);
            Assert.All(seq.Pitches, t =>
            {
                Assert.Null(t.PitchType);
                Assert.Null(t.VelocityKmh);
            });
        }
    }

    [Fact]
    public void Synthesize_IntentionalWalk_ZeroPitches_ShowsFourBalls()
    {
        var seed = PitchSequenceSynthesizer.SeedFrom(1, 1, true, 0, PlateAppearanceResult.Walk);
        var seq = PitchSequenceSynthesizer.Synthesize(PlateAppearanceResult.Walk, 0, seed);
        Assert.Equal(4, seq.Pitches.Count);
        Assert.All(seq.Pitches, t => Assert.Equal(PitchKind.Ball, t.Kind));
        Assert.Equal(4, seq.Pitches[^1].BallsAfter);
    }
}
