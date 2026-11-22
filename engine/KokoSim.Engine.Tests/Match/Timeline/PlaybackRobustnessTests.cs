using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>再生層の頑健性（issue #1215）: 長さ0レッグの NaN、Ball 列の時刻順。</summary>
public sealed class PlaybackRobustnessTests
{
    [Fact]
    public void RunnerAt_ZeroLengthLeg_IsNeverNaN_AndLandsOnTheEndpoint()
    {
        var from = new PlaybackVec(0, 0);
        var to = new PlaybackVec(10, 20);
        var runner = new PlaybackRunner
        {
            Segs = new[] { new PlaybackRun(1.0, 1.0, from, to) },
        };

        // 開始時刻ちょうど（0/0）・前後の時刻のどれでも有限
        foreach (var t in new[] { 0.5, 1.0, 1.5 })
        {
            var pos = PlaybackEvaluator.RunnerAt(runner, t);
            if (t < 1.0) { Assert.Null(pos); continue; }
            Assert.True(double.IsFinite(pos!.Value.X) && double.IsFinite(pos.Value.Y), $"t={t}");
            Assert.Equal(to, pos.Value);
        }
    }

    [Fact]
    public void RunnerAt_NormalLeg_StillInterpolates()
    {
        var runner = new PlaybackRunner
        {
            Segs = new[] { new PlaybackRun(1.0, 3.0, new PlaybackVec(0, 0), new PlaybackVec(10, 20)) },
        };
        var mid = PlaybackEvaluator.RunnerAt(runner, 2.0)!.Value;
        Assert.Equal(5.0, mid.X, 9);
        Assert.Equal(10.0, mid.Y, 9);
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team BuildTeam(string name)
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

    // BallAt／CameraPlan.FindSegmentAt は「先頭から見て最初に当たった区間」を返す＝Ball 列が
    // 時刻順（開始が後退しない・各区間は T0<=T1）であることに依存する。実出力で成り立つことを固定する。
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    public void ToPlaybackPlay_BallSegments_AreTimeOrdered_AndRunnerLegsAreWellFormed(ulong seed)
    {
        var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
            new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));

        var checkedPlays = 0;
        foreach (var e in r.Log)
        {
            if (e.Timeline is null) continue;
            var play = PlayTimelineAdapter.ToPlaybackPlay(e.Timeline);
            for (var i = 0; i < play.Ball.Count; i++)
            {
                var s = play.Ball[i];
                Assert.True(s.T1 >= s.T0, $"{play.Name} ball[{i}] T1<T0");
                if (i > 0) Assert.True(s.T0 >= play.Ball[i - 1].T0, $"{play.Name} ball[{i}] 開始が前の区間より前");
            }
            foreach (var runner in play.Runners)
            {
                foreach (var leg in runner.Segs) Assert.True(leg.T1 >= leg.T0, $"{play.Name} {runner.Label} レッグの T1<T0");
                for (var t = 0.0; t <= play.Dur + 0.5; t += 0.25)
                    if (PlaybackEvaluator.RunnerAt(runner, t, respectHideAt: false) is { } p)
                        Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y), $"{play.Name} {runner.Label} t={t}");
            }
            checkedPlays++;
        }
        Assert.True(checkedPlays > 0);
    }
}
