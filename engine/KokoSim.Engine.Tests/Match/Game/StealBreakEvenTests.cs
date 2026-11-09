using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>盗塁AIの損益判断（issue #838）。</summary>
public sealed class StealBreakEvenTests
{
    private static Player P(int speed = 50, int steal = 50, int arm = 50, int lead = 50) => new()
    {
        Position = FieldPosition.Catcher, Speed = speed, Steal = steal, ArmStrength = arm, Lead = lead,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50, Fielding = 50, Catching = 50,
    };

    [Fact]
    public void ExpectedSuccess_MixesInTheChanceOfBeingRead()
    {
        var c = new BaserunningCoefficients();
        var runner = P(speed: 80, steal: 80);
        var catcher = P();
        var plain = StealResolver.SuccessProbability(runner, catcher, c);
        var read = StealResolver.SuccessProbability(runner, catcher, c, pitchout: true);
        var expected = StealResolver.ExpectedSuccessProbability(runner, catcher, P(), c);

        Assert.True(read < expected && expected < plain, $"読まれた {read:P1} < 期待 {expected:P1} < 読まれない {plain:P1}");
    }

    [Fact]
    public void ExpectedSuccess_DropsAgainstABetterReadingCatcher()
    {
        var c = new BaserunningCoefficients();
        var runner = P(speed: 80, steal: 80);
        var weak = StealResolver.ExpectedSuccessProbability(runner, P(lead: 20), P(), c);
        var strong = StealResolver.ExpectedSuccessProbability(runner, P(lead: 90), P(), c);
        Assert.True(strong < weak);
    }

    [Fact]
    public void StealAbilityAlone_RaisesTheChance()
    {
        // 「盗塁」能力だけ上げても見込みが動かなかった（issue #838 症状3）。
        var c = new BaserunningCoefficients();
        var low = StealResolver.ExpectedSuccessProbability(P(steal: 20), P(), P(), c);
        var high = StealResolver.ExpectedSuccessProbability(P(steal: 100), P(), P(), c);
        Assert.True(high - low > 0.15, $"盗塁20 {low:P1} → 100 {high:P1}");
    }

    [Fact]
    public void AttemptProbability_HasNoCliffAtTheBreakEven()
    {
        // 崖（閾値を跨いだ瞬間に満額）をなくした: 余裕0では0、少し上では少し、余裕が大きいと満額へ。
        var t = new TacticsCoefficients();
        double P(double m) => StandardTacticsBrain.StealAttemptProbability(m, t.StealProb, t.StealAttemptLogisticSteepness);
        Assert.Equal(0.0, P(-0.01));
        Assert.Equal(0.0, P(0.0));
        Assert.InRange(P(0.01), 0.0, t.StealProb * 0.3);
        var prev = 0.0;
        for (var m = 0.0; m <= 0.2; m += 0.01)
        {
            Assert.True(P(m) >= prev);
            prev = P(m);
        }
        Assert.True(P(0.2) > t.StealProb * 0.95);
    }

    [Fact]
    public void ExpectedSuccess_RisesSmoothlyWithRunnerSpeed()
    {
        var c = new BaserunningCoefficients();
        var prev = StealResolver.ExpectedSuccessProbability(P(speed: 20, steal: 70), P(), P(), c);
        for (var sp = 25; sp <= 100; sp += 5)
        {
            var e = StealResolver.ExpectedSuccessProbability(P(speed: sp, steal: 70), P(), P(), c);
            Assert.True(e > prev && e - prev < 0.06, $"走力{sp}: {prev:P1}→{e:P1}");
            prev = e;
        }
    }
}
