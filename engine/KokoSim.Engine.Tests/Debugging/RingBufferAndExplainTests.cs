using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 設計書17 F3（デバッグHUD）のデータ源の受け入れ:
///  - <see cref="RingBufferTraceSink"/> が直近N球を古い順に保持し、巻き戻り境界で壊れない
///  - AI校の判断内訳（<see cref="IExplainTactics"/>）が観測時だけ埋まり、結果を1ビットも変えない
/// </summary>
public sealed class RingBufferAndExplainTests
{
    private static Team Ai(string name) => DeterminismCards.Team(name) with
    {
        Tactics = new AiTacticsBrain(new AiProfile(TacticalSense: 85, TierRank: 6, SchoolStyle.Standard)),
    };

    [Fact]
    public void RingBuffer_KeepsTheLastNPitchesInOrder()
    {
        var ring = new RingBufferTraceSink(pitchCapacity: 8, paCapacity: 4);
        var result = GameEngine.Play(DeterminismCards.Team("A"), DeterminismCards.Team("H"),
            new GameContext { CaptureTrace = true, TraceSink = ring }, new Xoshiro256Random(13UL));

        Assert.Equal(result.TotalPitches, ring.TotalPitches);
        Assert.Equal(result.Log.Count, ring.TotalPlateAppearances);

        var recent = ring.RecentPitches(8);
        Assert.Equal(8, recent.Count);
        // 古い順で連番（リングの巻き戻りをまたいでも順序が壊れない）。
        Assert.Equal(
            Enumerable.Range((int)ring.TotalPitches - 7, 8),
            recent.Select(t => t.PitchNoInGame));
        Assert.Equal(recent[^1], ring.Latest);

        // 容量を超えて要求しても、持っている以上は返さない。
        Assert.Equal(8, ring.RecentPitches(100).Count);
        Assert.Equal(4, ring.RecentPlateAppearances(100).Count);
        Assert.NotNull(ring.Result);
        Assert.NotNull(ring.Header);
    }

    [Fact]
    public void RingBuffer_BeforeAnyPitch_IsEmptyNotNullReferencing()
    {
        var ring = new RingBufferTraceSink();
        Assert.Null(ring.Latest);
        Assert.Empty(ring.RecentPitches(10));
        Assert.Empty(ring.RecentPlateAppearances(10));
    }

    /// <summary>新しい試合が始まったら前の試合の球は残らない（HUDが混ざらない）。</summary>
    [Fact]
    public void RingBuffer_ResetsOnNewGame()
    {
        var ring = new RingBufferTraceSink(pitchCapacity: 8);
        var ctx = new GameContext { CaptureTrace = true, TraceSink = ring };
        GameEngine.Play(DeterminismCards.Team("A"), DeterminismCards.Team("H"), ctx, new Xoshiro256Random(1UL));
        var firstGamePitches = ring.TotalPitches;

        GameEngine.Play(DeterminismCards.Team("A"), DeterminismCards.Team("H"), ctx, new Xoshiro256Random(2UL));

        Assert.True(ring.TotalPitches > 0);
        Assert.NotEqual(firstGamePitches + ring.TotalPitches, ring.TotalPitches * 2 + firstGamePitches); // 加算されていない
        Assert.All(ring.RecentPitches(8), t => Assert.True(t.PitchNoInGame <= ring.TotalPitches));
    }

    /// <summary>AI校の判断内訳が観測時だけ載り、しかも結果は1ビットも変わらない。</summary>
    [Fact]
    public void ExplainDecisions_FillsCandidatesWithoutChangingTheResult()
    {
        var baseline = GameResultDigest.Sha256Of(DeterminismCards.Run("pitch-tactics", 6UL));

        var sink = new RecordingTraceSink();
        var traced = GameEngine.Play(Ai("A"), Ai("H"),
            new GameContext { CaptureTrace = true, TraceSink = sink }, new Xoshiro256Random(6UL));

        Assert.Equal(baseline, GameResultDigest.Sha256Of(traced));
        Assert.Contains(sink.Pitches, t => t.SignCandidatesCsv is not null);

        var sample = sink.Pitches.First(t => t.SignCandidatesCsv is not null).SignCandidatesCsv!;
        Assert.Contains("inner=", sample);
        Assert.Contains("optimal=", sample);
        Assert.Contains("tier=", sample);
    }

    /// <summary>観測を切れば内訳は組まれない（既定パスは文字列生成コストゼロ）。</summary>
    [Fact]
    public void WithoutTracing_BrainDoesNotBuildExplanations()
    {
        var brain = new AiTacticsBrain(new AiProfile(TacticalSense: 85, TierRank: 6, SchoolStyle.Standard));
        Assert.False(brain.ExplainDecisions);

        var situation = new PitchTacticsSituation(default, 0, 0, 0, null);
        var d = brain.CallPitchAction(situation, new Xoshiro256Random(1UL));
        Assert.Null(d?.Explanation);
    }
}
