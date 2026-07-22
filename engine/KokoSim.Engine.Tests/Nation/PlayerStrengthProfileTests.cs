using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 選手個人のカテゴリ別ランク（打撃力/走力/守備力/投手力, Issue #30・2026-07-22 owner決定 候補A）。
/// 全能力を同一レベルに揃えた選手を使い、各合成値＝そのレベルになる性質で既知値照合する。
/// </summary>
public sealed class PlayerStrengthProfileTests
{
    private static readonly TeamStrengthCoefficients C = new();

    private static DevelopingPlayer Make(int level, bool pitcher)
    {
        var p = new DevelopingPlayer { IsPitcher = pitcher };
        foreach (var k in AbilityKinds.All) p.SetLevel(k, level);
        return p;
    }

    [Fact]
    public void UniformBatter_AllFourCategoriesEqualLevel()
    {
        // サブ重みは各カテゴリ合計1.0なので、全能力を level に揃えれば合成値＝level になる。
        var s = PlayerStrengthProfile.Compute(Make(72, pitcher: false), C);
        Assert.Equal(72, s.Batting, 3);
        Assert.Equal(72, s.Mobility, 3);
        Assert.Equal(72, s.Defense, 3);
        Assert.Equal(72, s.Pitching, 3); // 候補A: 野手でも投手力を計算する（余技能力値から）
    }

    [Fact]
    public void UniformPitcher_AllFourCategoriesEqualLevel()
    {
        var s = PlayerStrengthProfile.Compute(Make(65, pitcher: true), C);
        Assert.Equal(65, s.Batting, 3); // 候補A: 投手でも打撃力を計算する（余技能力値から）
        Assert.Equal(65, s.Mobility, 3);
        Assert.Equal(65, s.Defense, 3);
        Assert.Equal(65, s.Pitching, 3);
    }

    [Fact]
    public void Batting_WeightsContactAndPowerAboveLaunchAndDiscipline()
    {
        var p = new DevelopingPlayer();
        p.SetLevel(AbilityKind.Contact, 90);
        p.SetLevel(AbilityKind.Power, 90);
        p.SetLevel(AbilityKind.LaunchTendency, 10);
        p.SetLevel(AbilityKind.Discipline, 10);

        var s = PlayerStrengthProfile.Compute(p, C);
        var expected = 90 * C.ContactWeight + 90 * C.PowerWeight
            + 10 * C.LaunchWeight + 10 * C.DisciplineWeight;
        Assert.Equal(expected, s.Batting, 3);
    }

    [Fact]
    public void Pitching_WeightsVelocityMost()
    {
        var p = new DevelopingPlayer { IsPitcher = true };
        p.SetLevel(AbilityKind.Velocity, 90);
        p.SetLevel(AbilityKind.Control, 40);
        p.SetLevel(AbilityKind.Stamina, 40);
        p.SetLevel(AbilityKind.PitchRank, 40);

        var s = PlayerStrengthProfile.Compute(p, C);
        var expected = 90 * C.VelocityWeight + 40 * C.ControlWeight
            + 40 * C.StaminaWeight + 40 * C.PitchRankWeight;
        Assert.Equal(expected, s.Pitching, 3);
    }

    [Fact]
    public void Tiers_FollowSameBandsAsTeamStrength()
    {
        var s = PlayerStrengthProfile.Compute(Make(95, pitcher: false), C);
        Assert.Equal(Tier.S, s.BattingTier);
        Assert.Equal(Tier.S, s.MobilityTier);
        Assert.Equal(Tier.S, s.DefenseTier);
        Assert.Equal(Tier.S, s.PitchingTier);

        var g = PlayerStrengthProfile.Compute(Make(5, pitcher: false), C);
        Assert.Equal(Tier.G, g.BattingTier);
    }

    [Fact]
    public void Deterministic_SamePlayerSameResult()
    {
        var p = Make(58, pitcher: true);
        var a = PlayerStrengthProfile.Compute(p, C);
        var b = PlayerStrengthProfile.Compute(p, C);
        Assert.Equal(a.Batting, b.Batting, 6);
        Assert.Equal(a.Pitching, b.Pitching, 6);
    }
}
