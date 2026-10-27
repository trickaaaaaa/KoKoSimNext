using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #696: 1球ごとの観測 <see cref="LivePitch.RunnersStarted"/> ＝「この球で投球と同時にスタートを切った走者」。
/// 試合画面はこれを見て、投手のモーション開始時に「走った！」を出す。盗塁・エンドラン・スクイズを1つの読み口に
/// まとめ、盗塁が打球・ファウルで取り消されても「走った」事実は残す。観測専用＝結果・乱数は変えない。
/// </summary>
public sealed class RunnersStartedObservationTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 70, ArmStrength = 50, Fielding = 50, Catching = 50,
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

    /// <summary>窓ごとに <paramref name="reserve"/> で両軍へ采配を予約しながら1試合回し、(投げる前の局面, 投げた球) を集める。</summary>
    private static List<(LiveAtBat Before, LivePitch Pitch)> Play(ulong seed, Action<MatchProgression, bool>? reserve,
        bool capture = true)
    {
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = capture, CapturePitches = capture }, seed);
        var list = new List<(LiveAtBat, LivePitch)>();
        LiveAtBat? before = null;
        LivePitch? seen = null;
        while (true)
        {
            var r = prog.AdvancePitch();
            var lp = prog.LastPitch;
            if (lp is not null && !ReferenceEquals(lp, seen) && before is not null) { seen = lp; list.Add((before, lp)); }
            if (r == AdvancePitchResult.Finished) break;
            if (r == AdvancePitchResult.Pitch)
            {
                before = prog.CurrentAtBat;
                if (reserve is not null) { reserve(prog, true); reserve(prog, false); }
            }
        }
        return list;
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    public void Steal_MarksTheRunner_EvenWhenAFoulOrBattedBallCancelsIt(ulong seed)
    {
        var pitches = Play(seed, (p, away) => p.SetStealAttempt(away, StartType.Gamble, StealTarget.Second));

        foreach (var (before, lp) in pitches)
        {
            if (lp.StealPlay is not null) Assert.True(lp.RunnerStarted(1), "盗塁の再生がある球で一塁走者のスタートが載っていない");
            if (!before.BaseFirst) Assert.False(lp.RunnerStarted(1), "一塁が空なのに一塁走者がスタートしている");
            Assert.False(lp.RunnerStarted(3), "二盗だけの試合で三塁走者がスタートしている");
        }
        Assert.Contains(pitches, x => x.Pitch.StealPlay is not null);
        // 打球・ファウルで盗塁が取り消された球でも、走った事実は残る（StealPlay は消える）。
        Assert.Contains(pitches, x => x.Pitch.StealPlay is null && x.Pitch.RunnerStarted(1)
            && x.Pitch.Pitch.Kind is PitchKind.Foul or PitchKind.InPlay);
    }

    [Fact]
    public void HitAndRun_MarksTheRunnerOnFirst()
    {
        var pitches = Play(3, (p, away) => p.SetOffensiveSign(away, OffensiveSign.HitAndRun));
        var withRunner = 0;
        foreach (var (before, lp) in pitches)
        {
            if (!before.BaseFirst) continue;
            withRunner++;
            Assert.True(lp.RunnerStarted(1), "エンドランの球で一塁走者のスタートが載っていない");
        }
        Assert.True(withRunner > 0);
    }

    [Fact]
    public void Squeeze_MarksTheRunnerOnThird()
    {
        var pitches = Play(5, (p, away) => p.SetPitchBattingOverride(away, PitchBattingOverride.Squeeze));
        Assert.Contains(pitches, x => x.Before.BaseThird && x.Pitch.RunnerStarted(3));
        Assert.DoesNotContain(pitches, x => !x.Before.BaseThird && x.Pitch.RunnerStarted(3));
    }

    [Fact]
    public void NoSign_BasesEmpty_NobodyStarts()
    {
        foreach (var (before, lp) in Play(11, null))
        {
            if (!before.BaseFirst && !before.BaseSecond && !before.BaseThird)
                Assert.Equal(0, lp.RunnersStarted);
        }
    }
}
