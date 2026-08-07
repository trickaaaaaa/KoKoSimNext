using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 帰塁の表示専用フラグ（issue #524）。<see cref="RunnerLeg.Returning"/> が牽制の走者レッグ
/// （<see cref="TimelineBuilder.BuildPickoff"/>、アウト/セーフ両方）でだけ true になり、
/// 盗塁（<see cref="TimelineBuilder.BuildSteal"/>）では既定の false のままであることを担保する。
/// 判定・帯には一切影響しない表示専用の契約テスト。
/// </summary>
public sealed class ReturningDisplayTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    [Fact]
    public void PickoffTimeline_PickedOff_IsReturning()
    {
        var runner = new Player { Speed = 70, Steal = 60 };
        var pitcher = new Player { Position = FieldPosition.Pitcher, ArmStrength = 55, Mental = 50 };
        var tl = TimelineBuilder.BuildPickoff(runner, pitcher, "first", pickedOff: true, C, Field);
        var leg = Assert.Single(tl.Runners);
        Assert.True(leg.Returning, "牽制死の走者レッグは帰塁のはず（頭から突っ込む到達演技を選ぶため）");
    }

    [Fact]
    public void PickoffTimeline_Safe_IsReturning()
    {
        var runner = new Player { Speed = 70, Steal = 60 };
        var pitcher = new Player { Position = FieldPosition.Pitcher, ArmStrength = 55, Mental = 50 };
        var tl = TimelineBuilder.BuildPickoff(runner, pitcher, "first", pickedOff: false, C, Field);
        var leg = Assert.Single(tl.Runners);
        Assert.True(leg.Returning, "牽制セーフの走者レッグも帰塁のはず（アウト/セーフで演技を分けない）");
    }

    [Fact]
    public void StealTimeline_CaughtStealing_IsNotReturning()
    {
        var runner = new Player { Speed = 80, Steal = 75 };
        var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 60 };
        var tl = TimelineBuilder.BuildSteal(runner, catcher, StealResult.CaughtStealing, C, Field);
        var leg = Assert.Single(tl.Runners);
        Assert.False(leg.Returning, "盗塁死は進塁レッグ（帰塁ではない）のはず");
    }

    [Fact]
    public void StealTimeline_Safe_IsNotReturning()
    {
        var runner = new Player { Speed = 80, Steal = 75 };
        var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = 60 };
        var tl = TimelineBuilder.BuildSteal(runner, catcher, StealResult.Safe, C, Field);
        var leg = Assert.Single(tl.Runners);
        Assert.False(leg.Returning, "盗塁成功は進塁レッグ（帰塁ではない）のはず");
    }
}
