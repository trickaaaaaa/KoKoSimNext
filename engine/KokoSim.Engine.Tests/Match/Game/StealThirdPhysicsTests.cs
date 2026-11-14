using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 三盗を物理だけで決める（issue #949・design-14 P1-4）。判定だけに効く補正（旧 <c>StealThirdSuccessBias</c> −0.35）を
/// 外し、二塁走者のリード（<see cref="BaserunningCoefficients.StealThirdLeadM"/>）と本塁→三塁の送球距離の勝負にした。
/// </summary>
public sealed class StealThirdPhysicsTests
{
    private readonly ITestOutputHelper _out;
    public StealThirdPhysicsTests(ITestOutputHelper output) => _out = output;

    private static readonly BaserunningCoefficients C = new();

    private static Player Runner(int legs) => new() { Speed = legs, Steal = legs };
    private static Player Catcher(int arm) => new() { Position = FieldPosition.Catcher, ArmStrength = arm, Fielding = 50 };

    /// <summary>
    /// 同じ走力・同じ捕手なら、三盗の成功率は二盗より<b>少し下</b>（2026-11-14 監督決定）。
    /// 「少し」＝二盗を上回らず、差は12ポイント以内。
    /// </summary>
    [Fact]
    public void ThirdSteal_IsSlightlyBelowSecond_AtSameRunnerAndCatcher()
    {
        foreach (var legs in new[] { 50, 70, 90 })
            foreach (var arm in new[] { 30, 50, 70, 90 })
            {
                var p2 = StealResolver.SuccessProbability(Runner(legs), Catcher(arm), C);
                var p3 = StealResolver.SuccessProbability(Runner(legs), Catcher(arm), C, target: StealTarget.Third);
                _out.WriteLine($"走力{legs} 肩{arm}: 二盗 {p2:P1} / 三盗 {p3:P1}");
                Assert.True(p3 < p2, $"走力{legs} 肩{arm}: 三盗 {p3:P1} が二盗 {p2:P1} 以上");
                Assert.True(p2 - p3 <= 0.12, $"走力{legs} 肩{arm}: 三盗 {p3:P1} が二盗 {p2:P1} より下がりすぎ");
            }
    }

    /// <summary>三盗の成功見込みは、走者と守備の秒の差だけで決まる（判定だけに効く補正が無い）。</summary>
    [Fact]
    public void ThirdSteal_ProbabilityIsPurelyTheTimeRace()
    {
        foreach (var legs in new[] { 30, 60, 90 })
            foreach (var arm in new[] { 30, 60, 90 })
            {
                var runner = Runner(legs);
                var catcher = Catcher(arm);
                var margin = StealResolver.DefenseTimeSeconds(catcher, C, target: StealTarget.Third)
                             - StealResolver.RunnerTimeSeconds(runner, C, target: StealTarget.Third);
                var expected = MathUtil.Clamp(MathUtil.Logistic(margin / C.StealMarginScale), 0.01, 0.99);
                Assert.Equal(expected, StealResolver.SuccessProbability(runner, catcher, C, target: StealTarget.Third), 12);
            }
    }

    /// <summary>三盗の走る距離は、塁間から二塁走者のリードを引いた分（二盗の距離の流用ではない）。</summary>
    [Fact]
    public void ThirdSteal_RunsBasePathMinusSecondBaseLead()
    {
        var runner = Runner(60);
        var speed = runner.ToFielder(C.AbilityEffect).SprintSpeedMps;
        var second = StealResolver.RunnerTimeSeconds(runner, C);
        var third = StealResolver.RunnerTimeSeconds(runner, C, target: StealTarget.Third);
        Assert.Equal((C.StealLeadDistanceM - (StealResolver.BasePathM - C.StealThirdLeadM)) / speed, second - third, 9);
        Assert.Equal(StealResolver.BasePathM, new FieldGeometry().BaseDistanceM, 9);
    }

    /// <summary>
    /// 三盗で判定と絵の食い違い（＝秒を結果へ寄せ直した回）が0件（二盗と同じ）。寄せ直しが正当なのは
    /// 確率のクランプ（1%/99%）が効く極端な場面と、悪送球（無条件セーフ）だけ。補正 −0.35 があった頃は
    /// 三盗の平均17%で寄せ直していた（OPEN-QUESTIONS Q28）。
    /// </summary>
    [Fact]
    public void ThirdSteal_VerdictNeverNeedsNudgingToMatchThePicture()
    {
        var rng = new Xoshiro256Random(949);
        foreach (var target in new[] { StealTarget.Second, StealTarget.Third })
        {
            int plays = 0, nudged = 0;
            foreach (var legs in new[] { 20, 40, 60, 80, 100 })
                foreach (var arm in new[] { 20, 50, 80, 100 })
                {
                    var runner = Runner(legs);
                    var catcher = Catcher(arm);
                    var p = StealResolver.SuccessProbability(runner, catcher, C, target: target);
                    for (var i = 0; i < 500; i++)
                    {
                        var v = StealResolver.ResolveVerdict(runner, catcher, C, rng, target: target);
                        var wild = v.ThrowScatter is { } sc && ThrowScatterModel.IsWild(sc, C.StealThrowWildThresholdCm);
                        Assert.Equal(v.Safe, v.RunnerSeconds < v.DefenseSeconds);
                        if (wild || p <= 0.011 || p >= 0.989) continue;   // 寄せ直しが正当な回
                        plays++;
                        if (v.NudgedToMatchCall) nudged++;
                    }
                }
            _out.WriteLine($"{target}: {plays} 回中 寄せ直し {nudged} 回");
            Assert.True(plays > 5000, $"{target}: 母集団が薄い（{plays}）");
            Assert.Equal(0, nudged);
        }
    }
}
