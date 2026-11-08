using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// こぼれ球を拾った後の送球（issue #809・表示専用）。
/// ・拾った時点でまだ走っている走者がいる → 最初の送球は先頭走者の向かう塁へ（投手ではない）。
///   セーフの走者なので、球は走者の到達より後に着く（判定と矛盾させない）
/// ・投手へ返すのは全走者が止まってから（受け手が持ち直した後）
/// ・全員止まっていれば従来どおり投手へ（投手不在なら二塁へ）
/// </summary>
public sealed class ErrorPickupThrowTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly TimelinePoint Mound = new(0, 18.44);

    private static PlayTimeline Build(FieldingPlay play)
        => TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

    private static TimelinePoint Base(string name) => name switch
    {
        "first" => new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z),
        "second" => new TimelinePoint(Field.SecondBase.X, Field.SecondBase.Z),
        _ => new TimelinePoint(Field.ThirdBase.X, Field.ThirdBase.Z),
    };

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static FieldingPlay ShortstopBobble()
        => FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Catch };

    [Fact]
    public void BatterStillRunning_FirstThrowGoesToFirst_ThenBackToThePitcherAfterEveryoneStops()
    {
        var play = ShortstopBobble();
        var before = Build(play);
        var pick = before.ErrorPickupAt!.Value;
        var batterArrive = before.Runners.First(r => r.Label == "打").T1;
        Assert.True(batterArrive > pick, "前提: 拾った時点で打者はまだ一塁へ走っている");

        var tl = TimelineBuilder.AppendErrorReturnThrow(before, play, Field);
        var added = tl.Ball.Skip(before.Ball.Count).ToList();
        Assert.Equal(2, added.Count);

        var toFirst = added[0];
        Assert.Equal("first", toFirst.ToBase);
        Assert.Equal(FieldPosition.FirstBase, toFirst.ToRole);
        Assert.NotEqual(FieldPosition.Pitcher, toFirst.ToRole);
        Assert.True(toFirst.T0 >= pick, "拾う前に投げている");
        Assert.True(toFirst.T1 > batterArrive, "セーフの打者より先に球が着いている（判定と矛盾）");

        var back = added[1];
        Assert.Equal(FieldPosition.Pitcher, back.ToRole);
        Assert.True(Dist(back.To, Mound) < 1.0);
        Assert.Equal(0.0, Dist(back.From, Base("first")), 6);
        var allStopped = tl.Runners.Max(r => r.T1);
        Assert.True(back.T0 > allStopped, $"走者が止まる前（{allStopped:F2}s）に投手へ返している（{back.T0:F2}s）");
        Assert.True(back.T0 > toFirst.T1, "受け手が捕る前に投げ返している");
        Assert.True(tl.Duration >= back.T1);
    }

    /// <summary>先の塁へ走っている走者がいれば、その塁（いちばん先）へ投げる。</summary>
    [Fact]
    public void LeadRunnerHeadingToThird_FirstThrowGoesToThird()
    {
        var play = ShortstopBobble();
        var before = Build(play);
        var pick = before.ErrorPickupAt!.Value;
        var runner = new RunnerLeg
        {
            Label = "走1", T0 = TimelineBuilder.ContactAt + 0.1, T1 = pick + 1.5,
            From = Base("second"), To = Base("third"),
        };
        before = before with { Runners = before.Runners.Append(runner).ToList() };

        var tl = TimelineBuilder.AppendErrorReturnThrow(before, play, Field);
        var first = tl.Ball.Skip(before.Ball.Count).First();
        Assert.Equal("third", first.ToBase);
        Assert.True(first.T1 > runner.T1, "セーフの走者より先に球が着いている");
        var back = tl.Ball.Skip(before.Ball.Count).Last();
        Assert.True(back.T0 > tl.Runners.Max(r => r.T1), "走者が止まる前に投手へ返している");
    }

    /// <summary>拾った時点で全員止まっている → 従来どおり1本で投手へ返す。</summary>
    [Fact]
    public void EveryoneAlreadyStopped_SingleThrowToThePitcher()
    {
        var play = ShortstopBobble();
        var before = Build(play);
        var pick = before.ErrorPickupAt!.Value;
        // 打者が拾う前に一塁へ着いていた形（駆け抜けも含めて止まっている）
        before = before with
        {
            Runners = before.Runners.Select(r => r with { T0 = System.Math.Min(r.T0, pick - 1.0), T1 = System.Math.Min(r.T1, pick - 0.5) }).ToList(),
        };

        var tl = TimelineBuilder.AppendErrorReturnThrow(before, play, Field);
        var added = tl.Ball.Skip(before.Ball.Count).ToList();
        var only = Assert.Single(added);
        Assert.Equal(FieldPosition.Pitcher, only.ToRole);
        Assert.True(only.T0 >= pick);
    }
}
