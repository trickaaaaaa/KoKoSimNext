using KokoSim.Engine.Match.Tactics;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 本塁への送り判定（設計書12 §3/§4, F2 Slice B）。三塁コーチの「還す/自重」。
/// 純関数＝生還推定確率と状況(2アウト)・aggression(采配/校風)で閾値が動く。
/// </summary>
public sealed class HomeSendDecisionTests
{
    private static readonly TacticsCoefficients C = new();
    private const double Neutral = 0.5;

    [Fact]
    public void NeutralCoach_SendsClearlySafe_HoldsClearlyDead()
    {
        Assert.True(HomeSendDecision.ShouldSend(0.90, outs: 0, Neutral, C));   // 余裕の生還は還す
        Assert.False(HomeSendDecision.ShouldSend(0.10, outs: 0, Neutral, C));  // 刺されるなら自重
    }

    [Fact]
    public void NeutralThreshold_EqualsMinSuccess_AtZeroOuts()
    {
        Assert.Equal(C.SendHomeMinSuccess, HomeSendDecision.SendThreshold(0, Neutral, C), 9);
    }

    [Fact]
    public void TwoOuts_RelaxesThreshold_SendsMarginalPlayThatWouldBeHeld()
    {
        // 生還見込み0.35: 0アウトでは自重、2アウトでは還す（失うものが少ない）。
        Assert.False(HomeSendDecision.ShouldSend(0.35, outs: 0, Neutral, C));
        Assert.True(HomeSendDecision.ShouldSend(0.35, outs: 2, Neutral, C));
        Assert.True(
            HomeSendDecision.SendThreshold(2, Neutral, C) < HomeSendDecision.SendThreshold(0, Neutral, C));
    }

    [Fact]
    public void AggressiveCoach_SendsMarginal_ThatConservativeHolds()
    {
        // 同じ際どい打球(生還0.40)を、積極采配は還し、慎重采配は止める。
        Assert.True(HomeSendDecision.ShouldSend(0.40, outs: 0, aggression: 0.9, C));
        Assert.False(HomeSendDecision.ShouldSend(0.40, outs: 0, aggression: 0.1, C));
    }

    [Fact]
    public void HigherAggression_LowersThreshold_Monotonically()
    {
        var cautious = HomeSendDecision.SendThreshold(0, 0.2, C);
        var neutral = HomeSendDecision.SendThreshold(0, 0.5, C);
        var aggressive = HomeSendDecision.SendThreshold(0, 0.8, C);
        Assert.True(cautious > neutral && neutral > aggressive, $"{cautious} > {neutral} > {aggressive}");
    }

    [Fact]
    public void Threshold_IsClampedWithinBounds_ForExtremeInputs()
    {
        Assert.InRange(HomeSendDecision.SendThreshold(2, aggression: 2.0, C), 0.05, 0.95);   // 超積極×2アウト＝下限
        Assert.InRange(HomeSendDecision.SendThreshold(0, aggression: -1.0, C), 0.05, 0.95);  // 超慎重＝上限
        Assert.Equal(0.05, HomeSendDecision.SendThreshold(2, aggression: 2.0, C), 9);
    }
}
