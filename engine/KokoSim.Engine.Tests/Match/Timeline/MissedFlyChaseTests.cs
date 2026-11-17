using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit.Abstractions;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 落とした飛球（安打になった飛球）の処理野手が、落下点へ着くのを<b>着地より後</b>にする（issue #1075・表示専用）。
/// 判定は捕球に使える時間を割り引いている（catch_reach_factor・フェンス際の減点）が、絵は野手が全力で着く時刻を
/// そのまま使っていたので、遅れた球でも「落ちる前に落下点へ入っているのに安打」の絵になっていた。
/// </summary>
public sealed class MissedFlyChaseTests
{
    private readonly ITestOutputHelper _out;
    public MissedFlyChaseTests(ITestOutputHelper o) { _out = o; }

    private static readonly FieldGeometry Field = new();

    private const double Hang = 4.5;
    private static double LandAt => TimelineBuilder.ContactAt + Hang;

    private static FieldingPlay MissedFly(double margin, double rollSeconds = 1.6) =>
        FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 10, landingZ: 82,
            fieldedAtSeconds: Hang + rollSeconds, fieldedX: 14, fieldedZ: 76,
            hangTimeSeconds: Hang, apexHeightM: 25, isFly: true)
        with { CatchMarginSeconds = margin };

    private static PlayTimeline Build(FieldingPlay play) =>
        TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

    private static FielderMove[] Legs(PlayTimeline tl, FieldPosition role) =>
        tl.Moves.Where(m => m.Role == role).OrderBy(m => m.T0).ToArray();

    [Theory]
    [InlineData(-0.15)]
    [InlineData(-0.40)]
        public void ChaserReachesTheLandingSpot_AfterTheBallLands(double margin)
    {
        var tl = Build(MissedFly(margin));
        var legs = Legs(tl, FieldPosition.CenterField);

        Assert.Equal(2, legs.Length);   // ①落下点へ追う ②着地後に拾う点へ向かい直す
        var chase = legs[0];
        Assert.True(chase.T1 > LandAt + 0.05, $"落下点への到着 {chase.T1:F2}s が着地 {LandAt:F2}s より後でない");
        Assert.InRange(chase.To.X, 8.0, 11.0);   // 落下点（10, 82）のそば（体の分だけ手前で止まる）
        Assert.InRange(chase.To.Z, 80.0, 83.0);
        Assert.True(legs[1].T0 >= chase.T1 - 1e-9);   // 重ならない
    }

    [Fact]
    public void ArrivalDelayFollowsTheJudgedLateness()
    {
        var small = Legs(Build(MissedFly(-0.20)), FieldPosition.CenterField)[0].T1 - LandAt;
        var large = Legs(Build(MissedFly(-0.60)), FieldPosition.CenterField)[0].T1 - LandAt;
        Assert.InRange(small, 0.15, 0.25);
        Assert.InRange(large, 0.55, 0.65);
        Assert.True(large > small);
    }

    [Fact]
    public void LargeLateness_StillEndsTheChaseBeforeThePickup()
    {
        // フェンス際の減点（最大3s）で遅れが長く、拾う時刻の手前で頭打ちになる
        var play = MissedFly(-2.9, rollSeconds: 1.0);
        var legs = Legs(Build(play), FieldPosition.CenterField);
        var fieldedAt = TimelineBuilder.ContactAt + play.FieldedAtSeconds!.Value;

        Assert.Equal(2, legs.Length);
        Assert.True(legs[0].T1 > LandAt);
        Assert.True(legs[0].T1 <= fieldedAt - 0.12 + 1e-9);
        Assert.Equal(fieldedAt, legs[1].T1, 6);   // 拾う時刻は判定どおり
    }

    [Fact]
    public void NoLegIsFasterThanTheFielderCanRun()
    {
        var tl = Build(MissedFly(-0.40));
        var cf = Field.StandardAlignment().First(f => f.Position == FieldPosition.CenterField);
        var cap = Math.Max(4.0, cf.Attributes.SprintSpeedMps) * 1.001;
        var prev = new TimelinePoint(cf.Location.X, cf.Location.Z);
        foreach (var leg in Legs(tl, FieldPosition.CenterField))
        {
            var d = Math.Sqrt(Math.Pow(leg.To.X - prev.X, 2) + Math.Pow(leg.To.Z - prev.Z, 2));
            Assert.True(d / (leg.T1 - leg.T0) <= cap, $"{leg.Task} が {d / (leg.T1 - leg.T0):F2} m/s（上限 {cap:F2}）");
            prev = leg.To;
        }
    }

    [Fact]
    public void NoMargin_KeepsTheSingleLegPicture()
    {
        var legs = Legs(Build(MissedFly(-0.4) with { CatchMarginSeconds = null }), FieldPosition.CenterField);
        Assert.Single(legs);
    }

    [Fact]
    public void CaughtFly_IsUntouchedByTheMargin()
    {
        var caught = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField, landingX: 10, landingZ: 82, hangTimeSeconds: Hang);
        var a = Build(caught with { CatchMarginSeconds = 0.3 });
        var b = Build(caught with { CatchMarginSeconds = -0.3 });   // アウトなら遅れは絵に出さない
        Assert.Equal(a.Moves, b.Moves);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    public void PickedUpAtTheLanding_OneLegArrivesWhenTheBallLands(double rollSeconds)
    {
        // 判定の拾う時刻が着地の瞬間（転がらない）: 拾う時刻より後へは動かせないので、着地ちょうどに着かせる
        // （落ちる前に着いて待っている絵にしない）
        var play = MissedFly(-0.5, rollSeconds) with { FieldedX = 10, FieldedZ = 82 };
        var legs = Legs(Build(play), FieldPosition.CenterField);
        Assert.Single(legs);
        Assert.Equal(LandAt + rollSeconds, legs[0].T1, 6);
    }

    /// <summary>
    /// 母集団（40試合）: 落とした外野フライの安打のうち、処理野手が落下点へ着く時刻が着地より後になる割合。
    /// 直す前は遅れ −0.15〜−0.45s の球のほとんどが「落ちる前に着いている」絵だった。
    /// </summary>
    [Fact]
    public void RealGames_MissedFlyHits_ChaserArrivesAfterTheLanding()
    {
        int n = 0, twoStage = 0, after = 0, tight = 0, tightAfter = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log)
            {
                var tl = e.Timeline;
                if (tl?.CatchMarginSeconds is not { } m || m >= 0) continue;
                if (e.Result is not (PlateAppearanceResult.Single or PlateAppearanceResult.Double or PlateAppearanceResult.Triple)) continue;
                var flight = tl.Ball.FirstOrDefault(b => b.Kind == BallSegmentKind.Flight);
                if (flight is null) continue;
                var landAt = flight.T1;
                n++;
                var fielderRole = tl.Moves.FirstOrDefault(x => x.Task == FielderTask.FieldBall)?.Role;
                if (fielderRole is null) continue;
                var legs = tl.Moves.Where(x => x.Role == fielderRole && x.Task is FielderTask.FieldBall or FielderTask.Chase)
                    .OrderBy(x => x.T0).ToList();
                var chase = legs[0];
                if (legs.Count >= 2) twoStage++;
                if (chase.T1 >= landAt - 1e-6) after++;
                if (m > -0.5) { tight++; if (chase.T1 >= landAt - 1e-6) tightAfter++; }
            }
        }
        _out.WriteLine($"落球安打 {n} 件・2段 {twoStage}・着地後に着く {after}・際どい(−0.5s以内) {tight}／うち着地後 {tightAfter}");
        Assert.True(n >= 100, $"落球安打が {n} 件しか採れていない＝テストが空回り");
        Assert.True(tight >= 50, $"際どい落球が {tight} 件しか採れていない");
        Assert.Equal(n, after);                   // 落とした飛球で、落ちる前に落下点へ着いて待っている絵は無い
        Assert.Equal(tight, tightAfter);
        Assert.True(twoStage >= 100, $"追う→拾う直しの2段が {twoStage} 件しか出ていない");
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
