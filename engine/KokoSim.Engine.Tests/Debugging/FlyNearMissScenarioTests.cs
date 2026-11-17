using System;
using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 「外野手が背走して、あと一歩届かない」場面のシナリオ（issue #1075 追記3・data/debug/scenarios.yaml の
/// fly-near-miss-back-lab）が、<b>最初の1打席でその打球を出す</b>こと。
/// 3Dの「届かない」芝居（背走ジャンプ）は背走のときにしか出ず、自然な試合ではほとんど現れない
/// （届かない打球の大半は前進・横）＝撮影はこのシナリオで場面を作る。
/// 係数変更で打球が動いたら、このテストが赤くなる→シードを選び直す。
/// </summary>
public sealed class FlyNearMissScenarioTests
{
    [Fact]
    public void FirstPlateAppearance_IsAFlyThatJustClearsTheOutfielder()
    {
        Assert.True(DebugScenarioLoader.LoadFromRepoOrEmpty().TryGet("fly-near-miss-back-lab", out var def),
            "fly-near-miss-back-lab が無い");
        Assert.Null(def.Force);   // 強制発動は打球の軌道を作らない

        var built = ScenarioBuilder.Build(def, new GameContext { CaptureTimelines = true, CapturePitches = true }, 1UL);
        var prog = new MatchProgression(built.Away, built.Home, built.Ctx, built.Seed, built.Start);
        Assert.True(prog.Advance(), "最初の打席が確定しない");

        var tl = prog.PlayLog[^1].Timeline;
        Assert.NotNull(tl);
        // 3D の「届かない」芝居が出る幅（DuelStageRig.MissMarginSeconds = 0.50）の内側で、捕れていない
        Assert.True(tl!.CatchMarginSeconds is { } m && m < 0 && m > -0.50,
            $"捕球余裕が「あと一歩届かない」幅に無い（{tl.CatchMarginSeconds}）");

        // 外野手の頭を越えた（＝背走で追う）打球であること
        var flight = tl.Ball.First(b => b.Kind == BallSegmentKind.Flight);
        var land = flight.To;
        var landDist = Math.Sqrt(land.X * land.X + land.Z * land.Z);
        var nearest = new[] { FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField }
            .Select(p => tl.FielderStarts![p])
            .OrderBy(s => (s.X - land.X) * (s.X - land.X) + (s.Z - land.Z) * (s.Z - land.Z))
            .First();
        var startDist = Math.Sqrt(nearest.X * nearest.X + nearest.Z * nearest.Z);
        Assert.True(landDist > startDist + 6.0,
            $"外野手の頭を越えていない（落下点 {landDist:F1}m・定位置 {startDist:F1}m）");
    }
}
