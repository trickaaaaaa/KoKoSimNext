using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>
/// コンタクト判定（設計書15 Phase E-2）の物理妥当性テスト。誘発変化合成量が空振り率を単調に上げること、
/// 無回転球相当（変化量≈0）が帯の最低水準になることを固定する。
/// </summary>
public sealed class ContactModelTests
{
    private static PitchPlan Plan(double velocityKmh = 145.0, double stuff = 0.0) => new()
    {
        Type = PitchType.Fastball,
        AimX = 0.0,
        AimY = 0.5,
        VelocityKmh = velocityKmh,
        Stuff = stuff,
    };

    [Fact]
    public void WhiffProbability_IncreasesMonotonically_WithBreakMagnitude()
    {
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var plan = Plan();

        var magnitudes = new[] { 0.0, 0.1, 0.2, 0.3, 0.4, 0.6, 0.8 };
        double? previous = null;
        foreach (var m in magnitudes)
        {
            var features = new PitchTrajectoryFeatures(m, 0.0, 0.45);
            var p = ContactModel.WhiffProbability(batter, plan, features, inZone: true, coeff);
            if (previous is not null)
            {
                Assert.True(p > previous, $"magnitude={m}: {p} should exceed previous {previous}");
            }
            previous = p;
        }
    }

    [Fact]
    public void WhiffProbability_AtZeroBreak_IsTheFloorOfTheCurve()
    {
        // 無回転球（誘発変化≈0）は帯の中で最も空振りしにくい水準になるはず。
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var plan = Plan();

        var zero = new PitchTrajectoryFeatures(0.0, 0.0, 0.45);
        var pZero = ContactModel.WhiffProbability(batter, plan, zero, inZone: true, coeff);

        foreach (var m in new[] { 0.1, 0.3, 0.5, 0.8 })
        {
            var features = new PitchTrajectoryFeatures(m, 0.0, 0.45);
            var p = ContactModel.WhiffProbability(batter, plan, features, inZone: true, coeff);
            Assert.True(p > pZero, $"breakMagnitude={m} の空振り率が無回転相当を上回らなければならない");
        }
    }

    [Fact]
    public void WhiffProbability_UsesCombinedVerticalAndHorizontalBreak()
    {
        // 縦横合成（設計書15 Phase E-2, ユーザー承認）: 縦だけ0.3mと横だけ0.3mは合成量が同じなので同確率。
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var plan = Plan();

        var verticalOnly = new PitchTrajectoryFeatures(0.3, 0.0, 0.45);
        var horizontalOnly = new PitchTrajectoryFeatures(0.0, 0.3, 0.45);

        var pVertical = ContactModel.WhiffProbability(batter, plan, verticalOnly, inZone: true, coeff);
        var pHorizontal = ContactModel.WhiffProbability(batter, plan, horizontalOnly, inZone: true, coeff);

        Assert.Equal(pVertical, pHorizontal, 9);
    }

    [Fact]
    public void WhiffProbability_OutOfZone_IsHarderToMakeContactWith()
    {
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var plan = Plan();
        var features = new PitchTrajectoryFeatures(0.3, 0.0, 0.45);

        var inZone = ContactModel.WhiffProbability(batter, plan, features, inZone: true, coeff);
        var outOfZone = ContactModel.WhiffProbability(batter, plan, features, inZone: false, coeff);

        Assert.True(outOfZone > inZone);
    }
}
