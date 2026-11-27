using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 字幕「頭を越えた！」の条件（issue #1738）。旧条件は「着地が処理野手の守備位置の深さ＋1m超」だけで、
/// 背走して捕った凡打（外野へ飛んだ凡打の67%）や、着地とほぼ同時に拾った単打（字幕の付いた単打の約8割）にまで付いていた。
/// 新条件は「安打で、回収した野手の後ろへ落ち、野手が着地から1秒以上たってから追いついた」だけ。表示専用。
/// </summary>
public sealed class OverTheHeadCaptionTests
{
    private const string Caption = "頭を越えた！";
    private static readonly FieldGeometry Field = new();
    private static readonly IReadOnlyList<Fielder> Fielders = Field.StandardAlignment();

    private static double StartRange(FieldPosition role)
    {
        var f = Fielders.First(x => x.Position == role);
        return Math.Sqrt(f.Location.X * f.Location.X + f.Location.Z * f.Location.Z);
    }

    /// <summary>中堅手の後ろ <paramref name="behindM"/> m に落ちた安打。着地から <paramref name="chaseSeconds"/> 秒後に回収。</summary>
    private static FieldingPlay CenterHit(BattedBallResult result, double behindM, double chaseSeconds)
    {
        var range = StartRange(FieldPosition.CenterField) + behindM;
        const double hang = 4.5;
        return new FieldingPlay
        {
            Result = result,
            LandingX = 0.0, LandingZ = range, HangTimeSeconds = hang, ApexHeightM = 30.0,
            RangeM = range, BearingDeg = 0.0, FielderRole = FieldPosition.CenterField,
            FieldedAtSeconds = hang + chaseSeconds, BatterToFirstSeconds = 4.2, IsFly = true,
            FielderThrowSpeedMps = 35.0, CatchMarginSeconds = -2.0, Class = BattedBallClass.Fly,
        };
    }

    [Fact]
    public void BallBehindTheFielder_ChasedForSeconds_IsOverTheHead()
    {
        Assert.True(TimelineBuilder.IsOverTheHead(CenterHit(BattedBallResult.Double, 20.0, 2.0), Fielders));
        Assert.True(TimelineBuilder.IsOverTheHead(CenterHit(BattedBallResult.Triple, 25.0, 3.0), Fielders));
        Assert.True(TimelineBuilder.IsOverTheHead(CenterHit(BattedBallResult.Single, 15.0, 1.5), Fielders));
    }

    [Fact]
    public void SingleFieldedAlmostAtLanding_IsNotOverTheHead()
    {
        // 守備位置の15m後ろに落ちても、背走した野手が着地の0.3秒後に拾った＝野手の後ろへ抜けてはいない。
        Assert.False(TimelineBuilder.IsOverTheHead(CenterHit(BattedBallResult.Single, 15.0, 0.3), Fielders));
        Assert.False(TimelineBuilder.IsOverTheHead(CenterHit(BattedBallResult.Double, 15.0, 0.9), Fielders));
    }

    [Fact]
    public void BallShortOfTheFielder_IsNotOverTheHead()
    {
        // 長く追ったが、着地は回収した野手の守備位置より手前（ギャップ・フェンス際の浅い当たり）。
        Assert.False(TimelineBuilder.IsOverTheHead(CenterHit(BattedBallResult.Double, 0.5, 3.0), Fielders));
        Assert.False(TimelineBuilder.IsOverTheHead(CenterHit(BattedBallResult.Double, -10.0, 3.0), Fielders));
    }

    [Fact]
    public void OutsErrorsAndHomeRuns_AreNeverOverTheHead()
    {
        foreach (var result in new[] { BattedBallResult.Out, BattedBallResult.Error, BattedBallResult.HomeRun })
            Assert.False(TimelineBuilder.IsOverTheHead(CenterHit(result, 20.0, 2.0), Fielders), result.ToString());
    }

    [Fact]
    public void InfieldBall_IsNotOverTheHead()
    {
        var play = CenterHit(BattedBallResult.Double, 20.0, 2.0) with { RangeM = 30.0 };
        Assert.False(TimelineBuilder.IsOverTheHead(play, Fielders));
    }

    [Fact]
    public void Caption_FollowsThePredicate_ThroughBuildBattedBall()
    {
        var over = CenterHit(BattedBallResult.Double, 20.0, 2.0);
        var near = CenterHit(BattedBallResult.Single, 15.0, 0.3);

        Assert.Contains(Build(over).Captions, c => c.Text == Caption);
        Assert.DoesNotContain(Build(near).Captions, c => c.Text == Caption);

        static PlayTimeline Build(FieldingPlay play)
            => TimelineBuilder.BuildBattedBall(
                play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
    }

    [Fact]
    public void RealGames_CaptionNeverOnOutsAndRarelyOnSingles()
    {
        int outs = 0, outsCaptioned = 0, singles = 0, singlesCaptioned = 0, extra = 0, extraCaptioned = 0;
        foreach (var e in Plays())
        {
            if (e.Timeline is null) continue;
            var captioned = e.Timeline.Captions.Any(c => c.Text == Caption);
            switch (e.Result)
            {
                case PlateAppearanceResult.InPlayOut:
                case PlateAppearanceResult.ReachedOnError:
                    outs++; if (captioned) outsCaptioned++;
                    break;
                case PlateAppearanceResult.Single:
                    singles++; if (captioned) singlesCaptioned++;
                    break;
                case PlateAppearanceResult.Double:
                case PlateAppearanceResult.Triple:
                    extra++; if (captioned) extraCaptioned++;
                    break;
            }
        }

        Assert.True(outs > 300 && singles > 100 && extra > 30, $"標本が少ない（凡打 {outs}・単打 {singles}・長打 {extra}）");
        Assert.Equal(0, outsCaptioned);
        // 実測（40万打席）: 外野へ飛んだ単打の 0.4%・長打の 65〜87% に付く。単打の頭越しは例外的。
        Assert.True((double)singlesCaptioned / singles <= 0.03, $"単打の {singlesCaptioned}/{singles} に付いている");
        Assert.True(extraCaptioned > 0, "長打に1本も付いていない＝字幕が死んでいる");
    }

    private static IEnumerable<PlayLogEntry> Plays()
    {
        var logs = new IReadOnlyList<PlayLogEntry>[60];
        System.Threading.Tasks.Parallel.For(0, logs.Length, i =>
            logs[i] = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random((ulong)(i + 1))).Log);
        return logs.SelectMany(l => l);
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
            Reserves = new[]
            {
                Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage },
            },
        };
    }
}
