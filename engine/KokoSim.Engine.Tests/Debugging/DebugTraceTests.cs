using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>テスト用のシンク（全イベントをメモリに溜める）。</summary>
internal sealed class RecordingTraceSink : IDebugTraceSink
{
    public GameTraceHeader? Header { get; private set; }
    public List<PitchTrace> Pitches { get; } = new();
    public List<PaTrace> PlateAppearances { get; } = new();
    public GameResult? End { get; private set; }
    public int EndCalls { get; private set; }

    public void OnGameStart(GameTraceHeader header) => Header = header;
    public void OnPitch(PitchTrace t) => Pitches.Add(t);
    public void OnPlateAppearance(PaTrace t) => PlateAppearances.Add(t);
    public void OnGameEnd(GameResult result) { End = result; EndCalls++; }
}

/// <summary>
/// 設計書17 F1（観測）の受け入れ。中核は<b>「CaptureTrace の on/off で GameResultDigest が完全一致」</b>
/// ＝観測は試合結果を1ビットも変えない（不変条件#2/#5）。
/// </summary>
public sealed class DebugTraceTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[]
            {
                Pos(FieldPosition.Pitcher) with { Name = name + "R1", Pitching = PitcherAttributes.LeagueAverage },
                Pos(FieldPosition.Pitcher) with { Name = name + "R2", Pitching = PitcherAttributes.LeagueAverage },
            },
            Bench = new[]
            {
                Pos(FieldPosition.FirstBase) with { Name = name + "PH", Contact = 55 },
                Pos(FieldPosition.SecondBase) with { Name = name + "PR", Speed = 70 },
            },
        };
    }

    private static Team Ai(string name) =>
        Team(name) with { Tactics = new AiTacticsBrain(new AiProfile(TacticalSense: 85, TierRank: 6, SchoolStyle.Standard)) };

    // ===== 中核テスト: 観測は結果を変えない =====

    /// <summary>
    /// 決定論ゲートと同じ全カード×多シードで、CaptureTrace on/off の digest が完全一致すること。
    /// 1シードでも不一致なら本設計は no-go（設計書17 §9 F1 DoD）。
    /// </summary>
    [Theory]
    [InlineData("avg")]
    [InlineData("tactics")]
    [InlineData("modern")]
    [InlineData("pitch-tactics")]
    public void CaptureTrace_DoesNotChangeGameResultDigest(string card)
    {
        foreach (var seed in DeterminismCards.Seeds())
        {
            var off = GameResultDigest.Sha256Of(DeterminismCards.Run(card, seed));
            var on = GameResultDigest.Sha256Of(RunTraced(card, seed, new RecordingTraceSink()));
            Assert.Equal(off, on);
        }
    }

    /// <summary>
    /// TraceSink を挿しても、乱数消費を数えるデコレータ（CountingRandomSource）が
    /// 値を素通しすることの直接確認。包む/包まないで乱数列が完全一致する。
    /// </summary>
    [Fact]
    public void CountingRandomSource_PassesValuesThrough()
    {
        var raw = new Xoshiro256Random(12345UL);
        var counted = new CountingRandomSource(new Xoshiro256Random(12345UL));

        for (var i = 0; i < 500; i++)
        {
            Assert.Equal(raw.NextUInt64(), counted.NextUInt64());
            Assert.Equal(raw.NextDouble(), counted.NextDouble());
            Assert.Equal(raw.NextGaussian(), counted.NextGaussian());
        }
        Assert.Equal(1500, counted.Stats.Draws);

        // Fork した子の消費も同じカウンタへ集計される。
        var child = counted.Fork(7UL);
        child.NextDouble();
        Assert.Equal(1501, counted.Stats.Draws);
        Assert.Equal(7UL, counted.Stats.LastForkStreamId);
    }

    // ===== 観測レコードの中身 =====

    [Fact]
    public void Trace_HeaderCarriesEnoughToReproduceTheGame()
    {
        var sink = new RecordingTraceSink();
        var ctx = new GameContext { CaptureTrace = true, TraceSink = sink };
        var result = GameEngine.Play(Team("A"), Team("H"), ctx, new Xoshiro256Random(42UL));

        Assert.NotNull(sink.Header);
        Assert.Equal("A", sink.Header!.AwayName);
        Assert.Equal("H", sink.Header.HomeName);
        Assert.Equal(ReproToken.Fingerprint(Team("A"), Team("H"), ctx), sink.Header.FixtureFingerprint);
        Assert.Equal(Xoshiro256Random.StateWords * 16, sink.Header.RngStateHex.Length);
        Assert.Null(sink.Header.ScenarioId);

        // ヘッダの RNG 状態だけで頭から同じ試合を再生できる。
        Assert.True(ReproToken.TryParse(
            $"k1:{sink.Header.RngStateHex}:0:0:{sink.Header.FixtureFingerprint}", out var token));
        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), token.ToSaveState());
        while (resumed.Steps.MoveNext()) { }
        Assert.Equal(
            GameResultDigest.Sha256Of(GameEngine.Play(Team("A"), Team("H"), new GameContext(), new Xoshiro256Random(42UL))),
            GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress)));

        Assert.Equal(1, sink.EndCalls);
        Assert.Equal(result.AwayRuns, sink.End!.AwayRuns);
    }

    [Fact]
    public void Trace_PitchCountMatchesResultAndFieldsAreCoherent()
    {
        var sink = new RecordingTraceSink();
        var result = GameEngine.Play(Ai("A"), Ai("H"),
            new GameContext { CaptureTrace = true, TraceSink = sink }, new Xoshiro256Random(7UL));

        // 打席の観測件数＝速報ログの打席数。
        Assert.Equal(result.Log.Count, sink.PlateAppearances.Count);
        // 投球の観測件数＝両軍合計球数（敬遠は0球なので Log の Pitches 合計と一致する）。
        Assert.Equal(result.TotalPitches, sink.Pitches.Count);
        Assert.Equal(result.Log.Sum(e => e.Pitches), sink.Pitches.Count);

        // 通し番号は1始まりで欠番なし。
        Assert.Equal(Enumerable.Range(1, sink.Pitches.Count), sink.Pitches.Select(t => t.PitchNoInGame));

        foreach (var t in sink.Pitches)
        {
            Assert.InRange(t.BallsBefore, 0, 3);
            Assert.InRange(t.StrikesBefore, 0, 2);
            Assert.InRange(t.PitchNoInPa, 1, 40); // AtBatResolver.MaxPitches（internal）
            Assert.InRange(t.SwingProbability, 0.0, 1.0);
            Assert.True(t.PlanVelocityKmh > 0);
            Assert.True(t.FlightTimeSeconds > 0);
            Assert.False(t.Forced);
            Assert.True(t.RngDrawsInPitch > 0, "1球の解決は必ず乱数を消費する");
            // 見送りの球はスイングしていない。空振り/ファウル/インプレーは必ず振っている。
            if (t.Kind is PitchKind.Ball or PitchKind.CalledStrike) Assert.False(t.Swung);
            if (t.Kind is PitchKind.SwingingStrike or PitchKind.Foul or PitchKind.InPlay) Assert.True(t.Swung);
            // 打球データは InPlay/Foul のときだけ載る。
            if (t.Kind == PitchKind.InPlay) Assert.NotNull(t.ExitVelocityKmh);
            if (t.Kind is PitchKind.Ball or PitchKind.CalledStrike or PitchKind.SwingingStrike)
                Assert.Null(t.ExitVelocityKmh);
        }

        // 見送りストライクはゾーン内、ボールはゾーン外（判定と観測が食い違っていない）。
        Assert.All(sink.Pitches.Where(t => t.Kind == PitchKind.CalledStrike), t => Assert.True(t.InZone));
        Assert.All(sink.Pitches.Where(t => t.Kind == PitchKind.Ball && !t.Swung), t => Assert.False(t.InZone));
    }

    /// <summary>観測カウントが実況カウント（PitchRecord の解決後の値）と整合すること。</summary>
    [Fact]
    public void Trace_CountBeforePitchMatchesPitchLog()
    {
        var sink = new RecordingTraceSink();
        var result = GameEngine.Play(Team("A"), Team("H"),
            new GameContext { CaptureTrace = true, TraceSink = sink }, new Xoshiro256Random(11UL));

        var idx = 0;
        foreach (var e in result.Log)
        {
            var balls = 0;
            var strikes = 0;
            foreach (var rec in e.PitchLog!)
            {
                var t = sink.Pitches[idx++];
                Assert.Equal(balls, t.BallsBefore);
                Assert.Equal(strikes, t.StrikesBefore);
                Assert.Equal(rec.Kind, t.Kind);
                Assert.Equal(rec.PitchType, t.PlanType);
                Assert.Equal(rec.LocationX, t.ActualX);
                balls = rec.BallsAfter;
                strikes = rec.StrikesAfter;
            }
        }
        Assert.Equal(sink.Pitches.Count, idx);
    }

    [Fact]
    public void Trace_PlateAppearanceRecordsMatchTheLog()
    {
        var sink = new RecordingTraceSink();
        var result = GameEngine.Play(Ai("A"), Ai("H"),
            new GameContext { CaptureTrace = true, TraceSink = sink }, new Xoshiro256Random(23UL));

        for (var i = 0; i < result.Log.Count; i++)
        {
            Assert.Equal(result.Log[i].Result, sink.PlateAppearances[i].Result);
            Assert.Equal(result.Log[i].RunsScored, sink.PlateAppearances[i].Rbi);
            Assert.Equal(result.Log[i].Pitches, sink.PlateAppearances[i].Pitches);
            Assert.InRange(sink.PlateAppearances[i].OutsAfter, 0, 3);
        }
    }

    /// <summary>フラグだけ立ててシンクを渡さなければ観測は走らない（＝両方揃ったときだけ）。</summary>
    [Fact]
    public void CaptureTrace_WithoutSink_IsInert()
    {
        var ctx = new GameContext { CaptureTrace = true };
        Assert.False(ctx.TracingEnabled);
        Assert.Equal(
            GameResultDigest.Sha256Of(GameEngine.Play(Team("A"), Team("H"), new GameContext(), new Xoshiro256Random(5UL))),
            GameResultDigest.Sha256Of(GameEngine.Play(Team("A"), Team("H"), ctx, new Xoshiro256Random(5UL))));
    }

    /// <summary>製品コード（既定 GameContext）は観測オフ＝統計シムはゼロコスト（設計書17 §8）。</summary>
    [Fact]
    public void DefaultContext_HasTracingOff()
    {
        var ctx = new GameContext();
        Assert.False(ctx.CaptureTrace);
        Assert.Null(ctx.TraceSink);
        Assert.False(ctx.TracingEnabled);
        Assert.False(ctx.ToAtBatContext(new FieldGeometry().StandardAlignment()).CaptureTrace);
    }

    private static GameResult RunTraced(string card, ulong seed, IDebugTraceSink sink)
    {
        var rng = new Xoshiro256Random(seed);
        GameContext Ctx(GameContext baseCtx) => baseCtx with { CaptureTrace = true, TraceSink = sink };
        switch (card)
        {
            case "avg":
                return GameEngine.Play(Team("A"), Team("H"), Ctx(new GameContext()), rng);
            case "tactics":
                return GameEngine.Play(
                    Team("A") with { Tactics = new StandardTacticsBrain() },
                    Team("H") with { Tactics = new StandardTacticsBrain() },
                    Ctx(new GameContext()), rng);
            case "modern":
                return GameEngine.Play(Team("A"), Team("H"),
                    Ctx(new GameContext { TieBreakEnabled = true, TieBreakStartInning = 10, MercyRuleEnabled = true }), rng);
            case "pitch-tactics":
                return GameEngine.Play(Ai("A"), Ai("H"), Ctx(new GameContext()), rng);
            default:
                throw new System.ArgumentOutOfRangeException(nameof(card), card, "unknown card");
        }
    }
}
