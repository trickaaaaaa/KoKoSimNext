using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 野手レッグの急ぎ度（issue fielder-leg-speed-by-distance）。急がない移動（塁カバー・バックアップ・
/// 余裕のあるフライ）は期限から逆算した必要速度で走り、捕りに行くレッグは従来どおり全力。表示専用。
/// </summary>
public sealed class FielderPaceTests
{
    private static readonly FieldGeometry Field = new();

    private static PlayTimeline Build(FieldingPlay play) => TimelineBuilder.BuildBattedBall(
        play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

    private static double Speed(PlayTimeline tl, FielderMove m)
    {
        var from = tl.FielderStarts is { } s && s.TryGetValue(m.Role, out var p) ? p : throw new InvalidOperationException();
        // 直前のレッグがあればその終端から
        var prev = tl.Moves.Where(x => x.Role == m.Role && x.T1 <= m.T0 + 1e-9).OrderBy(x => x.T1).LastOrDefault();
        var start = prev?.To ?? from;
        var d = Math.Sqrt(Math.Pow(m.To.X - start.X, 2) + Math.Pow(m.To.Z - start.Z, 2));
        return d / Math.Max(1e-9, m.T1 - m.T0);
    }

    private static FieldingPlay RoutineGrounderOut() => new()
    {
        Result = BattedBallResult.Out, IsFly = false,
        LandingX = -9.0, LandingZ = 30.0, HangTimeSeconds = 0.3, ApexHeightM = 0.4,
        RangeM = 31.3, BearingDeg = -16.0, FielderRole = FieldPosition.Shortstop,
        FieldedX = -8.0, FieldedZ = 36.0,
        FieldedAtSeconds = 1.4, ThrowArriveSeconds = 3.2, BatterToFirstSeconds = 4.2,
    };

    [Fact]
    public void FirstBaseman_CoveringFirst_JogsAndIsSetBeforeTheThrow()
    {
        var tl = Build(RoutineGrounderOut());
        var cover = Assert.Single(tl.Moves, m => m.Role == FieldPosition.FirstBase && m.Task == FielderTask.CoverBase);
        var sprint = Field.StandardAlignment().First(f => f.Position == FieldPosition.FirstBase).Attributes.SprintSpeedMps;
        var v = Speed(tl, cover);
        Assert.True(v < sprint * 0.6, $"塁カバーが {v:F2} m/s（脚力 {sprint:F2}）＝全力のまま");
        Assert.True(v >= 0.9 - 1e-9, $"塁カバーが {v:F2} m/s＝歩きより遅い");
        var throwArrive = tl.Ball.First(b => b.Kind == BallSegmentKind.Throw && b.ToBase == "first").T1;
        Assert.True(cover.T1 <= throwArrive - 0.4 + 1e-9, $"送球到達 {throwArrive:F2} に対しカバー完了 {cover.T1:F2}");
    }

    [Fact]
    public void BallFielder_OnAGrounder_StillSprints()
    {
        var tl = Build(RoutineGrounderOut());
        var leg = tl.Moves.Where(m => m.Role == FieldPosition.Shortstop && m.Task == FielderTask.FieldBall).OrderBy(m => m.T0).First();
        var sprint = Field.StandardAlignment().First(f => f.Position == FieldPosition.Shortstop).Attributes.SprintSpeedMps;
        Assert.InRange(Speed(tl, leg), sprint * 0.9, sprint * 1.05);
    }

    private static FieldingPlay EasyFly() => new()
    {
        Result = BattedBallResult.Out, IsFly = true,
        LandingX = 4.0, LandingZ = 78.0, HangTimeSeconds = 4.5, ApexHeightM = 22.0,
        RangeM = 78.1, BearingDeg = 3.0, FielderRole = FieldPosition.CenterField,
        FieldedAtSeconds = 4.5, BatterToFirstSeconds = 4.2, CatchMarginSeconds = 3.0,
    };

    [Fact]
    public void Outfielder_UnderAnEasyFly_JogsAndIsUnderItBeforeTheCatch()
    {
        var tl = Build(EasyFly());
        var leg = tl.Moves.Where(m => m.Role == FieldPosition.CenterField && m.Task == FielderTask.FieldBall).OrderBy(m => m.T0).First();
        var sprint = Field.StandardAlignment().First(f => f.Position == FieldPosition.CenterField).Attributes.SprintSpeedMps;
        var v = Speed(tl, leg);
        Assert.True(v < sprint * 0.6, $"余裕のあるフライへ {v:F2} m/s（脚力 {sprint:F2}）＝全力のまま");
        Assert.True(v >= 0.9 - 1e-9, $"{v:F2} m/s＝歩きより遅い（下限 0.9）");
        Assert.True(leg.T1 <= TimelineBuilder.ContactAt + 4.5 - 0.5 + 1e-9, $"捕球 0.5s 前に落下点に居ない（{leg.T1:F2}）");
        Assert.Equal(4.0, leg.To.X, 6);
        Assert.Equal(78.0, leg.To.Z, 6);
    }

    private static FieldingPlay HardFly() => EasyFly() with
    {
        LandingX = -20.0, LandingZ = 95.0, HangTimeSeconds = 3.2, FieldedAtSeconds = 3.2, RangeM = 97.1, BearingDeg = -11.9,
        CatchMarginSeconds = 0.05,
    };

    [Fact]
    public void Outfielder_OnAHardFly_StillSprints()
    {
        var tl = Build(HardFly());
        var leg = tl.Moves.Where(m => m.Role == FieldPosition.CenterField && m.Task == FielderTask.FieldBall).OrderBy(m => m.T0).First();
        var sprint = Field.StandardAlignment().First(f => f.Position == FieldPosition.CenterField).Attributes.SprintSpeedMps;
        Assert.InRange(Speed(tl, leg), sprint * 0.9, sprint * 1.05);
    }
}
