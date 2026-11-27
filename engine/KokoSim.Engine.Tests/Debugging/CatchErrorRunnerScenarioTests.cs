using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 走者一塁・内野ゴロのお手玉（issue #1733。data/debug/scenarios.yaml の catch-error-runner-lab）。判定は「拾い直しても一塁は間に合わない」と
/// 決めて打者を一塁に生かしている。絵の拾い直したあとの送球は、判定が払った手間ぶん構えてから先の塁へ投げる＝同じ時計で
/// 一塁へ投げても打者の到達に間に合わない。係数変更で打球が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class CatchErrorRunnerScenarioTests
{
    private static readonly FieldGeometry Field = new();

    [Fact]
    public void AfterTheFumble_TheThrowToTheLeadBaseWaitsForTheHandling_SoFirstWouldNotHaveBeenInTime()
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet("catch-error-runner-lab", out var def));
        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        prog.ForceNext(def.ResolveForce());
        Assert.True(prog.Advance(), "最初の打席が確定しない");

        var entry = prog.PlayLog[^1];
        Assert.Equal(FieldingErrorKind.Catch, entry.ErrorKind);
        var tl = Assert.IsType<PlayTimeline>(entry.Timeline);
        var pickAt = Assert.NotNull(tl.ErrorPickupAt);
        var pickPoint = Assert.NotNull(tl.ErrorPickupPoint);

        var toLead = tl.Ball.First(b => b.Kind == BallSegmentKind.Throw && b.T0 >= pickAt - 1e-6);
        Assert.Equal("second", toLead.ToBase);
        var flightSeconds = toLead.T1 - toLead.T0;
        var distM = System.Math.Sqrt(
            (toLead.To.X - toLead.From.X) * (toLead.To.X - toLead.From.X) + (toLead.To.Z - toLead.From.Z) * (toLead.To.Z - toLead.From.Z));
        var armMps = distM / flightSeconds;

        var toFirstM = System.Math.Sqrt(
            (pickPoint.X - Field.FirstBase.X) * (pickPoint.X - Field.FirstBase.X) + (pickPoint.Z - Field.FirstBase.Z) * (pickPoint.Z - Field.FirstBase.Z));
        var firstWouldArriveAt = toLead.T0 + toFirstM / armMps;
        var batterArrive = tl.Runners.First(l => l.Label == "打").T1;
        // 拾った直後に投げる絵（修正前）は打者に +0.04s まで迫り、一塁へ投げれば際どく間に合って見えた。手間を払うと +0.3s 以上遅れる
        Assert.True(firstWouldArriveAt > batterArrive + 0.2,
            $"拾い直して一塁へ投げれば間に合って見える絵（{firstWouldArriveAt:F2}s ＜ 打者 {batterArrive:F2}s + 0.2）");
    }
}
