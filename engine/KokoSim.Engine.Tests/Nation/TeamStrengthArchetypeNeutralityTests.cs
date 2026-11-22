using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 投手総合（TeamStrengthProfile.PitcherComposite）は球質タイプの上乗せを差し引いた地力で測る（issue #1302）。
/// 上乗せは試合の失点が型によらず揃うよう校正してあり合計は揃っていない（本格 +6・技巧 −2・軟投 −5）ので、
/// 差し引かないと同じ地力でも型で投手力ランクが動く。
/// </summary>
public sealed class TeamStrengthArchetypeNeutralityTests
{
    private static readonly TeamStrengthCoefficients C = new();
    private static readonly PitcherArchetypeCoefficients Ac = new();

    // 地力（型を当てる前）の4能力 → 型を当てた選手を作る。
    private static DevelopingPlayer Pitcher(PitcherArchetype? type, double v, double ctl, double st, double rk)
    {
        var (av, ac, ast, ar) = PitcherArchetypes.Apply(type, v, ctl, st, rk, Ac);
        var p = new DevelopingPlayer { Archetype = type };
        PitcherRoleResolver.SeedAptitudeForRole(p, true);
        p.SetLevel(AbilityKind.Velocity, (int)System.Math.Round(av));
        p.SetLevel(AbilityKind.Control, (int)System.Math.Round(ac));
        p.SetLevel(AbilityKind.Stamina, (int)System.Math.Round(ast));
        p.SetLevel(AbilityKind.PitchRank, (int)System.Math.Round(ar));
        return p;
    }

    [Theory]
    [InlineData(PitcherArchetype.Power)]
    [InlineData(PitcherArchetype.Finesse)]
    [InlineData(PitcherArchetype.SoftToss)]
    [InlineData(PitcherArchetype.Flamethrower)]
    [InlineData(PitcherArchetype.Pinpoint)]
    [InlineData(PitcherArchetype.BreakingBallArtist)]
    public void SameBaseStrength_GivesSamePitcherComposite_RegardlessOfArchetype(PitcherArchetype type)
    {
        var balanced = TeamStrengthProfile.PitcherComposite(Pitcher(PitcherArchetype.Balanced, 60, 60, 60, 60), C);
        var typed = TeamStrengthProfile.PitcherComposite(Pitcher(type, 60, 60, 60, 60), C);
        Assert.Equal(balanced, typed, 0.6); // 能力値の整数丸めぶんだけ許す
    }

    [Fact]
    public void NoArchetype_IsUnchanged()
    {
        var p = Pitcher(null, 60, 55, 50, 45);
        var expected = 60 * C.VelocityWeight + 55 * C.ControlWeight + 50 * C.StaminaWeight + 45 * C.PitchRankWeight;
        Assert.Equal(expected, TeamStrengthProfile.PitcherComposite(p, C), 1e-9);
    }
}
