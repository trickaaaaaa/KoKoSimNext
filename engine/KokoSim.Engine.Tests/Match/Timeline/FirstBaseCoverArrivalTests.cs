using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 一塁封殺の送球が一塁へ着くとき、受け手（カバー役）が先に一塁へ着いていること（issue #672）。
/// 症状: 一塁手のゴロで投手が一塁へ来ていないのに、一塁手が無人の一塁へ送球してアウトになる。
/// 送球の受け手は表の担当と無関係に <c>firstBaseCoverer</c> へ固定されるが、一塁へ走らせる側は
/// 「投手が別の持ち場へ走っているときは注入しない」条件付きなので、両者がずれうる。
/// </summary>
public sealed class FirstBaseCoverArrivalTests
{
    private static double Distance(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>一塁手が処理する一塁封殺ゴロを、捕球位置×時刻で総当たりに作る。</summary>
    public static IEnumerable<object[]> Plays()
    {
        foreach (var x in new[] { 8.0, 12.0, 16.0, 19.0, 22.0 })
        foreach (var z in new[] { 14.0, 18.0, 22.0, 26.0, 32.0 })
        foreach (var fieldedAt in new[] { 0.9, 1.4, 2.0 })
            yield return new object[] { x, z, fieldedAt };
    }

    [Theory]
    [MemberData(nameof(Plays))]
    public void ThrowToFirst_ReceiverIsAtFirstByTheTimeTheBallArrives(double x, double z, double fieldedAt)
    {
        var field = new FieldGeometry();
        var play = FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.FirstBase, fieldedX: x, fieldedZ: z,
            fieldedAtSeconds: fieldedAt, throwArriveSeconds: fieldedAt + 1.0);
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var basePt = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);
        var throws = tl.Ball.Where(s => s.Kind == BallSegmentKind.Throw && s.ToBase == "first").ToList();
        foreach (var t in throws)
        {
            var receiver = t.ToRole!.Value;
            var legs = tl.Moves.Where(m => m.Role == receiver).OrderBy(m => m.T1).ToList();
            // 受け手の「T1 までに着いている位置」: T1 が送球到達以前の最後のレッグの行き先（無ければ出発点）
            var arrived = legs.LastOrDefault(m => m.T1 <= t.T1 + 1e-6);
            var coversFirst = arrived is not null && Distance(arrived.To, basePt) < 1.0;
            Assert.True(coversFirst,
                $"一塁へ送球 {t.FromRole}→{receiver} が T={t.T1:F2}s に着くが、{receiver} は一塁に着いていない"
                + $"（捕球 x={x} z={z} 捕球時刻={fieldedAt}／{receiver}のレッグ: "
                + string.Join(" ", legs.Select(m => $"{m.Task}→({m.To.X:F1},{m.To.Z:F1}) T1={m.T1:F2}")) + "）");
        }
    }
}

/// <summary>実試合で、一塁封殺の送球が着く前に受け手が一塁へ着いているかを数える（issue #672）。</summary>
public sealed class FirstBaseCoverArrivalInGamesTests
{
    private static KokoSim.Engine.Players.Player Pos(FieldPosition pos, int speed) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = speed, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static KokoSim.Engine.Match.Game.Team BuildTeam(string name, int speed)
    {
        var order = new List<KokoSim.Engine.Players.Player>
        {
            Pos(FieldPosition.Catcher, speed), Pos(FieldPosition.FirstBase, speed),
            Pos(FieldPosition.SecondBase, speed), Pos(FieldPosition.ThirdBase, speed),
            Pos(FieldPosition.Shortstop, speed), Pos(FieldPosition.LeftField, speed),
            Pos(FieldPosition.CenterField, speed), Pos(FieldPosition.RightField, speed),
            Pos(FieldPosition.Pitcher, speed) with { Name = name + "P", Pitching = KokoSim.Engine.Players.PitcherAttributes.LeagueAverage },
        };
        return new KokoSim.Engine.Match.Game.Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[]
            {
                Pos(FieldPosition.Pitcher, speed) with { Name = name + "R", Pitching = KokoSim.Engine.Players.PitcherAttributes.LeagueAverage },
            },
        };
    }

    private static double Distance(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    [Fact]
    [Trait("Category", "Heavy")]
    public void ThrowToFirst_ReceiverIsAtFirstBeforeTheBallArrives()
    {
        var field = new FieldGeometry();
        var basePt = new TimelinePoint(field.FirstBase.X, field.FirstBase.Z);
        var total = 0; var late = new List<string>(); var worst = 0.0;
        var sync = new object();

        System.Threading.Tasks.Parallel.For(1, 201, i =>
        {
            var r = KokoSim.Engine.Match.Game.GameEngine.Play(BuildTeam("A", 50), BuildTeam("H", 50),
                new KokoSim.Engine.Match.Game.GameContext { CaptureTimelines = true },
                new KokoSim.Engine.Core.Xoshiro256Random((ulong)i));
            foreach (var e in r.Log)
            {
                var tl = e.Timeline;
                if (tl is null) continue;
                foreach (var t in tl.Ball.Where(s => s.Kind == BallSegmentKind.Throw && s.ToBase == "first" && s.ToRole is not null))
                {
                    var receiver = t.ToRole!.Value;
                    // 受け手が一塁へ着く時刻＝一塁へ向かう最後のレッグの T1（無ければ立っているだけ＝着いていない）
                    var leg = tl.Moves.Where(m => m.Role == receiver && Distance(m.To, basePt) < 1.0)
                        .OrderBy(m => m.T1).LastOrDefault();
                    var arriveAt = leg?.T1 ?? double.PositiveInfinity;
                    lock (sync)
                    {
                        total++;
                        var lateBy = arriveAt - t.T1;
                        if (lateBy > 0.02)
                        {
                            worst = Math.Max(worst, double.IsInfinity(lateBy) ? 99 : lateBy);
                            if (late.Count < 6)
                                late.Add($"seed{i} {tl.Result} {t.FromRole}→{receiver} 球T1={t.T1:F2} 受け手着={(double.IsInfinity(arriveAt) ? "来ない" : arriveAt.ToString("F2"))}");
                        }
                    }
                }
            }
        });

        Assert.True(late.Count == 0, $"一塁送球 {total} 本のうち、受け手が球より遅れた例あり（最大 {worst:F2}s）: " + string.Join(" / ", late));
    }
}
