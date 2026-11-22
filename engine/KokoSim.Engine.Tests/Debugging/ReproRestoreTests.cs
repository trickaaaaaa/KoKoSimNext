using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 再現トークンの復元が「黙って別の試合を再生」しないこと（issue #1285・設計書17 §3.3）:
///  - 場面ジャンプで始めた試合は、開始局面ごと復元される（指紋にも載る）
///  - トークンの RNG 開始状態から始める（母種と無関係に、トークンが指す試合になる）
///  - 采配・強制発動の決定列を引き継ぐ（シーク・復元で予約が落ちない）
/// 併せて、トレースヘッダに母種と係数の識別子が載る（§4.3）。
/// </summary>
public sealed class ReproRestoreTests
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
            Reserves = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    private static readonly ScenarioStart Ninth = new()
    {
        Inning = 9, IsTop = false, Outs = 2, OnFirst = true, OnSecond = true, OnThird = true,
        Balls = 3, Strikes = 2, AwayScore = 3, HomeScore = 2,
    };

    private static string Log(MatchProgression p)
        => string.Join("|", p.BuildResult().Log.Select(e => $"{e.Inning}{(e.IsTop ? 'T' : 'B')}:{e.Result}:{e.RunsScored}"))
           + $"#{p.AwayScore}-{p.HomeScore}";

    private static MatchProgression Seeded(ulong seed, ScenarioStart? start)
        => new(Team("A"), Team("H"), new GameContext(), seed, start);

    // ===== 場面ジャンプの試合 =====

    [Fact]
    public void FromReproToken_ScenarioMatch_ReplaysFromTheScenarioStart()
    {
        var original = Seeded(11UL, Ninth);
        Assert.True(original.Advance());
        var token = original.ReproToken()!.Value;

        original.FinishRemaining();
        var expected = Log(original);

        var restored = MatchProgression.FromReproToken(Team("A"), Team("H"), new GameContext(), token, Ninth);
        Assert.Equal(1, restored.ConfirmedPlateAppearances);
        restored.FinishRemaining();

        Assert.Equal(expected, Log(restored));
    }

    /// <summary>シナリオ無しで復元すると 1 回表の頭から始まる別の試合になる（これが取りこぼしの正体）。</summary>
    [Fact]
    public void FromReproToken_WithoutScenarioStart_IsADifferentMatch()
    {
        var original = Seeded(11UL, Ninth);
        original.FinishRemaining();

        var token = Seeded(11UL, Ninth).ReproToken()!.Value;
        var wrong = MatchProgression.FromReproToken(Team("A"), Team("H"), new GameContext(), token);
        wrong.FinishRemaining();

        Assert.NotEqual(Log(original), Log(wrong));
    }

    [Fact]
    public void Fingerprint_IncludesTheScenarioStart_ButNotForPlainMatches()
    {
        var plain = ReproToken.Fingerprint(Team("A"), Team("H"), new GameContext());
        Assert.Equal(plain, ReproToken.Fingerprint(Team("A"), Team("H"), new GameContext(), null));

        var scenario = ReproToken.Fingerprint(Team("A"), Team("H"), new GameContext(), Ninth);
        Assert.NotEqual(plain, scenario);
        Assert.NotEqual(scenario,
            ReproToken.Fingerprint(Team("A"), Team("H"), new GameContext(), Ninth with { Outs = 1 }));

        var token = Seeded(5UL, Ninth).ReproToken()!.Value;
        Assert.Equal(scenario, token.FixtureFingerprint);
        Assert.True(token.Verify(Team("A"), Team("H"), new GameContext(), Ninth));
        Assert.False(token.Verify(Team("A"), Team("H"), new GameContext()));   // シナリオ無しとは照合が通らない
    }

    [Fact]
    public void TraceHeader_FixtureFingerprint_MatchesTheTokenOfAScenarioMatch()
    {
        var sink = new RingBufferTraceSink();
        var ctx = new GameContext { CaptureTrace = true, TraceSink = sink };
        var prog = new MatchProgression(Team("A"), Team("H"), ctx, 3UL, Ninth);

        Assert.Equal(prog.ReproToken()!.Value.FixtureFingerprint, sink.Header!.FixtureFingerprint);
    }

    // ===== トークンの RNG 開始状態 =====

    [Fact]
    public void FromReproToken_UsesTheTokensRngState_NotAnyOtherSeed()
    {
        var a = Seeded(21UL, null);
        a.FinishRemaining();
        var token = Seeded(21UL, null).ReproToken()!.Value;

        var restored = MatchProgression.FromReproToken(Team("A"), Team("H"), new GameContext(), token);
        restored.FinishRemaining();

        Assert.Equal(Log(a), Log(restored));
        Assert.NotEqual(Log(a), Log(Seeded(22UL, null).Also(p => p.FinishRemaining())));
    }

    // ===== 決定列（強制発動・采配）=====

    [Fact]
    public void ForceNext_IsRecordedAsADecision_AndSurvivesSeek()
    {
        var prog = Seeded(31UL, null);
        prog.ForceNext(ForcedOutcome.HomeRun);
        Assert.Contains(prog.Save().Decisions, d => d.Kind == GameDecisionKind.ForcedOutcome && d.Forced == ForcedOutcome.HomeRun);

        for (var i = 0; i < 4; i++) prog.Advance();
        prog.FinishRemaining();
        var expected = Log(prog);

        prog.SeekTo(4);   // 再生し直しても1打席目の強制本塁打が残る
        prog.FinishRemaining();
        Assert.Equal(expected, Log(prog));
        Assert.True(prog.BuildResult().HasForcedOutcomes);
    }

    [Fact]
    public void FromReproToken_CarriesTheDecisionsOfTheOriginalMatch()
    {
        var original = Seeded(31UL, null);
        original.ForceNext(ForcedOutcome.HomeRun);
        original.SetPitchBattingOverride(true, PitchBattingOverride.ForceTake);
        for (var i = 0; i < 5; i++) original.Advance();

        var token = original.ReproToken()!.Value;
        var decisions = original.Save().Decisions;
        original.FinishRemaining();
        var expected = Log(original);

        var restored = MatchProgression.FromReproToken(
            Team("A"), Team("H"), new GameContext(), token, null, decisions);
        Assert.Equal(5, restored.ConfirmedPlateAppearances);
        restored.FinishRemaining();
        Assert.Equal(expected, Log(restored));

        // 決定列を渡さなければ、同じトークンでも強制発動の無い別の展開になる。
        var plain = MatchProgression.FromReproToken(Team("A"), Team("H"), new GameContext(), token);
        plain.FinishRemaining();
        Assert.NotEqual(expected, Log(plain));
    }

    // ===== ヘッダの母種・係数識別子 =====

    [Fact]
    public void TraceHeader_CarriesSeedAndCoefficientsId()
    {
        var sink = new RingBufferTraceSink();
        var baseCtx = new GameContext();
        var ctx = baseCtx with
        {
            CaptureTrace = true, TraceSink = sink,
            TraceSeed = 0x9f3a5511UL, TraceCoefficientsId = CoefficientFingerprint.Of(baseCtx),
        };
        _ = new MatchProgression(Team("A"), Team("H"), ctx, 0x9f3a5511UL);

        Assert.Equal(0x9f3a5511UL, sink.Header!.Seed);
        Assert.Equal(CoefficientFingerprint.Of(baseCtx), sink.Header.CoefficientsId);

        var json = TraceJson.Game(sink.Header);
        Assert.Contains("\"seed\":\"0x9f3a5511\"", json);
        Assert.Contains("\"coeff\":\"" + CoefficientFingerprint.Of(baseCtx) + "\"", json);
    }

    [Fact]
    public void TraceHeader_WithoutSeedOrCoeff_WritesNull()
    {
        var sink = new RingBufferTraceSink();
        _ = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTrace = true, TraceSink = sink }, 1UL);

        var json = TraceJson.Game(sink.Header!);
        Assert.Contains("\"seed\":null", json);
        Assert.Contains("\"coeff\":null", json);
    }

    [Fact]
    public void CoefficientFingerprint_IsStable_AndFollowsTheCoefficients()
    {
        var a = CoefficientFingerprint.Of(new GameContext());
        Assert.Equal(CoefficientFingerprint.Length, a.Length);
        Assert.Equal(a, CoefficientFingerprint.Of(new GameContext()));

        var changed = new GameContext
        {
            Batting = new GameContext().Batting with { FoulShare = new GameContext().Batting.FoulShare + 0.01 },
        };
        Assert.NotEqual(a, CoefficientFingerprint.Of(changed));
        Assert.NotEqual(a, CoefficientFingerprint.Of(new GameContext { TieBreakEnabled = true }));
    }
}

internal static class ReproRestoreTestExtensions
{
    public static MatchProgression Also(this MatchProgression p, System.Action<MatchProgression> act)
    {
        act(p);
        return p;
    }
}
