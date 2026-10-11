using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #637: 本盗の成功見込みが、走者・捕手を問わず解決式の下限（1%）に張り付いていた。
/// 原因は「走者は二盗と同じ23.5m／守備は投手クイック＋タッチだけ（球の到達を数えない）」という土俵違い。
/// 走者を三塁からのリード込み距離、守備をワインドアップ＋球の到達に直した（2026-10-10 ユーザー決定）。
/// </summary>
public sealed class StealHomeModelTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly Player Catcher = new()
    {
        Position = FieldPosition.Catcher, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Runner(int lv) => new() { Speed = lv, Steal = lv };

    private static double P(int lv) => StealResolver.SuccessProbability(Runner(lv), Catcher, C, target: StealTarget.Home);

    /// <summary>
    /// 平均的な走者は下限すれすれ、俊足で数%＝ギャンブル枠。design-14「本盗は三盗よりさらに下方」を守る水準
    /// （2026-10-10 ユーザー決定）なので、三盗（走力90で約8%）より下に収まる。
    /// </summary>
    [Fact]
    public void SuccessProbability_LandsInGambleRange()
    {
        Assert.InRange(P(50), 0.012, 0.04);
        Assert.InRange(P(90), 0.04, 0.10);
        Assert.True(P(90) < StealResolver.SuccessProbability(Runner(90), Catcher, C), "本盗が二盗より易しくなっている");
        Assert.True(P(90) < StealResolver.SuccessProbability(Runner(90), Catcher, C, target: StealTarget.Third),
            "本盗が三盗より易しくなっている（design-14）");
    }

    /// <summary>走力で単調に上がる（旧実装はどの走者も下限で同値＝走力が効いていなかった）。</summary>
    [Fact]
    public void FasterRunner_HasBetterChance()
    {
        var prev = 0.0;
        foreach (var lv in new[] { 30, 50, 70, 90 })
        {
            var p = P(lv);
            Assert.True(p > prev, $"走力{lv}: {p:F3} ≦ {prev:F3}");
            if (lv >= 50) Assert.True(p > 0.0101, $"走力{lv}: 下限1%に張り付いている（{p:F4}）");
            prev = p;
        }
    }

    /// <summary>守備側は「ワインドアップ＋球の到達＋タッチ」＝捕手の肩は関係しない（送球区間が無い）。</summary>
    [Fact]
    public void DefenseTime_IsDeliveryPlusTag_IndependentOfCatcherArm()
    {
        var weak = Catcher with { ArmStrength = 20 };
        var strong = Catcher with { ArmStrength = 90 };
        var expected = C.StealHomeDeliverySeconds + C.HomeTagSeconds;
        Assert.Equal(expected, StealResolver.DefenseTimeSeconds(weak, C, target: StealTarget.Home), 9);
        Assert.Equal(expected, StealResolver.DefenseTimeSeconds(strong, C, target: StealTarget.Home), 9);
    }

    /// <summary>走者側は本盗だけ専用距離（二盗・三盗は従来の 23.5m のまま＝帯不変）。</summary>
    [Fact]
    public void RunnerTime_UsesHomeDistanceOnlyForHome()
    {
        var r = Runner(50);
        var second = StealResolver.RunnerTimeSeconds(r, C, target: StealTarget.Second);
        var third = StealResolver.RunnerTimeSeconds(r, C, target: StealTarget.Third);
        var home = StealResolver.RunnerTimeSeconds(r, C, target: StealTarget.Home);
        Assert.Equal(second, third, 9);
        var sprint = r.ToFielder(C.AbilityEffect).SprintSpeedMps;
        Assert.Equal((C.StealLeadDistanceM - C.StealHomeDistanceM) / sprint, second - home, 9);
    }
}
