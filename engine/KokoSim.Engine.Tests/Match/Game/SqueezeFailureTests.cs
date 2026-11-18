using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #1061: スクイズ不成立の3分類（ウエスト＝ボール／バント空振り＝ストライク／バントファウル＝ストライクで
/// 走者は三塁へ戻る）と、挟殺された三塁走者の再生データ（<see cref="LivePitch.SqueezeRundownPlay"/>）。
/// </summary>
public sealed class SqueezeFailureTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly System.Collections.Generic.IReadOnlyList<Fielder> Fielders = Field.StandardAlignment();
    private static readonly KokoSim.Engine.Match.Fielding.FieldingCoefficients FieldingCoeff = new();
    private static readonly BatterAttributes Batter = new() { Contact = 55 };
    private static readonly PitcherAttributes Pitcher = PitcherAttributes.LeagueAverage;
    private static readonly Player Bunter = new() { Bunt = 55, Speed = 60 };
    private static readonly Player ThirdRunner = new() { Speed = 55 };

    private static AtBatContext Ctx() => new() { Fielders = Field.StandardAlignment() };

    /// <summary>1球目のスクイズを、<paramref name="pick"/> が真を返す結果が出るまでシードを変えて投げる。</summary>
    private static (AtBatSession Session, PitchResolution Res) FirstSqueezePitch(
        System.Func<PitchKind, bool> pick, int strikes = 0, int balls = 0, double waste = 0.0)
    {
        for (ulong seed = 1; seed <= 2000; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var session = AtBatSession.Begin(Batter, Pitcher, Ctx(), batterPlayer: Bunter,
                thirdBaseRunner: ThirdRunner, squeezeWasteProbability: waste,
                initialBalls: balls, initialStrikes: strikes);
            var res = session.ThrowNextPitch(rng, PitchBattingOverride.Squeeze);
            if (pick(session.PitchLog[0].Kind)) return (session, res);
        }
        throw new Xunit.Sdk.XunitException("狙った結果のスクイズが見つからなかった");
    }

    [Fact]
    public void Waste_StaysABall()
    {
        var (s, res) = FirstSqueezePitch(_ => true, waste: 1.0);
        Assert.Equal(PitchKind.Ball, s.PitchLog[0].Kind);
        Assert.Equal(1, s.Balls);
        Assert.Equal(0, s.Strikes);
        Assert.True(res.SqueezeRunnerCaughtAtThird);
        Assert.False(res.EndsPlateAppearance);
    }

    [Fact]
    public void MissedBunt_IsAStrike_AndRunnerIsCaught()
    {
        var (s, res) = FirstSqueezePitch(k => k == PitchKind.SwingingStrike);
        Assert.Equal(1, s.Strikes);
        Assert.Equal(0, s.Balls);
        Assert.True(res.SqueezeRunnerCaughtAtThird);
        Assert.False(res.EndsPlateAppearance);
    }

    [Fact]
    public void MissedBunt_WithTwoStrikes_IsAStrikeout()
    {
        var (s, res) = FirstSqueezePitch(k => k == PitchKind.SwingingStrike, strikes: 2);
        Assert.True(res.EndsPlateAppearance);
        Assert.True(s.IsComplete);
        Assert.Equal(PlateAppearanceResult.Strikeout, s.Result.Result);
        Assert.Equal(3, s.Strikes);
    }

    [Fact]
    public void FoulBunt_IsAStrike_RunnerReturnsAndIsNotOut()
    {
        var (s, res) = FirstSqueezePitch(k => k == PitchKind.Foul);
        Assert.Equal(1, s.Strikes);
        Assert.Equal(0, s.Balls);
        Assert.False(res.SqueezeRunnerCaughtAtThird);   // ボールデッド＝三塁へ戻るだけ
        Assert.False(res.EndsPlateAppearance);
        Assert.False(s.IsComplete);
    }

    [Fact]
    public void FoulBunt_WithTwoStrikes_IsAStrikeout()
    {
        var (s, res) = FirstSqueezePitch(k => k == PitchKind.Foul, strikes: 2);
        Assert.True(res.EndsPlateAppearance);
        Assert.False(res.SqueezeRunnerCaughtAtThird);
        Assert.Equal(PlateAppearanceResult.Strikeout, s.Result.Result);
    }

    [Fact]
    public void Waste_OnThreeBalls_IsAWalk()
    {
        var (s, res) = FirstSqueezePitch(_ => true, balls: 3, waste: 1.0);
        Assert.True(res.EndsPlateAppearance);
        Assert.Equal(PlateAppearanceResult.Walk, s.Result.Result);
    }

    [Fact]
    public void SqueezeResolver_Foul_DoesNotPutRunnerOut_AndWasteIsFlagged()
    {
        var c = new BaserunningCoefficients();
        var rng = new Xoshiro256Random(5);
        var sawFoul = false;
        var sawMiss = false;
        for (var i = 0; i < 2000 && !(sawFoul && sawMiss); i++)
        {
            var o = SqueezeResolver.Resolve(Bunter, ThirdRunner, Pitcher, 0.0, 0.0, null, null, Field, Fielders, FieldingCoeff, 0.0, c, rng);
            if (o.Bunt == BuntResult.Foul) { Assert.False(o.RunnerOut); Assert.False(o.Wasted); sawFoul = true; }
            if (o.Bunt == BuntResult.MissedBunt) { Assert.True(o.RunnerOut); Assert.False(o.Wasted); sawMiss = true; }
        }
        Assert.True(sawFoul && sawMiss);
        var w = SqueezeResolver.Resolve(Bunter, ThirdRunner, Pitcher, 0.0, 0.0, null, null, Field, Fielders, FieldingCoeff, 1.0, c, rng);
        Assert.True(w.Wasted);
        Assert.True(w.RunnerOut);
    }

    // ── GameEngine（1球観測） ──

    private sealed record Seen(int OutsBefore, LivePitch Pitch);

    /// <summary>走者三塁（アウト数 <paramref name="outs"/>）で1球目に手動スクイズを出し、スクイズ不成立の球を集める。</summary>
    private static List<Seen> SqueezePitches(int outs, int strikes)
    {
        var list = new List<Seen>();
        var start = new ScenarioStart { Inning = 5, IsTop = true, Outs = outs, OnThird = true, Strikes = strikes };
        for (ulong seed = 1; seed <= 150; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true, CapturePitches = true }, seed, start);
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Squeeze);
            prog.AdvancePitch();
            if (prog.LastPitch is not { } lp || !lp.RunnerStarted(3)) continue;
            list.Add(new Seen(outs, lp));
        }
        return list;
    }

    [Fact]
    public void RundownPitch_CarriesThePlay_AndTheStateAfterIt()
    {
        var seen = SqueezePitches(outs: 0, strikes: 0);
        var caught = seen.Where(s => s.Pitch.SqueezeRundownPlay is not null).ToList();
        Assert.NotEmpty(caught);
        foreach (var (before, lp) in caught)
        {
            // 観測の塁・アウトは挟殺後の実状態
            Assert.False(lp.BaseThird);
            Assert.Equal(before + 1, lp.Outs);
            Assert.Contains(lp.Pitch.Kind, new[] { PitchKind.Ball, PitchKind.SwingingStrike });

            var play = lp.SqueezeRundownPlay!;
            var legs = play.Runners.Single().Segs;
            Assert.True(legs.Last().OutAtEnd);
            Assert.False(legs.Last().Force);
            // 絵と結果が食い違わない: 送球が走者より先に着き、タッチでアウト
            Assert.True(play.Ball.Last().T1 < legs.Last().T1);
            Assert.NotNull(play.Judgements);
        }
        // ボール（ウエスト）と空振りの両方が観測される
        Assert.Contains(caught, s => s.Pitch.Pitch.Kind == PitchKind.Ball);
        Assert.Contains(caught, s => s.Pitch.Pitch.Kind == PitchKind.SwingingStrike);
    }

    [Fact]
    public void FoulBunt_KeepsRunnerOnThird_WithoutRundownPlay()
    {
        var seen = SqueezePitches(outs: 0, strikes: 0);
        var fouls = seen.Where(s => s.Pitch.Pitch.Kind == PitchKind.Foul).ToList();
        Assert.NotEmpty(fouls);
        foreach (var (before, lp) in fouls)
        {
            Assert.True(lp.BaseThird);
            Assert.Equal(before, lp.Outs);
            Assert.Null(lp.SqueezeRundownPlay);
            Assert.Equal(1, lp.Pitch.StrikesAfter);
        }
    }

    [Fact]
    public void TwoOuts_Rundown_IsThirdOut_ObservedWithThePlay_BeforeTheSideChanges()
    {
        var seen = SqueezePitches(outs: 2, strikes: 0);
        var thirdOut = seen.Where(s => s.Pitch.SqueezeRundownPlay is not null).ToList();
        Assert.NotEmpty(thirdOut);
        foreach (var (_, lp) in thirdOut)
        {
            Assert.Equal(3, lp.Outs);
            Assert.False(lp.EndsPlateAppearance);   // 打席は未決着のまま、絵を出してから攻守交代
        }
    }

    [Fact]
    public void TwoOuts_TwoStrikes_MissedBunt_IsTheThirdOutByStrikeout_RunnerIsNotAlsoOut()
    {
        var seen = SqueezePitches(outs: 2, strikes: 2);
        var k = seen.Where(s => s.Pitch.Pitch.Kind == PitchKind.SwingingStrike).ToList();
        Assert.NotEmpty(k);
        foreach (var (_, lp) in k)
        {
            Assert.True(lp.EndsPlateAppearance);
            Assert.Null(lp.SqueezeRundownPlay);   // 三振が3アウト目＝走者のアウトは起きない
        }
    }

    [Fact]
    public void RundownPlay_NeverAttachedWhenCaptureTimelinesOff()
    {
        var start = new ScenarioStart { Inning = 5, IsTop = true, Outs = 0, OnThird = true };
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = false, CapturePitches = true }, seed, start);
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Squeeze);
            prog.AdvancePitch();
            if (prog.LastPitch is { } lp) Assert.Null(lp.SqueezeRundownPlay);
        }
    }

    // ── タイムライン ──

    [Fact]
    public void BuildSqueezeRundown_IsDeterministic_AndEndsWithATagOut()
    {
        var c = new BaserunningCoefficients();
        var catcher = Pos(FieldPosition.Catcher);
        var a = TimelineBuilder.BuildSqueezeRundown(ThirdRunner, catcher, waste: false, c, Field);
        var b = TimelineBuilder.BuildSqueezeRundown(ThirdRunner, catcher, waste: false, c, Field);
        Assert.Equal(a.ResolvedAt, b.ResolvedAt);
        Assert.Equal(a.Runners.Count, b.Runners.Count);

        var last = a.Runners.OrderBy(r => r.T0).Last();
        Assert.True(last.OutAtEnd);
        Assert.False(last.Force);
        // 走者は三本間（三塁と本塁を結ぶ線上）から出ない
        var third = Field.ThirdBase;
        foreach (var r in a.Runners)
        {
            foreach (var pt in new[] { r.From, r.To })
            {
                var cross = (pt.X - third.X) * (0 - third.Z) - (pt.Z - third.Z) * (0 - third.X);
                Assert.True(System.Math.Abs(cross) < 1e-6, "走者が三本間から外れた");
            }
        }
        // 送球は捕手→三塁手
        var throwSeg = a.Ball.Single(s => s.Kind == BallSegmentKind.Throw);
        Assert.Equal(FieldPosition.Catcher, throwSeg.FromRole);
        Assert.Equal(FieldPosition.ThirdBase, throwSeg.ToRole);
        Assert.True(throwSeg.T1 < last.T1);

        var waste = TimelineBuilder.BuildSqueezeRundown(ThirdRunner, catcher, waste: true, c, Field);
        Assert.NotEqual(a.Captions[0].Text, waste.Captions[0].Text);
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50, Bunt = 60,
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
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }
}
