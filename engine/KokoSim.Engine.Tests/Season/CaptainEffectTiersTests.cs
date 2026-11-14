using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>主将指名画面の「スタメン」「ベンチ」段階（issue #861）。境界は #860 の式から引く。</summary>
public sealed class CaptainEffectTiersTests
{
    private static DevelopingPlayer Guy(int leadership, int mental, int grade = 3,
        Personality personality = Personality.Normal) => new()
    {
        Name = "X", Grade = grade, Leadership = leadership, Mental = mental,
        Receptiveness = PersonalityFixture.Axes(personality).Receptiveness,
        Diligence = PersonalityFixture.Axes(personality).Diligence,
        Selflessness = PersonalityFixture.Axes(personality).Selflessness,
    };

    [Fact]
    public void StarterScore_IsLeadershipPower()
    {
        Assert.Equal(72.0, CaptainEffectTiers.StarterScore(Guy(80, 90)), 6);
    }

    [Fact]
    public void StarterScore_PillarAmplifiesAndClampsAt100()
    {
        var p = Guy(80, 90);
        p.Skills = new SkillSet(new[] { Skill.SpiritualPillar });
        Assert.Equal(100.0, CaptainEffectTiers.StarterScore(p), 6);
        var q = Guy(50, 50);
        q.Skills = new SkillSet(new[] { Skill.SpiritualPillar });
        Assert.Equal(25.0 * new SkillCoefficients().SpiritualPillarCaptainFactor, CaptainEffectTiers.StarterScore(q), 6);
    }

    [Fact]
    public void BenchScore_MatchesCaptainMessengerFactor()
    {
        var c = new TacticsCoefficients();
        var pc = new PersonalityCoefficients();
        var p = Guy(90, 80);
        var f = (TimeoutMessenger.FactorOf(90, 80, 3, p.Selflessness, 0, true, true, c, pc)
               + TimeoutMessenger.FactorOf(90, 80, 3, p.Selflessness, 0, false, true, c, pc)) / 2.0;
        var expected = (f - c.MessengerFactorMin) / (c.CaptainMessengerFactorMax - c.MessengerFactorMin) * 100.0;
        Assert.Equal(expected, CaptainEffectTiers.BenchScore(p, c, pc), 6);
    }

    [Fact]
    public void FactorOf_AgreesWithFactor()
    {
        var c = new TacticsCoefficients();
        var pc = new PersonalityCoefficients();
        var messenger = new Player { Name = "M", Grade = 3, Leadership = 70, Mental = 60 };
        var receiver = new Player { Name = "R", Grade = 3 };
        Assert.Equal(
            TimeoutMessenger.Factor(messenger, receiver, true, c, pc, messenger),
            TimeoutMessenger.FactorOf(70, 60, 3, messenger.Selflessness, 3, true, true, c, pc), 9);
        Assert.Equal(
            TimeoutMessenger.Factor(messenger, receiver, false, c, pc),
            TimeoutMessenger.FactorOf(70, 60, 3, messenger.Selflessness, 3, false, false, c, pc), 9);
    }

    [Fact]
    public void Tiers_AreMonotoneInLeadershipAndSpanTheScale()
    {
        var low = Guy(0, 0);
        var high = Guy(100, 100);
        Assert.Equal(Tier.G, CaptainEffectTiers.Starter(low));
        Assert.Equal(Tier.S, CaptainEffectTiers.Starter(high));
        Assert.True(CaptainEffectTiers.Bench(low) < CaptainEffectTiers.Bench(high));
        Assert.True(CaptainEffectTiers.Bench(Guy(70, 70)) <= CaptainEffectTiers.Bench(Guy(90, 90)));
    }

    [Fact]
    public void StarterAndBench_CanDisagree()
    {
        // 精神力は高いが統率傾向が低い選手と、その逆で、2つの段階は別の物差しになる。
        var mentalOnly = Guy(40, 100);
        var leaderOnly = Guy(100, 40);
        Assert.Equal(CaptainEffectTiers.Starter(mentalOnly), CaptainEffectTiers.Starter(leaderOnly));
        Assert.NotEqual(CaptainEffectTiers.Bench(mentalOnly), CaptainEffectTiers.Bench(leaderOnly));
    }
}
