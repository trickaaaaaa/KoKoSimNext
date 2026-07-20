using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// フォース進塁の可視化（issue #25）。内野ゴロで押し出される走者は、打球発生（接触=0.85s）まで
/// 盤面から消えてはならない＝再生層 <see cref="PlaybackEvaluator.RunnerAt"/> が t=0 で元の塁を返すこと。
/// 表示専用の契約テスト（判定・乱数順には触れない）。
/// </summary>
public sealed class ForceAdvanceVisibilityTests
{
    private static readonly FieldGeometry Field = new();

    private static FieldingPlay GrounderOut() => new()
    {
        Result = BattedBallResult.Out,
        LandingX = -9.0, LandingZ = 30.0, HangTimeSeconds = 0.3, ApexHeightM = 0.4,
        RangeM = 31.3, BearingDeg = -16.0, FielderRole = FieldPosition.Shortstop,
        FieldedAtSeconds = 1.4, ThrowArriveSeconds = 3.0, BatterToFirstSeconds = 4.2, IsFly = false,
    };

    private static PlayTimeline GrounderTimeline() => TimelineBuilder.BuildBattedBall(
        GrounderOut(), Field.StandardAlignment(), Field, null, runnersOn: true,
        TimelineBuilder.DescribeResult(GrounderOut()));

    private static PlaybackVec Vec(Vector3D p) => new(p.X, p.Z);

    private static void AssertNear(PlaybackVec expected, PlaybackVec? actual, string what)
    {
        Assert.True(actual.HasValue, $"{what}: 走者が描画されていない（null）");
        Assert.Equal(expected.X, actual!.Value.X, 3);
        Assert.Equal(expected.Y, actual.Value.Y, 3);
    }

    [Fact]
    public void ForcedRunner_IsOnBase_BeforeContact_AndAdvancesAfter()
    {
        // 一塁走者のフォース進塁（1→2）。接触前（t=0 = 投球フェーズの固定時刻）でも一塁に立っていること。
        var moves = new List<RunnerMove> { new(new Player { Speed = 50 }, 1, 2, Out: false) };
        var tl = TimelineBuilder.AppendRunnerLegs(GrounderTimeline(), moves, Field);
        tl = TimelineBuilder.AppendHeldRunners(tl, first: true, second: false, third: false, moves, Field);

        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var runner = Assert.Single(play.Runners, r => r.Label == "走1");

        AssertNear(Vec(Field.FirstBase), PlaybackEvaluator.RunnerAt(runner, 0.0), "投球中(t=0)の一塁走者");
        AssertNear(Vec(Field.FirstBase), PlaybackEvaluator.RunnerAt(runner, 0.5), "接触前(t=0.5)の一塁走者");
        AssertNear(Vec(Field.SecondBase), PlaybackEvaluator.RunnerAt(runner, play.Dur), "終了時の一塁走者");

        // 静止トークン（走一）とは二重に描かない。
        Assert.DoesNotContain(play.Runners, r => r.Label == "走一");
    }

    [Fact]
    public void ForcedRunners_FirstAndSecond_BothVisibleFromStart()
    {
        // 一・二塁のフォース進塁（1→2, 2→3）。両者とも接触前から塁上に描かれる。
        var moves = new List<RunnerMove>
        {
            new(new Player { Speed = 50 }, 2, 3, Out: false),
            new(new Player { Speed = 50 }, 1, 2, Out: false),
        };
        var tl = TimelineBuilder.AppendRunnerLegs(GrounderTimeline(), moves, Field);
        tl = TimelineBuilder.AppendHeldRunners(tl, first: true, second: true, third: false, moves, Field);

        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var fromSecond = Assert.Single(play.Runners, r => r.Label == "走1"); // moves の順にラベル付与
        var fromFirst = Assert.Single(play.Runners, r => r.Label == "走2");

        AssertNear(Vec(Field.SecondBase), PlaybackEvaluator.RunnerAt(fromSecond, 0.0), "投球中の二塁走者");
        AssertNear(Vec(Field.FirstBase), PlaybackEvaluator.RunnerAt(fromFirst, 0.0), "投球中の一塁走者");
        AssertNear(Vec(Field.ThirdBase), PlaybackEvaluator.RunnerAt(fromSecond, play.Dur), "終了時の二塁走者");
        AssertNear(Vec(Field.SecondBase), PlaybackEvaluator.RunnerAt(fromFirst, play.Dur), "終了時の一塁走者");
    }

    [Fact]
    public void ForcedOutRunner_IsVisibleUntilForceOut()
    {
        // 併殺の二塁封殺（1→2 でアウト）。接触前は一塁に立ち、封殺の瞬間（hideAt）に消える。
        var moves = new List<RunnerMove> { new(new Player { Speed = 50 }, 1, 2, Out: true) };
        var tl = TimelineBuilder.AppendRunnerLegs(GrounderTimeline(), moves, Field);

        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var runner = Assert.Single(play.Runners, r => r.Label == "走1");

        AssertNear(Vec(Field.FirstBase), PlaybackEvaluator.RunnerAt(runner, 0.0), "投球中の一塁走者");
        Assert.NotNull(runner.HideAt);
        Assert.Null(PlaybackEvaluator.RunnerAt(runner, runner.HideAt!.Value + 0.01)); // 封殺後は消える
    }

    [Fact]
    public void InGame_EveryRunnerTrack_StartsAtTimeZero()
    {
        // 実試合の出力契約: 打者走者("打")以外の走者トラックは必ず t=0 から描かれる
        //（＝どの打球でも「塁上に居るのに盤面に居ない」時間帯が存在しない）。
        var found = 0;
        for (ulong s = 0; s < 20; s++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(s));
            foreach (var tl in r.Log.Select(e => e.Timeline).Where(t => t is not null)!)
            {
                var play = PlayTimelineAdapter.ToPlaybackPlay(tl!);
                foreach (var runner in play.Runners.Where(x => x.Label != "打"))
                {
                    Assert.NotEmpty(runner.Segs);
                    Assert.Equal(0.0, runner.Segs[0].T0, 6);
                    Assert.NotNull(PlaybackEvaluator.RunnerAt(runner, 0.0));
                    found++;
                }
            }
        }
        Assert.True(found > 0, "20シードで塁上走者のトラックが1件も見つからない");
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
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }
}
