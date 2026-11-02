using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 満塁・内野前進のゴロで本塁封殺したあとの「本封→一封」（issue #741）。守備が投げたのは処理点→本塁の1本だけ
/// なので、打者の一塁アウトには捕手の一塁転送が要る。間に合わなければ打者は野選で一塁に生きる。
/// </summary>
public sealed class HomeForceRelayTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    // 内野前進の二塁手寄りのゴロ（処理点 (8,0,28)・1.3秒で捕球・肩50）。打者の一塁到達は引数。
    private static HomePlayContext InfieldIn(double batterToFirst)
        => new(Field,
            new HomePlaySituation(new Vector3D(8, 0, 28), 1.3, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.In, IsFly: false, BatterToFirstSeconds: batterToFirst);

    [Fact]
    public void BatterSafeProbability_FallsWithSlowerBatter()
    {
        var fast = BaserunningModel.HomeForceRelayBatterSafeProbability(InfieldIn(3.8), C);
        var slow = BaserunningModel.HomeForceRelayBatterSafeProbability(InfieldIn(5.5), C);
        Assert.True(fast > slow, $"快足 {fast:F2} ≤ 鈍足 {slow:F2}");
        Assert.True(fast > 0.5 && slow < 0.5, $"快足 {fast:F2} / 鈍足 {slow:F2}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HomeForceOut_BatterOutOnlyWhenRelayBeatsHim(int outsBefore)
    {
        var rng = new Xoshiro256Random(741);
        int homeForces = 0, fc = 0, dp = 0;
        foreach (var batterToFirst in new[] { 3.8, 4.3, 5.5 })
        {
            for (var i = 0; i < 3000; i++)
            {
                var batter = new Player { Speed = 50 };
                var r1 = new Player(); var r2 = new Player(); var r3 = new Player { Speed = 30, Baserunning = 30 };
                var bases = new BaseState { First = r1, Second = r2, Third = r3 };
                var (runs, extraOuts, _, safeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                    bases, PlateAppearanceResult.InPlayOut, batter, outsBefore, C, rng, collectMoves: true,
                    InfieldIn(batterToFirst));
                var force = moves.FirstOrDefault(m => m.Out && m.FromBase == 3 && m.ToBase == 4);
                if (force is null || moves.Any(m => m.Out && m.FromBase == 1 && m.ToBase == 2)) continue;

                homeForces++;
                Assert.True(force.HomeForceRelay);
                Assert.Equal(0, runs);
                Assert.Equal(1, extraOuts);
                if (safeOnFc)
                {
                    fc++;
                    // 野選: 打者は一塁、走者はフォースで押し上がって満塁のまま。
                    Assert.Same(batter, bases.First);
                    Assert.Same(r1, bases.Second);
                    Assert.Same(r2, bases.Third);
                }
                else
                {
                    dp++;
                    // 併殺（本封→一封）: 打者アウトは呼び出し側が計上する。一死からならチェンジ（塁は見ない）。
                    if (outsBefore + 1 + extraOuts >= 3) continue;
                    // 無死からは一塁が空き、走者は二・三塁。
                    Assert.Null(bases.First);
                    Assert.Same(r1, bases.Second);
                    Assert.Same(r2, bases.Third);
                }
            }
        }
        Assert.True(homeForces > 0, "本塁封殺が一度も起きない");
        Assert.True(fc > 0, "転送が間に合わない野選が一度も出ない（打者が無条件でアウトになっている）");
        Assert.True(dp > 0, "本封→一封の併殺が一度も出ない");
    }

    [Fact]
    public void TwoOuts_HomeForceEndsInningWithoutRelay()
    {
        var rng = new Xoshiro256Random(7412);
        for (var i = 0; i < 2000; i++)
        {
            var bases = new BaseState { First = new Player(), Second = new Player(), Third = new Player() };
            var (_, _, _, safeOnFc, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 2, C, rng, collectMoves: true, InfieldIn(3.8));
            Assert.False(safeOnFc);
            Assert.DoesNotContain(moves, m => m.HomeForceRelay);
        }
    }
}

/// <summary>本封→一封の絵（issue #741）: 本塁への送球のあとに捕手の一塁転送が描かれ、打者の判定が判定どおりになる。</summary>
public sealed class HomeForceRelayTimelineTests
{
    private static Engine.Match.Timeline.PlayTimeline Build(bool batterOut)
    {
        var field = new FieldGeometry();
        var play = KokoSim.Engine.Tests.Match.Fielding.FieldingPlayFixtures.InfieldGrounder(FieldPosition.SecondBase);
        var tl = Engine.Match.Timeline.TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: true,
            Engine.Match.Timeline.TimelineBuilder.DescribeResult(play));
        tl = Engine.Match.Timeline.TimelineBuilder.AppendBackHomeThrows(tl, play, field, homeArriveAnchor: 3.5, runnerOut: true);
        var batterArrive = tl.Runners.First(r => r.Label == "打").T1;
        return Engine.Match.Timeline.TimelineBuilder.AppendHomeForceRelay(tl, field, batterArrive, batterOut);
    }

    [Fact]
    public void DoublePlay_DrawsHomeToFirstRelay_AndCallsBatterOutAtItsArrival()
    {
        var tl = Build(batterOut: true);
        var throws = tl.Ball.Where(b => b.Kind == Engine.Match.Timeline.BallSegmentKind.Throw).ToList();
        var toHome = throws.Last(b => b.ToBase == "home");
        var relay = throws.Single(b => b.ToBase == "first");
        Assert.True(relay.From.X == 0 && relay.From.Z == 0, "一塁への送球が本塁から出ていない");
        Assert.True(relay.T0 > toHome.T1, "転送が本塁送球の到達より前に出ている");
        var call = tl.Judgements.Single(j => j.Label == "打" && j.Kind == Engine.Match.Timeline.JudgementKind.Out);
        Assert.Equal(relay.T1, call.T, 6);
    }

    [Fact]
    public void FieldersChoice_DrawsRelay_ButBatterIsSafe()
    {
        var tl = Build(batterOut: false);
        Assert.Contains(tl.Ball, b => b.Kind == Engine.Match.Timeline.BallSegmentKind.Throw && b.ToBase == "first");
        Assert.DoesNotContain(tl.Judgements, j => j.Label == "打" && j.Kind == Engine.Match.Timeline.JudgementKind.Out);
        Assert.False(tl.Runners.First(r => r.Label == "打").OutAtEnd);
    }
}
