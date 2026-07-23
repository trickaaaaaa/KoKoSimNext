using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 設計書17 F4（強制発動＋シーク）の受け入れ:
///  - 全 <see cref="ForcedOutcome"/> が1回で発動し、下流（進塁・記録・タイムライン）が破綻しない
///  - 強制した試合は <see cref="GameResult.HasForcedOutcomes"/> が立ち、digest・統計から外せる
///  - 強制しない試合は従来と1ビットも変わらない
///  - pitch粒度シークの往復で同一トレースになる
/// </summary>
public sealed class ForcedOutcomeTests
{
    /// <summary>稀プレー系のゲートを開けた文脈（既定オフのままだと強制しても発火機会が来ない）。</summary>
    private static GameContext RarePlayContext() => new()
    {
        Baserunning = new BaserunningCoefficients
        {
            // 強制発動の対象ゲートは Apply() が 1.0 に寄せるが、素の確率が0のままだと
            // 「そもそも分岐に入らない」ガードに阻まれる箇所があるため、検証用に開けておく。
            DropThirdStrikeReachProb = 0.05,
            WildPitchProb = 0.01,
            FieldersChoiceProb = 0.05,
            PickoffBaseProb = 0.01,
            DoubleStealThirdBreakProb = 0.05,
        },
    };

    private static IEnumerable<ForcedOutcome> AllOutcomes()
        => System.Enum.GetValues(typeof(ForcedOutcome)).Cast<ForcedOutcome>().Where(f => f != ForcedOutcome.None);

    /// <summary>
    /// 全 ForcedOutcome を、その種別が成立しうる打席まで待って1回発動させ、試合が最後まで壊れずに完走すること。
    /// 打席結果固定系は「宣言どおりの結果になったか」まで確認する。
    /// </summary>
    [Fact]
    public void EveryForcedOutcome_FiresOnceAndDownstreamSurvives()
    {
        foreach (var f in AllOutcomes())
        {
            var sink = new RecordingTraceSink();
            var ctx = RarePlayContext() with { CaptureTrace = true, TraceSink = sink, CaptureTimelines = true };
            var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"), ctx, 7UL);

            prog.ForceNext(f);
            prog.Advance();                       // 強制が効く打席
            var forcedPa = prog.Current;
            Assert.NotNull(forcedPa);

            var result = prog.FinishRemaining();  // 残りを最後まで流す（下流が破綻しないこと）

            Assert.True(result.HasForcedOutcomes, $"{f}: HasForcedOutcomes が立っていない");
            Assert.Contains(sink.PlateAppearances, t => t.Forced);
            Assert.True(result.Log.Count > 0);
            Assert.True(result.InningsPlayed >= 9);

            if (ForcedOutcomes.ToPlateAppearance(f) is { } expected)
            {
                Assert.Equal(expected, forcedPa!.Result);
                // 打席結果固定は投球ループごとスキップする（敬遠と同じ＝投球数0）。
                Assert.Equal(0, result.Log[0].Pitches);
            }
        }
    }

