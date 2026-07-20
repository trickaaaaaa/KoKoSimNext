using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 実試合供給の入口 <see cref="MatchPlaybackFeed"/> の契約テスト。
/// 固定シードの実試合1試合ぶんを、頭から順の観戦プレー列へ変換できること・
/// スコアが単調に積まれること・各プレーが再生可能であることを検証する。
/// </summary>
public sealed class MatchPlaybackFeedTests
{
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

    private static GameResult PlayGame(ulong seed) =>
        GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
            new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));

    [Fact]
    public void Build_ProducesOrderedReplayableItems()
    {
        var feed = MatchPlaybackFeed.Build(PlayGame(42));

        Assert.NotEmpty(feed);

        var prevAway = 0;
        var prevHome = 0;
        foreach (var item in feed)
        {
            // 各プレーは再生可能（長さ正・ボールあり）。
            Assert.True(item.Play.Dur > 0);
            Assert.NotEmpty(item.Play.Ball);
            Assert.False(string.IsNullOrEmpty(item.BatterName));
            Assert.InRange(item.Inning, 1, 30);

            // スコアは各チーム単調非減少。
            Assert.True(item.AwayScore >= prevAway, "先攻スコアは非減少");
            Assert.True(item.HomeScore >= prevHome, "後攻スコアは非減少");
            prevAway = item.AwayScore;
            prevHome = item.HomeScore;
        }
    }

    [Fact]
    public void Build_ScoresNeverExceedFinalAndScoringPlayExists()
    {
        var result = PlayGame(42);
        var feed = MatchPlaybackFeed.Build(result);

        foreach (var item in feed)
        {
            Assert.True(item.AwayScore <= result.AwayRuns);
            Assert.True(item.HomeScore <= result.HomeRuns);
        }

        // タイムライン付きの得点シーン（本塁打・タイムリー等）が1つ以上ある固定シードを選んでいる。
        Assert.Contains(feed, i => i.RunsScored > 0);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var a = MatchPlaybackFeed.Build(PlayGame(7));
        var b = MatchPlaybackFeed.Build(PlayGame(7));
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Inning, b[i].Inning);
            Assert.Equal(a[i].AwayScore, b[i].AwayScore);
            Assert.Equal(a[i].HomeScore, b[i].HomeScore);
            Assert.Equal(a[i].Play.Dur, b[i].Play.Dur, 6);
        }
    }
}