    /// <summary>強制発動しない限り、結果は従来と1ビットも変わらない（既定パスの不変）。</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(17UL)]
    public void WithoutForcing_ResultIsUnchanged(ulong seed)
    {
        var baseline = GameResultDigest.Sha256Of(DeterminismCards.Run("avg", seed));
        var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), seed);
        var result = prog.FinishRemaining();

        Assert.False(result.HasForcedOutcomes);
        Assert.Null(result.ScenarioId);
        Assert.Equal(baseline, GameResultDigest.Sha256Of(result));
    }

    /// <summary>強制は「次の1打席」に一度きり効く（次の打席には残らない）。</summary>
    [Fact]
    public void Force_AppliesToTheNextPlateAppearanceOnly()
    {
        var sink = new RecordingTraceSink();
        var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"),
            new GameContext { CaptureTrace = true, TraceSink = sink }, 5UL);

        prog.ForceNext(ForcedOutcome.HomeRun);
        prog.Advance();
        Assert.Equal(PlateAppearanceResult.HomeRun, prog.Current!.Result);

        prog.Advance();
        prog.Advance();
        Assert.Equal(1, sink.PlateAppearances.Count(t => t.Forced));
    }

    [Fact]
    public void ForcedOutcome_ParsesFromStringCaseInsensitively()
    {
        Assert.True(ForcedOutcomes.TryParse("WildPitch", out var a));
        Assert.Equal(ForcedOutcome.WildPitch, a);
        Assert.True(ForcedOutcomes.TryParse("homerun", out var b));
        Assert.Equal(ForcedOutcome.HomeRun, b);
        Assert.False(ForcedOutcomes.TryParse("Balk", out _));      // 意図的に持たない種別
        Assert.False(ForcedOutcomes.TryParse("", out _));
        Assert.False(ForcedOutcomes.TryParse("999", out _));       // 数値からの素通しを許さない
    }

    // ===== シーク（設計書17 §6.2）=====

    /// <summary>pitch粒度シークの往復（進む→戻る→進む）で同一トレースになる。</summary>
    [Theory]
    [InlineData(3UL)]
    [InlineData(11UL)]
    public void PitchLevelSeek_RoundTripsToTheSameTrace(ulong seed)
    {
        static List<string> TraceOf(ulong s, System.Action<MatchProgression> drive)
        {
            var sink = new RecordingTraceSink();
            var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"),
                new GameContext { CaptureTrace = true, TraceSink = sink }, s);
            drive(prog);
            prog.FinishRemaining();
            return sink.Pitches.Select(t => $"{t.PitchNoInGame}:{t.Kind}:{t.PlanType}:{t.ActualX:F4}").ToList();
        }

        var straight = TraceOf(seed, _ => { });
        var seeked = TraceOf(seed, prog =>
        {
            prog.AdvancePa(12);                 // 進む
            prog.AdvancePitch();
            prog.AdvancePitch();
            prog.SeekTo(5);                     // 戻る（打席境界）
            prog.SeekTo(12, 2);                 // また進む（打席途中＝pitch粒度）
        });

        // SeekTo は先頭から再生し直すので、最後のシーク以降のトレースは「試合1本ぶん」まるごとになる。
        // その塊が、シークせずまっすぐ流したトレースと1件も違わないこと＝往復しても同じ試合。
        var lastRestart = seeked.FindLastIndex(s => s.StartsWith("1:", System.StringComparison.Ordinal));
        Assert.True(lastRestart >= 0, "シーク後の再生が観測されていない");
        Assert.Equal(straight, seeked.Skip(lastRestart).ToList());
    }

    /// <summary>シークしてから最後まで流した結果は、シークしない実行と完全一致する。</summary>
    [Fact]
    public void SeekThenFinish_MatchesUninterrupted()
    {
        var full = GameResultDigest.Sha256Of(DeterminismCards.Run("avg", 21UL));

        var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), 21UL);
        prog.AdvancePa(20);
        prog.SeekTo(8);
        prog.SeekTo(20);
        var result = prog.FinishRemaining();

        Assert.Equal(full, GameResultDigest.Sha256Of(result));
    }

    [Fact]
    public void AdvanceUntilInningEnd_StopsAtThreeOuts()
    {
        var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), 4UL);
        var moved = prog.AdvanceUntilInningEnd();

        Assert.True(moved >= 3, "3アウト取るには最低3打席かかる");
        Assert.Equal(1, prog.Current!.Inning);
        Assert.True(prog.Current.IsTop);
    }

    /// <summary>再現トークンはシーク位置を指し、同じ場面へ戻せる。</summary>
    [Fact]
    public void ReproToken_TracksTheCurrentPosition()
    {
        var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), 31UL);
        prog.AdvancePa(14);

        var token = prog.ReproToken();
        Assert.NotNull(token);
        Assert.Equal(14, token!.Value.PlateAppearance);
        Assert.True(token.Value.Verify(DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext()));

        var resumed = GameReplay.Restore(DeterminismCards.Team("A"), DeterminismCards.Team("H"),
            new GameContext(), token.Value.ToSaveState());
        while (resumed.Steps.MoveNext()) { }

        Assert.Equal(
            GameResultDigest.Sha256Of(DeterminismCards.Run("avg", 31UL)),
            GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress)));
    }
}
